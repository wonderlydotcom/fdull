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

    let private validValue target (value: JsonElement) =
        let rec valid depth (target: Type) (value: JsonElement) =
            if depth > 64 then
                false
            elif target = typeof<unit> then
                value.ValueKind = JsonValueKind.Null
            else
                match container target with
                | Some "optional" ->
                    value.ValueKind = JsonValueKind.Null
                    || valid (depth + 1) target.GenericTypeArguments[0] value
                | Some "array" ->
                    value.ValueKind = JsonValueKind.Array
                    && value.EnumerateArray()
                       |> Seq.forall (valid (depth + 1) target.GenericTypeArguments[0])
                | Some "map" ->
                    value.ValueKind = JsonValueKind.Object
                    && value.EnumerateObject()
                       |> Seq.forall (fun property -> valid (depth + 1) target.GenericTypeArguments[1] property.Value)
                | Some _ -> false
                | None when FSharpType.IsRecord target ->
                    value.ValueKind = JsonValueKind.Object
                    && FSharpType.GetRecordFields target
                       |> Array.forall (fun field ->
                           match value.TryGetProperty field.Name with
                           | true, property -> valid (depth + 1) field.PropertyType property
                           | false, _ -> container field.PropertyType = Some "optional")
                | None ->
                    value.ValueKind <> JsonValueKind.Null
                    && value.ValueKind <> JsonValueKind.Undefined

        valid 0 target value

    let decodeType (target: Type) (options: JsonSerializerOptions) (json: string) =
        if not (validateType target) then
            raise (JsonException "DATA001: The materialization target is not an approved DTO shape.")

        use document = JsonDocument.Parse(json, JsonDocumentOptions(MaxDepth = 64))

        if not (validValue target document.RootElement) then
            raise (JsonException "DATA004: A DTO contains missing or null required data.")

        JsonSerializer.Deserialize(json, target, options)

    let decode<'a> options json : 'a =
        decodeType typeof<'a> options json |> unbox<'a>
