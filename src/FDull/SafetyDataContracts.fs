namespace FDull

open FSharp.Compiler.Symbols

/// Data approvals are structural. Declaration capabilities cannot bypass this walk.
module internal SafetyDataContracts =
    let inspect (budget: SafetyBudget) pinned (root: FSharpType) =
        let rec walk depth (visited: Set<string>) path (t: FSharpType) =
            budget.Visit depth

            if depth > SafetyLimits.typeDepth then
                invalidOp "BUILD003: Data contract validation exceeded the supported type depth."

            if t.IsUnresolved then
                invalidOp "BUILD003: A data contract payload is unresolved."
            elif t.IsAbbreviation then
                walk (depth + 1) visited path t.AbbreviatedType
            elif t.HasNullAnnotation then
                [ "NULL005", path, "Nullable payloads require normalization before admission as data." ]
            elif t.IsGenericParameter then
                []
            elif t.IsFunctionType then
                [ "EFFECT003", path, "A data contract cannot contain executable callbacks." ]
            elif t.IsTupleType || t.IsAnonRecordType then
                t.GenericArguments
                |> Seq.indexed
                |> Seq.toList
                |> List.collect (fun (index, arg) -> walk (depth + 1) visited (path + "." + string index) arg)
            elif t.HasTypeDefinition then
                let entity = t.TypeDefinition
                let name = SafetyPolicy.entityName entity

                let intrinsic =
                    (Set.contains entity.Assembly.QualifiedName SafetyPolicy.frameworkIdentities
                     || SafetyPolicy.isCoreIdentity entity.Assembly.QualifiedName)
                    && (Set.contains name SafetyPolicy.purePrimitiveTypes
                        || Set.contains name SafetyPolicy.immutableContainers)

                let arguments () =
                    t.GenericArguments |> Seq.toList |> List.collect (walk (depth + 1) visited path)

                if intrinsic then
                    arguments ()
                elif entity.IsArrayType then
                    [ "MUT008", path, "A data contract cannot contain mutable arrays." ]
                elif not (entity.IsFSharpRecord || entity.IsFSharpUnion) then
                    [ "MODEL001",
                      path + " -> " + name,
                      "Only immutable F# records, unions and approved primitive/container shapes are reusable data." ]
                else
                    pinned entity

                    let key =
                        entity.Assembly.QualifiedName + " | " + t.Format(FSharpDisplayContext.Empty)

                    if Set.contains key visited then
                        []
                    else
                        let visited = Set.add key visited

                        let forbiddenAttribute (attribute: FSharpAttribute) =
                            List.contains
                                (SafetyPolicy.entityName attribute.AttributeType)
                                [ "Microsoft.FSharp.Core.CLIMutableAttribute"
                                  "Microsoft.FSharp.Core.AllowNullLiteralAttribute"
                                  "Microsoft.FSharp.Core.DefaultValueAttribute"
                                  "Microsoft.FSharp.Core.CustomEqualityAttribute"
                                  "Microsoft.FSharp.Core.CustomComparisonAttribute"
                                  "Microsoft.FSharp.Core.StructuredFormatDisplayAttribute"
                                  "Microsoft.FSharp.Core.CompilationRepresentationAttribute" ]

                        let unsafeRepresentation = entity.Attributes |> Seq.exists forbiddenAttribute

                        let formattingHook =
                            entity.MembersFunctionsAndValues
                            |> Seq.exists (fun memberInfo ->
                                memberInfo.CompiledName = "ToString"
                                && memberInfo.IsOverrideOrExplicitInterfaceImplementation
                                && not memberInfo.IsCompilerGenerated)

                        let resource =
                            entity.AllInterfaces
                            |> Seq.exists (fun interfaceType ->
                                interfaceType.HasTypeDefinition
                                && List.contains
                                    (SafetyPolicy.entityName interfaceType.TypeDefinition)
                                    [ "System.IDisposable"; "System.IAsyncDisposable" ])

                        if unsafeRepresentation || resource || formattingHook then
                            [ "MODEL001",
                              path + " -> " + name,
                              "Mutable/null-backed representations, resource interfaces and implicit executable hooks cannot be approved as reusable data." ]
                        else
                            let substitution = Seq.zip entity.GenericParameters t.GenericArguments |> Seq.toList

                            let fields =
                                if entity.IsFSharpUnion then
                                    entity.UnionCases |> Seq.collect (fun case -> case.Fields)
                                else
                                    entity.FSharpFields :> seq<FSharpField>

                            let payloads =
                                fields
                                |> Seq.toList
                                |> List.collect (fun field ->
                                    let fieldPath = path + "." + field.Name

                                    if
                                        field.IsMutable
                                        || field.IsStatic
                                        || field.FieldAttributes |> Seq.exists forbiddenAttribute
                                    then
                                        [ "MUT003",
                                          fieldPath,
                                          "A data contract cannot contain mutable, static or default-initialized fields." ]
                                    else
                                        walk (depth + 1) visited fieldPath (field.FieldType.Instantiate substitution))

                            payloads @ arguments ()
            else
                invalidOp "BUILD003: A data contract has an unclassified type shape."

        walk 0 Set.empty (root.Format(FSharpDisplayContext.Empty)) root
