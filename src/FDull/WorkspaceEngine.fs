namespace FDull

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

type WorkspaceRequest =
    { Root: string
      Project: string
      ProjectReferences: string list
      Arguments: string list
      Sources: SafetyInput list
      References: SafetyInput list
      Generated: string list
      BuildInputs: SafetyInput list
      Policy: SafetyInput }

type WorkspaceProjectReport =
    { Project: string
      ProjectReferences: string list
      Report: SafetyReport
      Uses: WorkspaceUse list }

/// The parent validates process status and every diagnostic/use before trusting a report.
module WorkspaceProtocol =
    let validate
        (request: WorkspaceRequest)
        (policy: WorkspacePolicyDocument)
        processExit
        stderr
        (result: WorkspaceProjectReport)
        =
        let report = result.Report
        let files = request.Project :: (request.Sources |> List.map _.Path) |> Set.ofList

        let useFiles =
            (policy.Sources |> List.map _.File)
            @ (request.Generated
               |> List.map (fun file -> Path.GetRelativePath(request.Root, file).Replace('\\', '/')))
            |> Set.ofList

        if
            result.Project <> request.Project
            || result.ProjectReferences <> request.ProjectReferences
            || report.ExitCode <> processExit
            || not (List.contains report.ExitCode [ 0; 1; 3 ])
            || (report.ExitCode = 0
                && (not report.Complete
                    || not report.Diagnostics.IsEmpty
                    || not (String.IsNullOrEmpty stderr)))
            || (report.ExitCode = 1 && (not report.Complete || report.Diagnostics.IsEmpty))
            || (report.ExitCode = 3 && (report.Complete || report.Diagnostics.IsEmpty))
            || report.Diagnostics.Length > 4097
            || report.Diagnostics
               |> List.exists (fun diagnostic ->
                   diagnostic.Severity <> "error"
                   || diagnostic.Policy <> request.Policy.Digest
                   || diagnostic.Project <> request.Project
                   || not (Set.contains diagnostic.Profile WorkspacePolicy.profiles)
                   || not (Set.contains diagnostic.File files)
                   || not (SafetyRules.knownDiagnostic diagnostic.Rule)
                   || String.IsNullOrWhiteSpace diagnostic.Message
                   || diagnostic.StartLine < 1
                   || diagnostic.StartColumn < 1
                   || diagnostic.EndColumn < 1
                   || (diagnostic.EndLine, diagnostic.EndColumn) < (diagnostic.StartLine, diagnostic.StartColumn)
                   || (diagnostic.Rule = "BUILD003" && report.Complete))
            || result.Uses
               |> List.exists (fun item ->
                   not (Set.contains item.File useFiles)
                   || String.IsNullOrWhiteSpace item.Owner
                   || String.IsNullOrWhiteSpace item.Identity
                   || not (List.contains item.Kind [ "member"; "type"; "rule"; "profile"; "attribute"; "constructor" ]))
        then
            Error "BUILD003: Inconsistent workspace worker result."
        else
            Ok()

module WorkspaceEngine =
    let private checker = lazy (FSharpChecker.Create(keepAssemblyContents = true))

    let check (request: WorkspaceRequest) =
        let budget =
            SafetyBudget(maximumSteps = 2000000, maximumDepth = 512, milliseconds = 45000)

        let diagnostics = ResizeArray<SafetyDiagnostic>()
        let mutable complete = false
        let mutable uses = []
        let mutable observations = fun () -> []

        let initialContext =
            { SafetyContext.application with
                Project = request.Project }

        let emitWith context rule (r: range) symbol message alternative =
            if not (SafetyRules.knownDiagnostic rule) then
                invalidOp "Unknown diagnostic classification."

            if not (context.Permit rule r symbol) then
                if diagnostics.Count >= 4096 then
                    invalidOp "Workspace diagnostic budget exceeded."

                diagnostics.Add
                    { Rule = rule
                      Severity = "error"
                      File = r.FileName
                      StartLine = max 1 r.StartLine
                      StartColumn = r.StartColumn + 1
                      EndLine = max 1 r.EndLine
                      EndColumn = r.EndColumn + 1
                      Project = request.Project
                      Profile = context.Profile r
                      Symbol = symbol
                      Message = message
                      Alternative = alternative
                      Policy = request.Policy.Digest }

        let globalError rule message =
            emitWith
                initialContext
                rule
                (Range.mkRange request.Project (Position.mkPos 1 0) (Position.mkPos 1 1))
                ""
                message
                "Use complete protected workspace inputs."

        try
            if
                typeof<FSharpChecker>.Assembly.FullName <> SafetyPolicy.fcsIdentity
                || typeof<unit>.Assembly.FullName <> SafetyPolicy.coreIdentity
            then
                invalidOp "BUILD002: The compiler/Core identities differ from FDull's tested toolchain."

            let allInputs =
                request.Policy :: request.Sources @ request.References @ request.BuildInputs

            let unchanged () =
                allInputs
                |> List.forall (fun input -> SafetyPolicy.fileDigest input.Path = input.Digest)

            if not (unchanged ()) then
                invalidOp "Workspace inputs changed before analysis."

            match WorkspaceCompilerPolicy.validate request.Arguments with
            | Error error -> invalidOp error
            | Ok() -> ()

            let policy = WorkspacePolicy.read request.Root

            let buildInputs =
                policy.Inputs
                |> List.map (fun pin ->
                    { Path = Path.Combine(request.Root, pin.File)
                      Digest = pin.Digest })

            if request.BuildInputs <> buildInputs then
                invalidOp "BUILD004: Worker build inputs differ from the protected inventory."

            let project = Path.GetRelativePath(request.Root, request.Project).Replace('\\', '/')

            match WorkspaceAudit.validateEdges policy.Projects project request.ProjectReferences with
            | Error error -> invalidOp error
            | Ok() -> ()

            let expected =
                policy.Sources
                |> List.filter (fun source -> source.Project = project)
                |> List.map (fun source -> Path.GetFullPath(source.File, request.Root))
                |> Set.ofList

            let projectDirectory =
                Path.GetDirectoryName request.Project
                |> FDull.Transport.External.required "workspace.project-parent"

            let requiredGenerated =
                [ Path.Combine(projectDirectory, "obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.fs")
                  Path.Combine(
                      projectDirectory,
                      "obj/Release/net10.0/"
                      + Path.GetFileNameWithoutExtension(request.Project)
                      + ".AssemblyInfo.fs"
                  ) ]
                |> Set.ofList

            let sourceFiles = request.Sources |> List.map _.Path |> Set.ofList

            let generated =
                match
                    WorkspaceGenerated.supported
                        request.Root
                        projectDirectory
                        request.Project
                        request.ProjectReferences
                        (request.Sources |> List.map _.Path)
                        (request.References |> List.map _.Path)
                with
                | Error error -> invalidOp error
                | Ok supported -> Set.intersect sourceFiles supported

            if
                expected.IsEmpty
                || (sourceFiles |> Set.filter WorkspaceGenerated.isTestSdkProgram).Count > 1
                || not (Set.isSubset requiredGenerated generated)
                || Set.ofList request.Generated <> generated
                || sourceFiles <> Set.union expected generated
            then
                invalidOp "BUILD004: Exported sources differ from the exact project and generated-source inventory."

            if
                request.Sources.IsEmpty
                || request.Sources.Length > SafetyLimits.sourceCount
                || request.References.IsEmpty
                || request.References.Length > SafetyLimits.referenceCount
                || request.Sources
                   |> List.exists (fun source -> FileInfo(source.Path).Length > SafetyLimits.sourceBytes)
            then
                invalidOp "Unsupported workspace input inventory."

            let files = request.Sources |> List.map _.Path

            let options =
                checker.Value.GetProjectOptionsFromCommandLineArgs(request.Project, List.toArray request.Arguments)

            let options =
                { options with
                    SourceFiles = List.toArray files }

            let parsing, errors = checker.Value.GetParsingOptionsFromProjectOptions options

            if not errors.IsEmpty then
                invalidOp "Incomplete project compiler options."

            let syntaxDiagnostics = ResizeArray<_>()

            let syntax =
                files
                |> List.map (fun file ->
                    let source = File.ReadAllText file

                    let parsed =
                        checker.Value.ParseFile(file, SourceText.ofString source, parsing)
                        |> fun work -> Async.RunSynchronously(work, budget.RemainingMilliseconds)

                    let inspect =
                        SafetySyntax.inspect budget file source parsed.ParseTree (fun a b c d e ->
                            syntaxDiagnostics.Add(a, b, c, d, e))

                    file, inspect)
                |> Map.ofList

            let result =
                checker.Value.ParseAndCheckProject options
                |> fun work -> Async.RunSynchronously(work, budget.RemainingMilliseconds)

            let resolved, observed =
                WorkspacePolicy.context
                    request.Root
                    request.Project
                    (Set.ofList request.Generated)
                    budget
                    policy
                    result
                    syntax

            let context = resolved
            let emit = emitWith context
            observations <- observed

            for rule, r, symbol, message, alternative in syntaxDiagnostics do
                emit rule r symbol message alternative

            for diagnostic in result.Diagnostics do
                if
                    diagnostic.Severity = FSharpDiagnosticSeverity.Error
                    || diagnostic.Severity = FSharpDiagnosticSeverity.Warning
                then
                    emit
                        ("FS" + diagnostic.ErrorNumber.ToString("0000"))
                        diagnostic.Range
                        ""
                        diagnostic.Message
                        "Fix the compiler diagnostic."

            if
                result.HasCriticalErrors
                || result.Diagnostics
                   |> Array.exists (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)
            then
                globalError "BUILD003" "Compiler errors prevented complete workspace analysis."
            elif
                result.AssemblyContents.ImplementationFiles.Length
                <> (files |> List.filter (fun file -> Path.GetExtension file = ".fs") |> List.length)
            then
                globalError "BUILD003" "The compiler implementation inventory differs from the exported sources."
            else
                SafetyAnalysis.inspect budget context (Set.ofList files) result syntax emit
                complete <- true

            uses <- observed ()

            if not (unchanged ()) then
                complete <- false
                globalError "BUILD004" "Workspace inputs changed during analysis."
        with error ->
            complete <- false

            let classification =
                [ "BUILD002"
                  "BUILD004"
                  "BUILD005"
                  "ARCH001"
                  "ARCH002"
                  "ARCH003"
                  "ARCH004" ]
                |> List.tryFind (fun rule -> error.Message.StartsWith(rule + ":", StringComparison.Ordinal))
                |> Option.defaultValue "BUILD003"

            globalError classification error.Message

        uses <- observations ()

        if diagnostics |> Seq.exists (fun diagnostic -> diagnostic.Rule = "BUILD003") then
            complete <- false

        let ordered =
            diagnostics
            |> Seq.distinct
            |> Seq.sortBy (fun d -> d.File, d.StartLine, d.StartColumn, d.Rule, d.Symbol)
            |> Seq.toList

        { Project = request.Project
          ProjectReferences = request.ProjectReferences
          Report =
            { Policy = request.Policy.Digest
              Tool = SafetyPolicy.fileDigest typeof<SafetyReport>.Assembly.Location
              Complete = complete
              ExitCode =
                if not complete then 3
                elif ordered.IsEmpty then 0
                else 1
              Sources = request.Sources
              References = request.References
              Arguments = request.Arguments
              Diagnostics = ordered }
          Uses = uses }
