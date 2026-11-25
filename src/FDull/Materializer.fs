namespace FDull.Transport

open System
open System.Reflection
open System.Text.Json
open Microsoft.FSharp.Reflection

/// Serialization boundary for FDull's data protocols. Reflection
/// inspects DTO metadata; it never constructs a private domain representation.
module Materializer =
    let private scalars =
        [ typeof<string>
          typeof<bool>
          typeof<char>
          typeof<byte>
          typeof<sbyte>
          typeof<int16>
          typeof<uint16>
          typeof<int>
          typeof<uint32>
          typeof<int64>
          typeof<uint64>
          typeof<single>
          typeof<double>
          typeof<decimal>
          typeof<Guid>
          typeof<DateTime>
          typeof<DateTimeOffset> ]

    let private container (target: Type) =
        if target.IsGenericType then
            let definition = target.GetGenericTypeDefinition()

            if definition = typedefof<option<obj>> then
                Some "optional"
            elif definition = typedefof<list<obj>> || definition = typedefof<Set<int>> then
                Some "array"
            elif definition = typedefof<Map<int, obj>> then
                Some "map"
            else
                None
        else
            None

    let validateType (target: Type) =
        let rec supported depth visited (target: Type) =
            if depth > 64 then
                false
            elif List.contains target visited then
                true
            elif List.contains target scalars || target = typeof<unit> then
                true
            else
                let nested = supported (depth + 1) (target :: visited)

                match container target with
                | Some _ -> target.GetGenericArguments() |> Array.forall nested
                | None when
                    FSharpType.IsRecord target
                    && target.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance).Length > 0
                    ->
                    FSharpType.GetRecordFields target
                    |> Array.forall (fun field -> nested field.PropertyType)
                | None -> false

        supported 0 [] target

    // Reflection over a DTO type is slow, and a policy holds thousands of records of a few types, so
    // the check of each type is prepared once. A prepared check looks its nested checks up when it
    // runs, which lets a type that contains itself be prepared.
    let private checks =
        System.Collections.Concurrent.ConcurrentDictionary<Type, int -> JsonElement -> bool>()

    let rec private check (target: Type) : int -> JsonElement -> bool =
        checks.GetOrAdd(target, Func<Type, int -> JsonElement -> bool> prepare)

    and private prepare (target: Type) : int -> JsonElement -> bool =
        if target = typeof<unit> then
            let valid (depth: int) (value: JsonElement) =
                depth <= 64 && value.ValueKind = JsonValueKind.Null

            valid
        else
            match container target with
            | Some "optional" ->
                let item = target.GenericTypeArguments[0]

                let valid (depth: int) (value: JsonElement) =
                    depth <= 64
                    && (value.ValueKind = JsonValueKind.Null || check item (depth + 1) value)

                valid
            | Some "array" ->
                let item = target.GenericTypeArguments[0]

                let valid (depth: int) (value: JsonElement) =
                    depth <= 64
                    && value.ValueKind = JsonValueKind.Array
                    && value.EnumerateArray() |> Seq.forall (check item (depth + 1))

                valid
            | Some "map" ->
                let item = target.GenericTypeArguments[1]

                let valid (depth: int) (value: JsonElement) =
                    depth <= 64
                    && value.ValueKind = JsonValueKind.Object
                    && value.EnumerateObject()
                       |> Seq.forall (fun property -> check item (depth + 1) property.Value)

                valid
            | Some _ ->
                let valid (_: int) (_: JsonElement) = false
                valid
            | None when FSharpType.IsRecord target ->
                let fields =
                    FSharpType.GetRecordFields target
                    |> Array.map (fun field ->
                        field.Name, field.PropertyType, container field.PropertyType = Some "optional")

                let valid (depth: int) (value: JsonElement) =
                    depth <= 64
                    && value.ValueKind = JsonValueKind.Object
                    && fields
                       |> Array.forall (fun (name, fieldType, optional) ->
                           match value.TryGetProperty name with
                           | true, property -> check fieldType (depth + 1) property
                           | false, _ -> optional)

                valid
            | None ->
                let valid (depth: int) (value: JsonElement) =
                    depth <= 64
                    && value.ValueKind <> JsonValueKind.Null
                    && value.ValueKind <> JsonValueKind.Undefined

                valid

    let private validValue target (value: JsonElement) = check target 0 value

    let decodeType (target: Type) (options: JsonSerializerOptions) (json: string) =
        if not (validateType target) then
            raise (JsonException "DATA001: The materialization target is not an approved DTO shape.")

        use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 64))

        if not (validValue target document.RootElement) then
            raise (JsonException "DATA004: A DTO contains missing or null required data.")

        JsonSerializer.Deserialize(json, target, options)

    let decode<'a> options json : 'a =
        decodeType typeof<'a> options json |> unbox<'a>
