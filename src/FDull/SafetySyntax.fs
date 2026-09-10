namespace FDull

open System
open System.Collections
open Microsoft.FSharp.Reflection
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FSharp.Compiler.Tokenization

[<NoEquality; NoComparison>]
type internal SourceDecision =
    { Scrutinee: range option
      Expression: SynExpr option
      Range: range
      Clauses: SynMatchClause list }

[<NoEquality; NoComparison>]
type internal SourceSyntax =
    { Attributes: SynAttribute list
      Matches: SourceDecision list
      Discards: range list
      Comprehensions: range list
      ExplicitCoercions: range list
      Types: range list
      Modules: range list
      Bindings: range list
      ResourceBindings: range list
      Handlers: SynMatchClause list list }

module internal SafetySyntax =
    // Reflection here walks the pinned FCS syntax *structure*, never source text.
    // Explicit SynExpr cases below define the supported authored language.
    let private children (node: obj | null) =
        match node with
        | Null -> [||]
        | NonNull node ->
            let t = node.GetType()

            if node :? IEnumerable && not (node :? string) then
                (node :?> IEnumerable) |> Seq.cast<obj | null> |> Seq.toArray
            elif FSharpType.IsUnion t then
                FSharpValue.GetUnionFields(node, t) |> snd
            elif FSharpType.IsRecord t then
                FSharpValue.GetRecordFields node
            elif FSharpType.IsTuple t then
                FSharpValue.GetTupleFields node
            else
                [||]

    let private unionName node =
        let case, _ = FSharpValue.GetUnionFields(node, node.GetType())
        case.Name

    let inspect (budget: SafetyBudget) file (source: string) tree emit =
        let attrs = ResizeArray<SynAttribute>()
        let matches = ResizeArray<SourceDecision>()
        let discards = ResizeArray<range>()
        let comprehensions = ResizeArray<range>()
        let coercions = ResizeArray<range>()
        let types = ResizeArray<range>()
        let modules = ResizeArray<range>()
        let bindings = ResizeArray<range>()
        let resourceBindings = ResizeArray<range>()
        let handlers = ResizeArray<SynMatchClause list>()
        let error id location message alternative = emit id location "" message alternative
        let tokenizer = FSharpSourceTokenizer([], Some file, Some "10.0", Some true)
        let mutable state = FSharpTokenizerLexState.Initial

        for lineNumber, line in source.Replace("\r\n", "\n").Split('\n') |> Array.indexed do
            budget.Visit 0
            let tokens = tokenizer.CreateLineTokenizer line
            let mutable scanning = true
            let comments = System.Text.StringBuilder()

            while scanning do
                budget.Visit 0
                let token, next = tokens.ScanToken state
                state <- next

                match token with
                | None -> scanning <- false
                | Some token ->
                    let r =
                        Range.mkRange
                            file
                            (Position.mkPos (lineNumber + 1) token.LeftColumn)
                            (Position.mkPos (lineNumber + 1) (token.RightColumn + 1))

                    let value =
                        line.Substring(
                            token.LeftColumn,
                            min (line.Length - token.LeftColumn) (token.RightColumn - token.LeftColumn + 1)
                        )

                    if token.ColorClass = FSharpTokenColorKind.Comment then
                        comments.Append(value) |> ignore
                    elif
                        value.StartsWith("#", StringComparison.Ordinal)
                        && token.ColorClass <> FSharpTokenColorKind.String
                    then
                        error
                            "BUILD001"
                            r
                            "Authored compiler directives are prohibited."
                            "Use the platform's fixed compilation policy."
                    elif value = "<|" || value = "<||" || value = "<|||" then
                        error
                            "STYLE001"
                            r
                            "Backward pipes are prohibited, including function values."
                            "Use ordinary application or |> ."
                    elif value = "||>" || value = "|||>" then
                        error
                            "STYLE002"
                            r
                            "Tuple pipes are outside the approved vocabulary."
                            "Use ordinary curried application."

            let comment = comments.ToString().TrimStart('/', '(', '*', ' ')

            if
                comment.StartsWith("fsharplint:disable", StringComparison.OrdinalIgnoreCase)
                || comment.StartsWith("fsharp-guard:disable", StringComparison.OrdinalIgnoreCase)
                || comment.StartsWith("fsharpanalyzer:", StringComparison.OrdinalIgnoreCase)
            then
                error
                    "BUILD001"
                    (Range.mkRange
                        file
                        (Position.mkPos (lineNumber + 1) 0)
                        (Position.mkPos (lineNumber + 1) line.Length))
                    "Source comments cannot suppress mandatory analysis."
                    "Remove the suppression."

        let rec walk inList typeDepth depth (node: obj | null) =
            budget.Visit depth

            match node with
            | Null -> ()
            | NonNull node ->
                let typeDepth = if node :? SynType then typeDepth + 1 else typeDepth

                match node with
                | :? SynType as t when typeDepth > SafetyLimits.typeDepth ->
                    error
                        "BUILD003"
                        t.Range
                        "Type nesting exceeds the supported analysis depth."
                        "Use a supported immutable data shape."

                    invalidOp "Unsupported type nesting prevented complete analysis."
                | _ -> ()

                let mutable listContext = inList

                match node with
                | :? SynExpr as expr ->
                    let deny id message alternative = error id expr.Range message alternative

                    match expr with
                    | SynExpr.ArrayOrListComputed(false, _, r) ->
                        listContext <- true
                        comprehensions.Add r
                    | SynExpr.Upcast _
                    | SynExpr.InferredUpcast _ -> coercions.Add expr.Range
                    | SynExpr.ArrayOrList(true, _, _)
                    | SynExpr.ArrayOrListComputed(true, _, _) ->
                        deny "MUT008" "Arrays are mutable application data." "Use an immutable list."
                    | SynExpr.While _
                    | SynExpr.WhileBang _ ->
                        deny "MUT009" "While loops are prohibited." "Use pure transformations or a platform operation."
                    | SynExpr.For _
                    | SynExpr.ForEach _ when not inList ->
                        deny
                            "MUT009"
                            "Statement for loops are prohibited."
                            "Use List.map or an immutable list comprehension."
                    | SynExpr.LongIdentSet _
                    | SynExpr.DotSet _
                    | SynExpr.Set _
                    | SynExpr.DotIndexedSet _
                    | SynExpr.NamedIndexedPropertySet _
                    | SynExpr.DotNamedIndexedPropertySet _ ->
                        deny "MUT002" "Assignment and setters are prohibited." "Construct a new immutable value."
                    | SynExpr.Downcast _
                    | SynExpr.InferredDowncast _ ->
                        deny "CAST001" "Downcasts are prohibited." "Model alternatives with a discriminated union."
                    | SynExpr.TypeTest _ ->
                        deny "CAST005" "Runtime type dispatch is prohibited." "Match explicit union cases."
                    | SynExpr.Null _ -> deny "NULL001" "Authored null values are prohibited." "Use Option for absence."
                    | SynExpr.TryWith(withCases = clauses) ->
                        handlers.Add clauses

                        deny
                            "ERR001"
                            "Application code cannot catch exceptions."
                            "Use Result and approved platform adapters."
                    | SynExpr.TryFinally _ ->
                        deny
                            "ERR009"
                            "Resource cleanup belongs to a platform adapter."
                            "Describe the operation through the SDK."
                    | SynExpr.Assert _ ->
                        deny "ERR002" "Assertions cannot implement business validation." "Return a modeled error."
                    | SynExpr.AddressOf _
                    | SynExpr.Fixed _ ->
                        deny
                            "NATIVE003"
                            "Authored address and native-memory operations are prohibited."
                            "Keep memory ownership inside a platform adapter."
                    | SynExpr.Quote _ ->
                        deny
                            "REFL004"
                            "Quoted executable code is outside the supported application language."
                            "Use checked pure functions."
                    | SynExpr.Dynamic _ ->
                        deny "REFL002" "Dynamic invocation is prohibited." "Use an approved typed API."
                    | SynExpr.ObjExpr _ -> () // The typed interface and every method body are checked by SafetyAnalysis.
                    | SynExpr.Lazy _ ->
                        deny
                            "EFFECT003"
                            "Lazy executable values cannot enter application data."
                            "Compute an immutable value explicitly."
                    | SynExpr.TraitCall _ ->
                        deny
                            "EFFECT001"
                            "Authored statically resolved dispatch has no approved contract."
                            "Use an approved named operation."
                    | SynExpr.Match(_, value, clauses, r, _)
                    | SynExpr.MatchBang(_, value, clauses, r, _) ->
                        matches.Add
                            { Scrutinee = Some value.Range
                              Expression = Some value
                              Range = r
                              Clauses = clauses }
                    | SynExpr.MatchLambda(_, _, clauses, _, r) ->
                        matches.Add
                            { Scrutinee = None
                              Expression = None
                              Range = r
                              Clauses = clauses }
                    | SynExpr.Sequential(_, true, first, _, _, _) -> discards.Add first.Range
                    | SynExpr.Do(value, _) -> discards.Add value.Range
                    | SynExpr.LibraryOnlyILAssembly _
                    | SynExpr.LibraryOnlyStaticOptimization _
                    | SynExpr.LibraryOnlyUnionCaseFieldGet _
                    | SynExpr.LibraryOnlyUnionCaseFieldSet _
                    | SynExpr.ArbitraryAfterError _
                    | SynExpr.FromParseError _
                    | SynExpr.DiscardAfterMissingQualificationAfterDot _ ->
                        deny
                            "BUILD003"
                            "Unsupported or incomplete F# syntax."
                            "Use the supported F# application language."
                    | _ ->
                        // Exhaustive versioned compatibility inventory; a new FCS node fails closed.
                        let allowed =
                            set
                                [ "Paren"
                                  "Const"
                                  "Typed"
                                  "Tuple"
                                  "AnonRecd"
                                  "ArrayOrList"
                                  "Record"
                                  "New"
                                  "For"
                                  "ForEach"
                                  "IndexRange"
                                  "IndexFromEnd"
                                  "ComputationExpr"
                                  "Lambda"
                                  "App"
                                  "TypeApp"
                                  "IfThenElse"
                                  "Typar"
                                  "Ident"
                                  "LongIdent"
                                  "DotGet"
                                  "DotLambda"
                                  "DotIndexedGet"
                                  "Upcast"
                                  "InferredUpcast"
                                  "JoinIn"
                                  "ImplicitZero"
                                  "Sequential"
                                  "SequentialOrImplicitYield"
                                  "YieldOrReturn"
                                  "YieldOrReturnFrom"
                                  "LetOrUse"
                                  "DoBang"
                                  "InterpolatedString"
                                  "DebugPoint" ]

                        if not (Set.contains (unionName expr) allowed) then
                            deny
                                "BUILD003"
                                "Unclassified syntax node."
                                "Update and test the platform's compiler compatibility policy."
                | :? SynBinding as binding ->
                    let (SynBinding(_, _, _, isMutable, _, _, _, pattern, _, expression, r, _, _)) =
                        binding

                    let expressionRange = expression.Range
                    bindings.Add(Range.mkRange r.FileName r.Start expressionRange.End)

                    if isMutable then
                        error "MUT001" r "Mutable bindings are prohibited." "Pass immutable state explicitly."

                    match pattern with
                    | SynPat.Wild _ -> discards.Add expression.Range
                    | _ -> ()
                | :? SynLetOrUse as binding ->
                    // This pinned FCS representation includes ordinary/bang use flags.
                    if binding.IsUse then
                        for SynBinding(headPat = pattern) in binding.Bindings do
                            resourceBindings.Add pattern.Range

                        error
                            "ERR009"
                            binding.Range
                            "Resource acquisition belongs to a platform adapter."
                            "Use a declared platform operation."
                | :? SynField as field ->
                    let (SynField(_, _, _, _, isMutable, _, _, r, _)) = field

                    if isMutable then
                        error "MUT003" r "Mutable fields are prohibited." "Use an immutable field."
                | :? SynMemberDefn as memberDef ->
                    match memberDef with
                    | SynMemberDefn.AutoProperty(range = r) ->
                        bindings.Add r

                        error
                            "MUT003"
                            r
                            "Application object properties are outside the data-only model."
                            "Use an immutable record."
                    | _ -> ()
                | :? SynExceptionDefnRepr as ex ->
                    error "ERR002" ex.Range "Application exception declarations are prohibited." "Use an error union."
                | :? SynAttribute as attr -> attrs.Add attr
                | :? SynTypeDefn as definition -> types.Add definition.Range
                | :? SynModuleDecl as declaration ->
                    match declaration with
                    | SynModuleDecl.NestedModule(range = r) -> modules.Add r
                    | _ -> ()
                | :? SynModuleOrNamespace as declaration -> modules.Add declaration.Range
                | :? SynPat as pattern ->
                    match pattern with
                    | SynPat.IsInst(_, r) ->
                        error "CAST005" r "Runtime type patterns are prohibited." "Match explicit union cases."
                    | SynPat.Null r ->
                        error
                            "NULL001"
                            r
                            "Null matching requires an approved normalizer."
                            "Use normalized Option values."
                    | _ -> ()
                | :? ParsedHashDirective as directive ->
                    let (ParsedHashDirective(_, _, r)) = directive

                    error
                        "BUILD001"
                        r
                        "Compiler directives cannot change application policy."
                        "Use the platform's fixed compilation policy."
                | _ -> ()

                for child in children node do
                    walk listContext typeDepth (depth + 1) child

        walk false 0 0 (box tree)

        { Attributes = List.ofSeq attrs
          Matches = List.ofSeq matches
          Discards = List.ofSeq discards
          Comprehensions = List.ofSeq comprehensions
          ExplicitCoercions = List.ofSeq coercions
          Types = List.ofSeq types
          Modules = List.ofSeq modules
          Bindings = List.ofSeq bindings
          ResourceBindings = List.ofSeq resourceBindings
          Handlers = List.ofSeq handlers }
