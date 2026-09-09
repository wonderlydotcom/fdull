namespace FDull

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

/// Shared analysis engine. Authoritative callers use Safety.check, which runs
/// this engine in the bounded worker. Editor/unit-test callers cannot certify builds.
module SafetyEngine =
    let private checker = lazy (FSharpChecker.Create(keepAssemblyContents = true))

    let check references directory (output: string) files =
        let budget = SafetyBudget()
        let diagnostics = ResizeArray<SafetyDiagnostic>()
        let mutable complete = false
        let mutable exitCode = 0
        let mutable sources = []
        let mutable referenceInputs = []

        let flags = SafetyPolicy.arguments references directory output

        let emit id (r: range) symbol message alternative =
            if not (SafetyRules.knownDiagnostic id) then
                invalidOp ("Unknown diagnostic classification: " + id)

            if id <> "BUILD003" && diagnostics.Count >= SafetyLimits.diagnosticCount then
                invalidOp "Analysis exceeded the supported diagnostic budget."

            diagnostics.Add
                { Rule = id
                  Severity = "error"
                  File = r.FileName
                  StartLine = max 1 r.StartLine
                  StartColumn = r.StartColumn + 1
                  EndLine = max 1 r.EndLine
                  EndColumn = r.EndColumn + 1
                  Project = "fdull.source"
                  Profile = "PURE"
                  Symbol = symbol
                  Message = message
                  Alternative = alternative
                  Policy = SafetyPolicy.version }

        let globalError id message =
            emit
                id
                (Range.mkRange directory (Position.mkPos 1 0) (Position.mkPos 1 1))
                ""
                message
                "Run the pinned platform toolchain with complete inputs."

        try
            SafetyPolicy.validate ()

            if
                typeof<FSharpChecker>.Assembly.FullName <> SafetyPolicy.fcsIdentity
                || typeof<unit>.Assembly.FullName <> SafetyPolicy.coreIdentity
            then
                exitCode <- 2
                globalError "BUILD002" "The FCS/Core identities differ from the tested toolchain."
            elif
                List.isEmpty files
                || List.isEmpty references
                || (Set.ofList files).Count <> List.length files
                || files.Length > SafetyLimits.sourceCount
                || references.Length > SafetyLimits.referenceCount
            then
                exitCode <- 3
                globalError "BUILD003" "Analysis requires nonempty, unique, ordered sources and complete references."
            elif (files @ references |> List.exists (File.Exists >> not)) then
                exitCode <- 3
                globalError "BUILD003" "A source or reference is missing."
            elif
                files
                |> List.exists (fun file -> FileInfo(file).Length > SafetyLimits.sourceBytes)
            then
                exitCode <- 3
                globalError "BUILD003" "A source exceeds the supported analysis size."
            else
                sources <-
                    files
                    |> List.map (fun p ->
                        { Path = p
                          Digest = SafetyPolicy.fileDigest p })

                referenceInputs <-
                    references
                    |> List.map (fun p ->
                        { Path = p
                          Digest = SafetyPolicy.fileDigest p })

                let commandOptions =
                    checker.Value.GetProjectOptionsFromCommandLineArgs(
                        Path.Combine(directory, "Product.fsproj"),
                        List.toArray flags
                    )

                let options =
                    { commandOptions with
                        SourceFiles = List.toArray files }

                if
                    options.SourceFiles |> Array.toList <> files
                    || options.IsIncompleteTypeCheckEnvironment
                then
                    exitCode <- 3
                    globalError "BUILD003" "FCS did not load the exact complete compilation inputs."
                else
                    let parsingOptions, optionErrors =
                        checker.Value.GetParsingOptionsFromProjectOptions options

                    for error in optionErrors do
                        globalError "BUILD003" error.Message

                    let syntax =
                        files
                        |> List.map (fun file ->
                            let source = File.ReadAllText file

                            let parsed =
                                checker.Value.ParseFile(file, SourceText.ofString source, parsingOptions)
                                |> fun a -> Async.RunSynchronously(a, budget.RemainingMilliseconds)

                            file, SafetySyntax.inspect budget file source parsed.ParseTree emit)
                        |> Map.ofList

                    if diagnostics |> Seq.exists (fun d -> d.Rule = "BUILD003") then
                        invalidOp "Unsupported source syntax prevented complete typed analysis."

                    let checkedProject =
                        checker.Value.ParseAndCheckProject options
                        |> fun a -> Async.RunSynchronously(a, budget.RemainingMilliseconds)

                    for error in checkedProject.Diagnostics do
                        if
                            error.Severity = FSharpDiagnosticSeverity.Error
                            || error.Severity = FSharpDiagnosticSeverity.Warning
                        then
                            emit
                                ("FS" + error.ErrorNumber.ToString("0000"))
                                error.Range
                                ""
                                error.Message
                                "Fix the compiler diagnostic; warnings are errors."

                    if
                        checkedProject.HasCriticalErrors
                        || checkedProject.Diagnostics
                           |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                    then
                        exitCode <- 3
                        globalError "BUILD003" "Compiler errors prevented complete typed analysis."
                    elif
                        checkedProject.AssemblyContents.ImplementationFiles.Length
                        <> (files |> List.filter (fun p -> Path.GetExtension p = ".fs") |> List.length)
                    then
                        exitCode <- 3
                        globalError "BUILD003" "The checked implementation inventory is incomplete."
                    else
                        SafetyAnalysis.inspect
                            budget
                            SafetyContext.application
                            (Set.ofList files)
                            checkedProject
                            syntax
                            emit

                        complete <- true

                    if
                        sources @ referenceInputs
                        |> List.exists (fun input -> SafetyPolicy.fileDigest input.Path <> input.Digest)
                    then
                        complete <- false
                        exitCode <- 4
                        globalError "BUILD004" "A source/reference changed during analysis."
        with error ->
            complete <- false
            exitCode <- 3
            globalError "BUILD003" ("Analysis failed: " + error.Message)
        // Syntax and semantic passes can discover unsupported inputs after FCS
        // succeeds. Such findings must retain incomplete-analysis semantics.
        if diagnostics |> Seq.exists (fun d -> d.Rule = "BUILD003") then
            complete <- false

            if exitCode <> 4 then
                exitCode <- 3

        let diagnostics =
            diagnostics
            |> Seq.distinct
            |> Seq.sortBy (fun d -> d.File, d.StartLine, d.StartColumn, d.Rule, d.Symbol)
            |> Seq.toList

        { Policy = SafetyPolicy.version
          Tool = typeof<SafetyReport>.Assembly.Location |> SafetyPolicy.fileDigest
          Complete = complete
          ExitCode =
            if exitCode <> 0 then exitCode
            elif not complete then 3
            elif not diagnostics.IsEmpty then 1
            else 0
          Sources = sources
          References = referenceInputs
          Arguments = flags
          Diagnostics = diagnostics }
