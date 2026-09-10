namespace FDull

open System
open System.Collections.Generic
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open FSharp.Compiler.Syntax

module internal SafetyAnalysis =
    let private region (r: range) =
        { File = r.FileName
          Start = r.StartLine, r.StartColumn
          End = r.EndLine, r.EndColumn }

    let private containsRange (outer: range) (inner: range) =
        outer.FileName = inner.FileName
        && (outer.StartLine, outer.StartColumn) <= (inner.StartLine, inner.StartColumn)
        && (inner.EndLine, inner.EndColumn) <= (outer.EndLine, outer.EndColumn)

    let private mutableType (name: string) =
        name.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal)
        || Set.contains
            name
            (set
                [ "System.Collections.Generic.List`1"
                  "System.Collections.Generic.Dictionary`2"
                  "System.Collections.Generic.HashSet`1"
                  "System.Collections.Generic.Queue`1"
                  "System.Collections.Generic.Stack`1"
                  "System.Collections.Generic.LinkedList`1"
                  "System.Collections.Generic.SortedDictionary`2"
                  "System.Collections.Generic.SortedList`2"
                  "System.Collections.Generic.SortedSet`1"
                  "System.Collections.ObjectModel.Collection`1"
                  "System.Collections.ObjectModel.ObservableCollection`1"
                  "System.Collections.ArrayList"
                  "System.Collections.Hashtable"
                  "System.Collections.Queue"
                  "System.Collections.Stack"
                  "System.Collections.IList"
                  "System.Collections.IDictionary"
                  "System.Collections.Generic.IList`1"
                  "System.Collections.Generic.ICollection`1"
                  "System.Collections.Generic.IDictionary`2"
                  "System.Collections.Generic.ISet`1"
                  "System.Text.StringBuilder" ])

    let private viewType name =
        Set.contains
            name
            (set
                [ "System.Collections.Generic.IReadOnlyList`1"
                  "System.Collections.Generic.IReadOnlyCollection`1"
                  "System.Collections.Generic.IReadOnlyDictionary`2"
                  "System.Collections.Generic.IEnumerable`1"
                  "System.Collections.IEnumerable"
                  "System.Linq.IQueryable`1"
                  "System.ArraySegment`1" ])

    let private nativeType name =
        Set.contains
            name
            (set
                [ "System.IntPtr"
                  "System.UIntPtr"
                  "System.Span`1"
                  "System.ReadOnlySpan`1"
                  "System.Memory`1"
                  "System.ReadOnlyMemory`1"
                  "Microsoft.FSharp.NativeInterop.nativeptr`1" ])

    let private typeRule name =
        if name = "System.Object" then
            Some("CAST004", "Object-typed payloads erase business data.")
        elif
            name = "System.Exception"
            || name.EndsWith("Exception", StringComparison.Ordinal)
        then
            Some("ERR005", "Exception objects cannot be business data.")
        elif name = "Microsoft.FSharp.Core.FSharpRef`1" then
            Some("MUT004", "Ref cells are mutable capabilities.")
        elif
            name.StartsWith("System.Threading.ThreadLocal", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.AsyncLocal", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.FSharp.Control.FSharpEvent", StringComparison.Ordinal)
            || name.StartsWith("System.Collections.Concurrent.", StringComparison.Ordinal)
        then
            Some("MUT007", "Shared mutable state and synchronization are platform-owned.")
        elif mutableType name then
            Some("MUT005", "Mutable collection/building capabilities are prohibited.")
        elif viewType name then
            Some("MUT008", "Read-only views and deferred sequences do not establish immutable ownership.")
        elif nativeType name then
            Some("NATIVE003", "Native integers, pointers, and memory views are prohibited.")
        elif
            name = "System.Type"
            || name.StartsWith("System.Reflection.", StringComparison.Ordinal)
        then
            Some("REFL001", "Runtime metadata is a reflection capability.")
        elif name = "System.Nullable`1" then
            Some("NULL005", "Nullable external data must be normalized.")
        elif
            name.StartsWith("System.Threading.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.FSharp.Control.", StringComparison.Ordinal)
            || name.StartsWith("System.Lazy", StringComparison.Ordinal)
            || name = "System.IO.Stream"
        then
            Some("EFFECT003", "Executable/resource capabilities are not pure data.")
        else
            None

    let private memberRule (m: FSharpMemberOrFunctionOrValue) =
        let owner =
            m.DeclaringEntity
            |> Option.map SafetyPolicy.entityName
            |> Option.defaultValue ""

        let name = m.CompiledName
        let core = SafetyPolicy.isCoreIdentity m.Assembly.QualifiedName

        let collection =
            owner.StartsWith("Microsoft.FSharp.Collections.", StringComparison.Ordinal)
            || owner = "System.Linq.Enumerable"

        if name = "op_Implicit" || name = "op_Explicit" then
            Some("CAST006", "Custom conversions require an exact platform contract.")
        elif
            core
            && Set.contains name (set [ "op_PipeLeft"; "op_PipeLeft2"; "op_PipeLeft3" ])
        then
            Some("STYLE001", "Backward pipes are prohibited.")
        elif core && Set.contains name (set [ "op_PipeRight2"; "op_PipeRight3" ]) then
            Some("STYLE002", "Tuple pipes are prohibited.")
        elif core && Set.contains name (set [ "Unbox"; "UnboxGeneric"; "UnboxFast" ]) then
            Some("CAST002", "Unboxing, including function aliases, is prohibited.")
        elif core && name = "Box" then
            Some("CAST004", "Authored boxing erases the payload type.")
        elif
            (core && name = "TryUnbox")
            || (owner = "System.Linq.Enumerable" && name = "OfType")
        then
            Some("CAST005", "Runtime-type recovery is prohibited.")
        elif
            (owner = "Microsoft.FSharp.Collections.SeqModule"
             || owner = "System.Linq.Enumerable")
            && name = "Cast"
        then
            Some("CAST003", "Indirect cast APIs are prohibited.")
        elif core && owner.Contains("Unchecked") then
            if
                name.Contains("NonNull", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Null", StringComparison.OrdinalIgnoreCase)
            then
                Some("NULL003", "Unchecked nullability removal is prohibited.")
            else
                Some("NULL002", "Unchecked/default construction is prohibited.")
        elif name = "ZeroCreate" && owner = "Microsoft.FSharp.Collections.ArrayModule" then
            Some("NULL004", "Default-initialized arrays can bypass construction.")
        elif
            core
            && Set.contains name (set [ "Ref"; "Dereference"; "op_ColonEquals"; "Incr"; "Decr" ])
        then
            Some("MUT004", "Ref-cell operations are prohibited.")
        elif m.IsPropertySetterMethod then
            Some("MUT002", "Property setters are prohibited, including method references.")
        elif
            owner = "System.Threading.Interlocked"
            || owner = "System.Threading.Volatile"
            || owner = "System.Threading.Monitor"
            || core && name = "Lock"
        then
            Some("MUT007", "Shared state and synchronization are platform-owned.")
        elif mutableType owner then
            Some("MUT006", "Mutable object methods are prohibited, including method references.")
        elif
            owner.StartsWith("System.Threading.", StringComparison.Ordinal)
            && (name = "Run"
                || name = "Wait"
                || name = "Result"
                || name = "get_Result"
                || name = "GetResult"
                || name.StartsWith("Start", StringComparison.Ordinal))
        then
            Some("ERR010", "Ad hoc background execution and blocking waits are prohibited.")
        elif
            core
            && Set.contains
                name
                (set
                    [ "Raise"
                      "Reraise"
                      "FailWith"
                      "FailWithf"
                      "InvalidArg"
                      "InvalidOp"
                      "NullArg"
                      "PrintFormatToStringThenFail" ])
        then
            Some("ERR002", "Throwing is not application error handling.")
        elif
            name = "Catch"
            && owner.StartsWith("Microsoft.FSharp.Control.", StringComparison.Ordinal)
        then
            Some("ERR003", "Indirect catch APIs are prohibited.")
        elif
            owner = "Microsoft.FSharp.Core.ResultModule"
            && Set.contains name (set [ "DefaultValue"; "DefaultWith"; "ToOption"; "ToValueOption" ])
        then
            Some("ERR008", "This operation discards modeled error information.")
        elif
            (collection
             && Set.contains
                 name
                 (set
                     [ "Fold"
                       "FoldBack"
                       "Fold2"
                       "FoldBack2"
                       "MapFold"
                       "MapFoldBack"
                       "Scan"
                       "ScanBack"
                       "Reduce"
                       "ReduceBack"
                       "Aggregate" ]))
        then
            Some("STYLE003", "Raw accumulation requires an exact reviewed pure helper.")
        elif
            (collection
             && Set.contains
                 name
                 (set
                     [ "Head"
                       "get_Head"
                       "Tail"
                       "get_Tail"
                       "Last"
                       "get_Last"
                       "Item"
                       "get_Item"
                       "Find"
                       "FindBack"
                       "Pick"
                       "ExactlyOne"
                       "Min"
                       "Max"
                       "Average"
                       "AverageBy"
                       "MinBy"
                       "MaxBy"
                       "First"
                       "Single" ]))
            || (owner.Contains("Option")
                && (name = "GetValue" || name = "get_Value" || name = "Value"))
            || name = "Parse" && owner.StartsWith("System.", StringComparison.Ordinal)
        then
            Some("ERR006", "Partial access and throwing parsers require explicit failure handling.")
        elif
            (name = "get_Now"
             || name = "get_UtcNow"
             || name = "Now"
             || name = "UtcNow"
             || name = "NewGuid")
            || owner.StartsWith("System.IO.", StringComparison.Ordinal)
            || owner = "System.Environment"
            || owner = "System.Console"
            || owner = "System.Random"
            || owner.StartsWith("System.Net.", StringComparison.Ordinal)
            || name.StartsWith("PrintFormat", StringComparison.Ordinal)
               && not (name.StartsWith("PrintFormatToString", StringComparison.Ordinal))
        then
            Some("EFFECT002", "Ambient I/O, time, randomness, and process state belong to platform adapters.")
        elif
            owner.Contains("Unsafe")
            || owner.Contains("NativePtr")
            || owner.Contains("MemoryMarshal")
            || owner = "System.Runtime.InteropServices.Marshal"
        then
            Some("NATIVE002", "Raw native/memory capabilities are prohibited.")
        elif name = "GetUninitializedObject" then
            Some("REFL003", "Uninitialized-object creation bypasses domain construction.")
        elif
            owner = "System.Activator"
            || owner = "Microsoft.FSharp.Reflection.FSharpValue"
            || name = "DynamicInvoke"
        then
            Some("REFL002", "Reflective construction and invocation are prohibited.")
        elif (core && (name = "TypeOf" || name = "TypeDefOf")) || name = "GetType" then
            Some("REFL001", "Runtime type metadata is prohibited.")
        elif owner.StartsWith("System.Reflection.", StringComparison.Ordinal) then
            Some("REFL004", "Dynamic loading and metaprogramming are prohibited.")
        elif
            owner.StartsWith("System.Text.Json.", StringComparison.Ordinal)
            || owner.StartsWith("Newtonsoft.Json.", StringComparison.Ordinal)
        then
            Some("DATA002", "Raw serialization/materialization is platform-owned.")
        elif
            core
            && Set.contains
                name
                (set
                    [ "ToByte"
                      "ToSByte"
                      "ToInt16"
                      "ToUInt16"
                      "ToInt32"
                      "ToUInt32"
                      "ToInt64"
                      "ToUInt64"
                      "ToChar"
                      "EnumOfValue"
                      "ToEnum" ])
        then
            Some("CAST007", "Numeric/domain conversions require a validated platform helper.")
        else
            None

    let inspect
        (budget: SafetyBudget)
        (context: SafetyContext)
        (files: Set<string>)
        (result: FSharpCheckProjectResults)
        (syntax: Map<string, SourceSyntax>)
        emit
        =
        let uses = result.GetAllUsesOfAllSymbols()

        let useIndex =
            SafetyRanges.create budget.Visit (fun (useSite: FSharpSymbolUse) -> region useSite.Range) uses

        let localSymbol (s: FSharpSymbol) =
            s.DeclarationLocation |> Option.exists (fun r -> Set.contains r.FileName files)

        let localEntity (e: FSharpEntity) = localSymbol e
        let localMember (m: FSharpMemberOrFunctionOrValue) = localSymbol m
        let domainEntity (e: FSharpEntity) = localEntity e || context.Domain e

        let emitAt id r symbol message =
            emit id r symbol message "Use explicit immutable data, Result/Option, or an approved platform API."

        let rec shapeAt depth dataOnly path (visited: Set<string>) (t: FSharpType) r =
            budget.Visit depth
            let shape = shapeAt (depth + 1)

            if t.IsUnresolved then
                emitAt "BUILD003" r path "A type could not be resolved."
            elif t.IsAbbreviation then
                shape dataOnly path visited t.AbbreviatedType r
            elif t.HasNullAnnotation then
                emitAt "NULL005" r path "Nullable data requires boundary normalization."
            elif t.IsGenericParameter then
                ()
            elif t.IsFunctionType then
                if dataOnly then
                    emitAt "EFFECT003" r path "An executable callback is hidden inside data."
                else
                    for arg in t.GenericArguments do
                        shape false path visited arg r
            elif t.IsTupleType || t.IsAnonRecordType then
                for i, arg in t.GenericArguments |> Seq.indexed do
                    shape dataOnly (path + "." + string i) visited arg r
            elif t.HasTypeDefinition then
                let e = t.TypeDefinition
                let name = SafetyPolicy.entityName e
                let key = e.Assembly.QualifiedName + "|" + t.Format(FSharpDisplayContext.Empty)

                let intrinsic =
                    (Set.contains e.Assembly.QualifiedName SafetyPolicy.frameworkIdentities
                     || SafetyPolicy.isCoreIdentity e.Assembly.QualifiedName)
                    && (Set.contains name SafetyPolicy.purePrimitiveTypes
                        || Set.contains name SafetyPolicy.immutableContainers)

                if not intrinsic && context.Type r e then
                    for arg in t.GenericArguments do
                        shape dataOnly path visited arg r
                elif visited.Count > SafetyLimits.typeDepth then
                    emitAt "BUILD003" r path "Recursive type analysis exceeded the supported depth."
                elif not (Set.contains key visited) then
                    let visited = Set.add key visited

                    if e.IsArrayType then
                        emitAt "MUT008" r path "Arrays are mutable, including nested and multidimensional arrays."
                    elif e.IsByRef then
                        emitAt "NATIVE003" r path "Authored byref/outref/inref capabilities are prohibited."
                    else
                        match typeRule name with
                        | Some(id, message) ->
                            emitAt id r (path + " -> " + name) message

                            if dataOnly && path.Contains('.') && (id = "MUT005" || id = "MUT008") then
                                emitAt "MUT010" r path "An immutable wrapper contains a mutable or aliasable payload."
                        | None ->
                            let approved =
                                (Set.contains e.Assembly.QualifiedName SafetyPolicy.frameworkIdentities
                                 || SafetyPolicy.isCoreIdentity e.Assembly.QualifiedName)
                                && (Set.contains name SafetyPolicy.purePrimitiveTypes
                                    || Set.contains name SafetyPolicy.immutableContainers)


                            if localEntity e then
                                if e.IsFSharpRecord || e.IsFSharpUnion then
                                    let substitution = Seq.zip e.GenericParameters t.GenericArguments |> Seq.toList

                                    let fields =
                                        if e.IsFSharpUnion then
                                            e.UnionCases |> Seq.collect (fun c -> c.Fields)
                                        else
                                            e.FSharpFields

                                    for field in fields do
                                        if field.IsMutable then
                                            emitAt "MUT003" r (path + "." + field.Name) "Mutable fields are prohibited."

                                        shape
                                            true
                                            (path + "." + field.Name)
                                            visited
                                            (field.FieldType.Instantiate substitution)
                                            r
                                else
                                    emitAt
                                        "MODEL001"
                                        r
                                        (path + " -> " + name)
                                        "Only approved immutable data shapes are supported."
                            elif approved then
                                ()
                            else
                                emitAt
                                    "MODEL001"
                                    r
                                    (path + " -> " + name)
                                    "The external type has no approved immutable-data contract."

                            for arg in t.GenericArguments do
                                shape dataOnly path visited arg r
            else
                emitAt "BUILD003" r path "Unclassified F# type shape."

        let shape = shapeAt 0

        let rec transportAt depth (visited: Set<string>) path (t: FSharpType) r =
            budget.Visit depth
            let transport = transportAt (depth + 1)

            if t.IsAbbreviation then
                transport visited path t.AbbreviatedType r
            elif t.IsGenericParameter then
                emitAt "DATA003" r path "A materializing API cannot expose caller-selected arbitrary target types."
            elif t.HasTypeDefinition then
                let e = t.TypeDefinition
                let name = SafetyPolicy.entityName e
                let key = t.Format(FSharpDisplayContext.Empty)

                if visited.Count > SafetyLimits.typeDepth then
                    emitAt "BUILD003" r path "Materialization target analysis exceeded the supported depth."
                elif not (Set.contains key visited) then
                    let visited = Set.add key visited

                    if
                        context.Domain e
                        || ((e.IsFSharpRecord || e.IsFSharpUnion)
                            && not e.RepresentationAccessibility.IsPublic)
                    then
                        emitAt
                            "DATA001"
                            r
                            (path + " -> " + name)
                            "Automatic materialization cannot construct domain/private representations."
                    elif e.IsFSharpRecord then
                        let substitution = Seq.zip e.GenericParameters t.GenericArguments |> Seq.toList

                        for field in e.FSharpFields do
                            transport visited (path + "." + field.Name) (field.FieldType.Instantiate substitution) r
                    elif
                        not (
                            (Set.contains e.Assembly.QualifiedName SafetyPolicy.frameworkIdentities
                             || SafetyPolicy.isCoreIdentity e.Assembly.QualifiedName)
                            && (Set.contains name SafetyPolicy.purePrimitiveTypes
                                || Set.contains name SafetyPolicy.immutableContainers)
                        )
                    then
                        emitAt
                            "DATA004"
                            r
                            (path + " -> " + name)
                            "The materialization target has no approved DTO representation."

                    for arg in t.GenericArguments do
                        transport visited path arg r
            elif t.IsFunctionType then
                emitAt "DATA004" r path "Transport data cannot contain executable callbacks."
            elif t.IsTupleType || t.IsAnonRecordType then
                for arg in t.GenericArguments do
                    transport visited path arg r
            elif t.IsUnresolved then
                emitAt "BUILD003" r path "Materialization target is unresolved."

        let transport = transportAt 0

        let checkMember (m: FSharpMemberOrFunctionOrValue) r =
            budget.Visit 0

            if m.IsUnresolved then
                emitAt "BUILD003" r m.DisplayName "The symbol could not be resolved."
            else
                match memberRule m with
                | None when localMember m || SafetyPolicy.approvedApi m -> ()
                | rule when
                    context.Member r m
                    && (not (List.contains (context.Profile r) [ "PURE"; "PURE_HELPER" ])
                        || rule.IsNone
                        || (context.Profile r = "PURE_HELPER"
                            && rule |> Option.exists (fun (id, _) -> id = "STYLE003")))
                    ->
                    ()
                | Some(id, message) -> emitAt id r m.FullName message
                | None when not (localMember m) && not (SafetyPolicy.approvedApi m) ->
                    emitAt
                        "EFFECT001"
                        r
                        (m.Assembly.QualifiedName + " | " + m.XmlDocSig)
                        "This exact external API is outside the approved catalog."
                | None -> ()

        let rec containsPrivateDomainAt depth (visited: Set<string>) (t: FSharpType) =
            budget.Visit depth
            let containsPrivateDomain = containsPrivateDomainAt (depth + 1)

            if t.IsAbbreviation then
                containsPrivateDomain visited t.AbbreviatedType
            elif t.IsGenericParameter then
                false
            elif t.HasTypeDefinition then
                let e = t.TypeDefinition
                let key = t.Format(FSharpDisplayContext.Empty)

                if visited.Count > SafetyLimits.typeDepth then
                    true
                elif Set.contains key visited then
                    false
                else
                    (domainEntity e
                     && not e.RepresentationAccessibility.IsPublic
                     && (e.IsFSharpRecord || e.IsFSharpUnion))
                    || t.GenericArguments |> Seq.exists (containsPrivateDomain (Set.add key visited))
                    || ((e.IsFSharpRecord || e.IsFSharpUnion)
                        && (let fields =
                                if e.IsFSharpUnion then
                                    e.UnionCases |> Seq.collect (fun c -> c.Fields)
                                else
                                    e.FSharpFields

                            let substitution = Seq.zip e.GenericParameters t.GenericArguments |> Seq.toList

                            fields
                            |> Seq.exists (fun field ->
                                containsPrivateDomain (Set.add key visited) (field.FieldType.Instantiate substitution))))
            elif t.IsFunctionType || t.IsTupleType || t.IsAnonRecordType then
                t.GenericArguments |> Seq.exists (containsPrivateDomain visited)
            else
                false

        let containsPrivateDomain = containsPrivateDomainAt 0

        for useSite in uses do
            budget.Visit 0

            let useRange = useSite.Range

            if Set.contains useRange.FileName files then
                match useSite.Symbol with
                | :? FSharpMemberOrFunctionOrValue as m ->
                    if useSite.IsFromDefinition then
                        if not m.IsCompilerGenerated then
                            if m.IsMutable then
                                emitAt "MUT001" useSite.Range m.FullName "Mutable bindings are prohibited."

                            if m.IsMutable && m.IsModuleValueOrMember then
                                emitAt
                                    "MUT007"
                                    useSite.Range
                                    m.FullName
                                    "Application-owned global mutable state is prohibited."

                            if m.CompiledName.StartsWith("op_", StringComparison.Ordinal) then
                                emitAt
                                    (if m.CompiledName.StartsWith("op_PipeLeft", StringComparison.Ordinal) then
                                         "STYLE001"
                                     else
                                         "STYLE002")
                                    useSite.Range
                                    m.FullName
                                    "Application-defined operators, including redefinitions, are prohibited."

                            if m.IsActivePattern then
                                emitAt
                                    "EFFECT004"
                                    useSite.Range
                                    m.FullName
                                    "Custom active-pattern dispatch requires a reviewed contract."

                            shape false m.DisplayName Set.empty m.FullType useSite.Range

                            if m.IsModuleValueOrMember && m.Accessibility.IsPublic then
                                if
                                    containsPrivateDomain Set.empty m.ReturnParameter.Type
                                    && not (context.Constructor m)
                                then
                                    emitAt
                                        "MODEL003"
                                        useSite.Range
                                        m.FullName
                                        "A public producer of private domain values needs an exact protected construction contract."

                                for parameters in m.CurriedParameterGroups do
                                    for parameter in parameters do
                                        if parameter.Type.IsFunctionType then
                                            emitAt
                                                "EFFECT003"
                                                useSite.Range
                                                m.FullName
                                                "Public pure ingress cannot accept unchecked executable callbacks."
                    else
                        let attributeConstructor =
                            m.IsConstructor
                            && (m.DeclaringEntity |> Option.exists (fun e -> e.IsAttributeType))
                            && (syntax
                                |> Map.exists (fun _ source ->
                                    source.Attributes
                                    |> List.exists (fun a ->
                                        budget.Visit 0
                                        containsRange a.Range useSite.Range)))

                        if not attributeConstructor then
                            checkMember m useSite.Range
                | :? FSharpField as field when useSite.IsFromDefinition ->
                    shape true field.Name Set.empty field.FieldType useSite.Range
                | :? FSharpEntity as e when useSite.IsFromDefinition && not e.IsFSharpModule && not e.IsNamespace ->
                    if context.Domain e && e.RepresentationAccessibility.IsPublic then
                        emitAt
                            "MODEL002"
                            useSite.Range
                            (SafetyPolicy.entityName e)
                            "A designated invariant must have a private representation."

                    if e.IsFSharpAbbreviation then
                        shape false e.DisplayName Set.empty e.AbbreviatedType useSite.Range
                    elif e.IsFSharpExceptionDeclaration then
                        emitAt "ERR002" useSite.Range e.DisplayName "Application exceptions are prohibited."
                    elif
                        not e.IsFSharpRecord
                        && not e.IsFSharpUnion
                        && not (context.Type useSite.Range e)
                    then
                        emitAt
                            "MODEL001"
                            useSite.Range
                            e.DisplayName
                            "Application classes, structs, enums, and executable objects require a protected contract."
                    else
                        let fields =
                            if e.IsFSharpUnion then
                                e.UnionCases |> Seq.collect (fun c -> c.Fields)
                            else
                                e.FSharpFields

                        for field in fields do
                            shape true (e.DisplayName + "." + field.Name) Set.empty field.FieldType useSite.Range
                | _ -> ()

        for KeyValue(_, source) in syntax do
            for attr in source.Attributes do
                let entity =
                    useIndex.Inside(region attr.Range)
                    |> List.tryPick (fun u ->
                        budget.Visit 0

                        if containsRange attr.Range u.Range then
                            match u.Symbol with
                            | :? FSharpEntity as e when e.IsAttributeType -> Some e
                            | :? FSharpMemberOrFunctionOrValue as m when m.IsConstructor -> m.DeclaringEntity
                            | _ -> None
                        else
                            None)

                match entity with
                | None -> emitAt "BUILD003" attr.Range "attribute" "The authored attribute could not be resolved."
                | Some entity ->
                    let name = SafetyPolicy.entityName entity

                    let id =
                        if name = "System.Runtime.InteropServices.DllImportAttribute" then
                            "NATIVE001"
                        elif
                            Set.contains
                                name
                                (set
                                    [ "Microsoft.FSharp.Core.CLIMutableAttribute"
                                      "Microsoft.FSharp.Core.DefaultValueAttribute"
                                      "Microsoft.FSharp.Core.AllowNullLiteralAttribute" ])
                        then
                            "ATTR002"
                        elif name = "Microsoft.FSharp.Core.CompilationRepresentationAttribute" then
                            "ATTR003"
                        elif
                            name.Contains("Suppress")
                            || name.Contains("Generated")
                            || name.Contains("InternalsVisibleTo")
                            || name.Contains("Null")
                        then
                            "ATTR004"
                        else
                            "ATTR001"

                    let unitArgument =
                        match attr.ArgExpr with
                        | SynExpr.Const(SynConst.Unit, _) -> true
                        | _ -> false

                    if
                        not (context.Attribute attr entity)
                        && not (
                            SafetyPolicy.isCoreIdentity entity.Assembly.QualifiedName
                            && Set.contains name SafetyPolicy.attributes
                            && attr.Target.IsNone
                            && unitArgument
                        )
                    then
                        emitAt
                            id
                            attr.Range
                            name
                            "This attribute/target/argument combination has no permission in application source."

        let rec exactException pattern =
            match pattern with
            | SynPat.Paren(pattern, _)
            | SynPat.Typed(pattern, _, _)
            | SynPat.As(pattern, _, _) -> exactException pattern
            | SynPat.Or(first, second, _, _) -> exactException first && exactException second
            | SynPat.IsInst(target, _) ->
                useIndex.Inside(region target.Range)
                |> List.exists (fun useSite ->
                    match useSite.Symbol with
                    | :? FSharpEntity as entity ->
                        let name = SafetyPolicy.entityName entity
                        name <> "System.Exception" && name <> "System.SystemException"
                    | _ -> false)
            | SynPat.LongIdent _ ->
                useIndex.Inside(region pattern.Range)
                |> List.exists (fun useSite ->
                    match useSite.Symbol with
                    | :? FSharpEntity as entity -> entity.IsFSharpExceptionDeclaration
                    | _ -> false)
            | _ -> false

        let rec rethrows expression =
            match expression with
            | SynExpr.Paren(expression, _, _, _)
            | SynExpr.Typed(expression, _, _) -> rethrows expression
            | SynExpr.Sequential(_, _, _, last, _, _) -> rethrows last
            | SynExpr.App(_, _, callee, _, _) ->
                useIndex.Inside(region callee.Range)
                |> List.exists (fun useSite ->
                    match useSite.Symbol with
                    | :? FSharpMemberOrFunctionOrValue as memberInfo ->
                        SafetyPolicy.isCoreIdentity memberInfo.Assembly.QualifiedName
                        && memberInfo.CompiledName = "Reraise"
                    | _ -> false)
            | _ -> false

        for KeyValue(_, source) in syntax do
            for handler in source.Handlers do
                for SynMatchClause(pattern, _, body, _, _, _) in handler do
                    if not (exactException pattern) && not (rethrows body) then
                        emitAt
                            "ERR004"
                            pattern.Range
                            "exception handler"
                            "Broad exception recovery requires an exact top-level failure-reporting contract."

        // Domain coverage is checked below against the typed scrutinee.
        // Nested names in a predicate do not establish the match input type.

        let rec mustHandleAt depth (t: FSharpType) =
            budget.Visit depth
            let mustHandle = mustHandleAt (depth + 1)

            if t.IsAbbreviation then
                mustHandle t.AbbreviatedType
            elif t.HasTypeDefinition then
                let name = SafetyPolicy.entityName t.TypeDefinition

                name = "Microsoft.FSharp.Core.FSharpResult`2"
                || (name = "System.Threading.Tasks.Task`1"
                    && t.GenericArguments |> Seq.exists mustHandle)
            else
                false

        let mustHandle = mustHandleAt 0
        let expressions = ResizeArray<FSharpExpr>()
        let lambdaInputs = ResizeArray<range * FSharpType list>()
        let ignoreAliases = HashSet<FSharpMemberOrFunctionOrValue>()

        let rec isIgnoreAt depth (e: FSharpExpr) =
            budget.Visit depth
            let isIgnore = isIgnoreAt (depth + 1)

            match e with
            | FSharpExprPatterns.Call(_, m, _, _, args) ->
                ((SafetyPolicy.isCoreIdentity m.Assembly.QualifiedName
                  && m.CompiledName = "Ignore")
                 || ignoreAliases.Contains m)
                && args
                   |> List.forall (function
                       | FSharpExprPatterns.Value _ -> true
                       | _ -> false)
            | FSharpExprPatterns.Value m ->
                (SafetyPolicy.isCoreIdentity m.Assembly.QualifiedName
                 && m.CompiledName = "Ignore")
                || ignoreAliases.Contains m
            | FSharpExprPatterns.Lambda(_, body)
            | FSharpExprPatterns.TypeLambda(_, body) -> isIgnore body
            | _ -> false

        let isIgnore = isIgnoreAt 0

        let disposable (t: FSharpType) =
            t.HasTypeDefinition
            && SafetyPolicy.entityName t.TypeDefinition = "System.IDisposable"
            && Set.contains t.TypeDefinition.Assembly.QualifiedName SafetyPolicy.frameworkIdentities

        let rec unitType depth (t: FSharpType) =
            budget.Visit depth

            if t.IsAbbreviation then
                unitType (depth + 1) t.AbbreviatedType
            else
                t.HasTypeDefinition
                && SafetyPolicy.isCoreIdentity t.TypeDefinition.Assembly.QualifiedName
                && SafetyPolicy.entityName t.TypeDefinition = "Microsoft.FSharp.Core.Unit"

        // The pinned compiler lowers `use value = ...` to an IDisposable type
        // test and unbox in a finally clause at the source binding's range.
        // Recognize that complete lowering, never arbitrary coercion nodes.
        let resourceCleanup (cleanup: FSharpExpr) =
            let cleanupRange = cleanup.Range

            let resourceRange =
                Map.tryFind cleanupRange.FileName syntax
                |> Option.exists (fun source -> source.ResourceBindings |> List.exists (fun r -> r = cleanupRange))

            match cleanup with
            | FSharpExprPatterns.IfThenElse(FSharpExprPatterns.TypeTest(target,
                                                                        FSharpExprPatterns.Coerce(_,
                                                                                                  FSharpExprPatterns.Value tested)),
                                            FSharpExprPatterns.Call(Some(FSharpExprPatterns.Call(_,
                                                                                                 unbox,
                                                                                                 _,
                                                                                                 _,
                                                                                                 [ FSharpExprPatterns.Coerce(_,
                                                                                                                             FSharpExprPatterns.Value released) ])),
                                                                    dispose,
                                                                    _,
                                                                    _,
                                                                    []),
                                            FSharpExprPatterns.Const(_, resultType)) ->
                resourceRange
                && disposable target
                && tested = released
                && SafetyPolicy.isCoreIdentity unbox.Assembly.QualifiedName
                && unbox.FullName = "Microsoft.FSharp.Core.LanguagePrimitives.IntrinsicFunctions.UnboxGeneric"
                && Set.contains dispose.Assembly.QualifiedName SafetyPolicy.frameworkIdentities
                && dispose.XmlDocSig = "M:System.IDisposable.Dispose"
                && unitType 0 resultType
            | _ -> false

        let rec expressionAt depth (expr: FSharpExpr) =
            budget.Visit depth
            expressions.Add expr

            match expr with
            | FSharpExprPatterns.ObjectExpr(objectType, _, _, _) ->
                if
                    not objectType.HasTypeDefinition
                    || not (context.Type expr.Range objectType.TypeDefinition)
                then
                    emitAt
                        "MODEL001"
                        expr.Range
                        "object expression"
                        "Executable objects require an exact protected interface contract; their method bodies remain checked."
            | FSharpExprPatterns.Lambda(parameter, body) -> lambdaInputs.Add(body.Range, [ parameter.FullType ])
            | FSharpExprPatterns.Let((binding, value, _), _) when isIgnore value -> ignoreAliases.Add binding |> ignore
            | FSharpExprPatterns.Call(_, m, typeArgs1, typeArgs2, arguments) ->
                // Symbol uses catch references before optimization; this pass supplies
                // instantiated types and local result-flow information.
                let owner =
                    m.DeclaringEntity
                    |> Option.map SafetyPolicy.entityName
                    |> Option.defaultValue ""

                let materializer =
                    (List.contains owner [ "FDull.Transport.Codec"; "FDull.Transport.Materializer" ]
                     && List.contains m.CompiledName [ "decode"; "decodeType" ])
                    || (owner = "System.Text.Json.JsonSerializer"
                        && List.contains m.CompiledName [ "Deserialize"; "DeserializeAsync" ])

                if materializer then
                    let targets =
                        if not (List.isEmpty (typeArgs1 @ typeArgs2)) then
                            typeArgs1 @ typeArgs2
                        else
                            arguments
                            |> List.collect (function
                                | FSharpExprPatterns.Call(_, symbol, first, second, _) when
                                    SafetyPolicy.isCoreIdentity symbol.Assembly.QualifiedName
                                    && symbol.CompiledName = "TypeOf"
                                    ->
                                    first @ second
                                | _ -> [])

                    if targets.IsEmpty then
                        emitAt
                            "DATA003"
                            expr.Range
                            m.FullName
                            "Runtime-selected materialization requires an exact checked DTO decoder contract."

                    for target in targets do
                        transport Set.empty m.FullName target expr.Range

                let serializer =
                    (List.contains owner [ "FDull.Transport.Codec" ]
                     && List.contains m.CompiledName [ "encode"; "digest" ])
                    || (owner = "System.Text.Json.JsonSerializer"
                        && List.contains
                            m.CompiledName
                            [ "Serialize"; "SerializeToElement"; "SerializeToNode"; "SerializeAsync" ])

                if serializer then
                    for target in typeArgs1 @ typeArgs2 do
                        if containsPrivateDomain Set.empty target then
                            emitAt
                                "DATA005"
                                expr.Range
                                m.FullName
                                "Project validated domain values into an explicit transport record before serialization."

                if
                    (SafetyPolicy.isCoreIdentity m.Assembly.QualifiedName
                     && m.CompiledName = "Ignore")
                    || ignoreAliases.Contains m
                then
                    for arg in arguments do
                        if mustHandle arg.Type then
                            emitAt "ERR007" expr.Range m.FullName "A Result is discarded through ignore."
            | FSharpExprPatterns.Application(fn, _, args) when isIgnore fn ->
                for arg in args do
                    if mustHandle arg.Type then
                        emitAt "ERR007" expr.Range "ignore alias" "A Result is discarded through an alias of ignore."
            | FSharpExprPatterns.Coerce(target, input) ->
                let rec actual depth (t: FSharpType) =
                    budget.Visit depth

                    if t.IsAbbreviation then
                        actual (depth + 1) t.AbbreviatedType
                    else
                        t

                let inputType = actual 0 input.Type
                let targetType = actual 0 target

                let listEnumeration =
                    inputType.HasTypeDefinition
                    && targetType.HasTypeDefinition
                    && SafetyPolicy.entityName inputType.TypeDefinition = "Microsoft.FSharp.Collections.FSharpList`1"
                    && SafetyPolicy.entityName targetType.TypeDefinition = "System.Collections.Generic.IEnumerable`1"

                let comprehensionLowering =
                    syntax
                    |> Map.exists (fun _ source ->
                        source.Comprehensions
                        |> List.exists (fun r ->
                            budget.Visit 0
                            containsRange r expr.Range)
                        && not (
                            source.ExplicitCoercions
                            |> List.exists (fun r ->
                                budget.Visit 0
                                containsRange r expr.Range)
                        ))

                if not listEnumeration && not comprehensionLowering then
                    shape false "coercion" Set.empty target expr.Range
            | FSharpExprPatterns.Sequential(first, _) when mustHandle first.Type ->
                emitAt "ERR007" first.Range "Result" "A Result is discarded in a sequence."
            | _ -> ()

            let children =
                match expr with
                | FSharpExprPatterns.TryFinally(body, cleanup, _, _) when resourceCleanup cleanup -> [ body ]
                | _ -> expr.ImmediateSubExpressions

            for child in children do
                expressionAt (depth + 1) child

        let expression = expressionAt 0

        let rec declaration depth item =
            budget.Visit depth

            match item with
            | FSharpImplementationFileDeclaration.Entity(_, declarations) ->
                List.iter (declaration (depth + 1)) declarations
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(m, arguments, body) ->
                // Keep every body inspection behind this test. Inspecting generated
                // null-backed DU members caused runaway FCS allocation (ATTR003).
                // Authored attributes, signatures and data shapes are checked above.
                if not m.IsCompilerGenerated then
                    match List.tryLast arguments with
                    | Some parameters -> lambdaInputs.Add(body.Range, parameters |> List.map (fun p -> p.FullType))
                    | None -> ()

                    if isIgnore body then
                        ignoreAliases.Add m |> ignore

                    expression body
            | FSharpImplementationFileDeclaration.InitAction body -> expression body

        for file in result.AssemblyContents.ImplementationFiles do
            for d in file.Declarations do
                declaration 0 d

        let expressionIndex =
            SafetyRanges.create budget.Visit (fun (expr: FSharpExpr) -> region expr.Range) expressions

        let lambdaIndex =
            SafetyRanges.create budget.Visit (fun (r, _) -> region r) lambdaInputs

        let rec matchPatternAt depth (t: FSharpType) pattern =
            budget.Visit depth
            let matchPattern = matchPatternAt (depth + 1)

            if t.IsAbbreviation then
                matchPattern t.AbbreviatedType pattern
            else
                match pattern with
                | SynPat.Paren(p, _)
                | SynPat.Typed(p, _, _)
                | SynPat.As(p, _, _) -> matchPattern t p
                | SynPat.Or(a, b, _, _) ->
                    matchPattern t a
                    matchPattern t b
                | SynPat.Tuple(_, patterns, _, _) when t.IsTupleType && patterns.Length = t.GenericArguments.Count ->
                    for p, fieldType in Seq.zip patterns t.GenericArguments do
                        matchPattern fieldType p
                | SynPat.Wild r
                | SynPat.Named(range = r) when
                    t.HasTypeDefinition
                    && domainEntity t.TypeDefinition
                    && t.TypeDefinition.IsFSharpUnion
                    ->
                    emitAt
                        "MODEL004"
                        r
                        (SafetyPolicy.entityName t.TypeDefinition)
                        "A closed domain decision must name its union cases."
                | _ -> ()

        let matchPattern = matchPatternAt 0

        let rec sourcePattern depth expression pattern =
            budget.Visit depth
            let check = sourcePattern (depth + 1)

            match expression, pattern with
            | SynExpr.Paren(input, _, _, _), _
            | SynExpr.Typed(input, _, _), _ -> check input pattern
            | _, SynPat.Paren(inner, _)
            | _, SynPat.Typed(inner, _, _)
            | _, SynPat.As(inner, _, _) -> check expression inner
            | _, SynPat.Or(first, second, _, _) ->
                check expression first
                check expression second
            | SynExpr.Tuple(_, inputs, _, _), SynPat.Tuple(_, patterns, _, _) when inputs.Length = patterns.Length ->
                List.iter2 check inputs patterns
            | SynExpr.Tuple(_, inputs, _, _), (SynPat.Wild _ | SynPat.Named _) ->
                List.iter (fun input -> check input pattern) inputs
            | _ ->
                let r = expression.Range

                let named =
                    match expression with
                    | SynExpr.Ident _
                    | SynExpr.LongIdent _
                    | SynExpr.DotGet _ -> true
                    | _ -> false

                let symbols =
                    if not named then
                        []
                    else
                        useIndex.Inside(region r)
                        |> List.choose (fun useSite ->
                            let useRange = useSite.Range

                            if useRange.EndLine <> r.EndLine || useRange.EndColumn <> r.EndColumn then
                                None
                            else
                                match useSite.Symbol with
                                | :? FSharpMemberOrFunctionOrValue as value -> Some value.FullType
                                | :? FSharpField as field -> Some field.FieldType
                                | _ -> None)

                let types =
                    if not symbols.IsEmpty then
                        symbols
                    else
                        expressionIndex.Inside(region r)
                        |> List.choose (fun candidate ->
                            if candidate.Range <> r then
                                None
                            else
                                match candidate with
                                | FSharpExprPatterns.Call _
                                | FSharpExprPatterns.Application _
                                | FSharpExprPatterns.FSharpFieldGet _
                                | FSharpExprPatterns.Value _ -> Some candidate.Type
                                | _ -> None)

                for input in types do
                    matchPattern input pattern

        for KeyValue(_, source) in syntax do
            for decision in source.Matches do
                budget.Visit 0

                match decision.Expression with
                | Some expression ->
                    for SynMatchClause(pattern, _, _, _, _, _) in decision.Clauses do
                        sourcePattern 0 expression pattern
                | None -> ()

                if decision.Scrutinee.IsNone then
                    let closest =
                        lambdaIndex.Inside(region decision.Range)
                        |> Seq.sortByDescending (fun (r, _) -> r.EndLine - r.StartLine, r.EndColumn - r.StartColumn)
                        |> Seq.tryHead

                    match closest with
                    | Some(_, [ input ]) ->
                        for SynMatchClause(pattern, _, _, _, _, _) in decision.Clauses do
                            matchPattern input pattern
                    | Some(_, inputs) ->
                        for SynMatchClause(pattern, _, _, _, _, _) in decision.Clauses do
                            match pattern with
                            | SynPat.Tuple(_, patterns, _, _) when inputs.Length = patterns.Length ->
                                List.iter2 matchPattern inputs patterns
                            | _ -> ()
                    | None -> ()

                let candidates =
                    match decision.Scrutinee with
                    | Some _ -> []
                    | None -> expressionIndex.Enclosing(region decision.Range)

                for expr in candidates do
                    budget.Visit 0

                    let input =
                        match decision.Scrutinee with
                        | Some _ -> None
                        | None when containsRange expr.Range decision.Range && expr.Type.IsFunctionType ->
                            Some expr.Type.GenericArguments[0]
                        | _ -> None

                    match input with
                    | Some t ->
                        for SynMatchClause(pattern, _, _, _, _, _) in decision.Clauses do
                            matchPattern t pattern
                    | None -> ()

            for discarded in source.Discards do
                for expr in expressionIndex.Inside(region discarded) do
                    budget.Visit 0

                    if discarded = expr.Range && mustHandle expr.Type then
                        emitAt "ERR007" discarded "Result" "A wildcard/do/sequence binding discards a Result."
