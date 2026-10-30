namespace FDull

open System
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text

type WorkspaceRequest =
    {
        Root: string
        Project: string
        ProjectReferences: string list
        Arguments: string list
        Sources: SafetyInput list
        References: SafetyInput list
        Generated: string list
        BuildInputs: SafetyInput list
        /// Evaluated repository-local MSBuild imports, all bound to reviewed build pins.
        ImportedBuildInputs: SafetyInput list
        Policy: SafetyInput
        /// Digest of the policy slice the project's analysis can observe; reports carry it as `Policy`.
        PolicySlice: string
    }

type WorkspaceProjectReport =
    {
        Project: string
        ProjectReferences: string list
        Report: SafetyReport
        Uses: WorkspaceUse list
        /// Set only by the parent, after a cached response passes the same checks as a fresh one.
        Cached: bool
    }

/// A distinct worker response prevents advisory source analysis from certifying a build.
type WorkspaceReviewProjectReport =
    { Advisory: bool
      Analysis: WorkspaceProjectReport
      Findings: WorkspaceReviewFinding list }

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
            || result.Cached
            || result.ProjectReferences <> request.ProjectReferences
            || report.ExitCode <> processExit
            || not (List.contains report.ExitCode [ 0; 1; 3 ])
            || (report.ExitCode = 0
                && (not report.Complete
                    || not report.Diagnostics.IsEmpty
                    || not (String.IsNullOrEmpty stderr)))
            || (report.ExitCode = 1 && (not report.Complete || report.Diagnostics.IsEmpty))
            || (report.ExitCode = 3 && (report.Complete || report.Diagnostics.IsEmpty))
            || report.Diagnostics.Length > SafetyLimits.workspaceDiagnosticCount + 1
            || report.Diagnostics
               |> List.exists (fun diagnostic ->
                   diagnostic.Severity <> "error"
                   || diagnostic.Policy <> request.PolicySlice
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
                   || not (
                       List.contains
                           item.Kind
                           [ "member"
                             "type"
                             "rule"
                             "capture"
                             "profile"
                             "attribute"
                             "constructor"
                             "data"
                             "data-assembly" ]
                   )
                   || (List.contains item.Kind [ "data"; "data-assembly" ]
                       && (item.Owner
                           <> Path.GetRelativePath(request.Root, request.Project).Replace('\\', '/')
                           || not (
                               policy.Sources
                               |> List.exists (fun source -> source.File = item.File && source.Project = item.Owner)
                           ))))
        then
            Error "BUILD003: Inconsistent workspace worker result."
        else
            Ok()

module WorkspaceEngine =
    let private checker = lazy (FSharpChecker.Create(keepAssemblyContents = true))

    /// Compile the exact source/reference invocation into a private temporary
    /// output. ParseAndCheckProject does not report every compiler diagnostic.
    let private compileForDiagnostics (budget: SafetyBudget) (request: WorkspaceRequest) files =
        let outputs =
            request.Arguments
            |> List.choose (fun argument ->
                if argument.StartsWith("-o:", StringComparison.Ordinal) then
                    Some(argument.Substring 3)
                else
                    None)

        match outputs with
        | [ output ] ->
            let temporary = Directory.CreateTempSubdirectory("fdull-compiler-")

            try
                let destination =
                    Path.GetFileName output
                    |> FDull.Transport.External.required "workspace.compiler-output"
                    |> fun name -> Path.Combine(temporary.FullName, name)

                let arguments =
                    request.Arguments
                    |> List.map (fun argument ->
                        if argument.StartsWith("-o:", StringComparison.Ordinal) then
                            "-o:" + destination
                        elif argument.StartsWith("--doc:", StringComparison.Ordinal) then
                            let name =
                                Path.GetFileName(argument.Substring 6)
                                |> FDull.Transport.External.required "workspace.compiler-documentation"

                            "--doc:" + Path.Combine(temporary.FullName, name)
                        else
                            argument)

                // FCS runs outside the SDK compiler host. For executables its
                // implicit manifest lookup points at the runtime directory,
                // which has no default.win32manifest. The manifest cannot
                // affect source diagnostics, so suppress it for this private pass.
                let manifest =
                    if List.contains "--target:exe" arguments then
                        [ "--nowin32manifest" ]
                    else
                        []

                let command = [ "fsc.exe" ] @ arguments @ manifest @ files |> List.toArray

                let diagnostics, failure =
                    checker.Value.Compile command
                    |> fun work -> Async.RunSynchronously(work, budget.RemainingMilliseconds)

                diagnostics, failure, File.Exists destination
            finally
                temporary.Delete true
        | _ -> invalidOp "BUILD002: The exported compiler invocation must have one output."

    let private analyze advisory whole (request: WorkspaceRequest) =
        let budget =
            SafetyBudget(maximumSteps = 2000000, maximumDepth = 512, milliseconds = 45000)

        let diagnostics = ResizeArray<SafetyDiagnostic>()
        let mutable complete = false
        let mutable uses = []
        let mutable observations = fun () -> []
        let findings = ResizeArray<WorkspaceReviewFinding>()

        let describeGlobal =
            fun diagnostic (_: range) (_: SafetyReviewContract option) ->
                { Diagnostic = diagnostic
                  Owner = "<workspace>"
                  Kind = "rule"
                  Identity = diagnostic.Rule + " | " + diagnostic.Symbol
                  Resolution = "analysis-blocked" }

        let initialContext =
            { SafetyContext.application with
                Project = request.Project }

        let emitWith limit context describe rule (r: range) symbol message alternative contract =
            if not (SafetyRules.knownDiagnostic rule) then
                invalidOp "Unknown diagnostic classification."

            if not (context.Permit rule r symbol contract) then
                if diagnostics.Count >= limit then
                    invalidOp "Workspace diagnostic budget exceeded."

                let diagnostic =
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
                      Policy = request.PolicySlice }

                diagnostics.Add diagnostic

                if advisory then
                    findings.Add(describe diagnostic r contract)

        let globalError rule message =
            emitWith
                (SafetyLimits.workspaceDiagnosticCount + 1)
                initialContext
                describeGlobal
                rule
                (Range.mkRange request.Project (Position.mkPos 1 0) (Position.mkPos 1 1))
                ""
                message
                "Use complete protected workspace inputs."
                None

        try
            if
                typeof<FSharpChecker>.Assembly.FullName <> SafetyPolicy.fcsIdentity
                || typeof<unit>.Assembly.FullName <> SafetyPolicy.coreIdentity
            then
                invalidOp "BUILD002: The compiler/Core identities differ from FDull's tested toolchain."

            let allInputs =
                request.Policy :: request.Sources
                @ request.References
                @ request.BuildInputs
                @ request.ImportedBuildInputs

            let unchanged () =
                allInputs
                |> List.forall (fun input -> SafetyPolicy.fileDigest input.Path = input.Digest)

            if not (unchanged ()) then
                invalidOp "Workspace inputs changed before analysis."

            match WorkspaceCompilerPolicy.validate request.Arguments with
            | Error error -> invalidOp error
            | Ok() -> ()

            let project = Path.GetRelativePath(request.Root, request.Project).Replace('\\', '/')

            let document =
                if advisory then
                    WorkspacePolicy.readForReview request.Root
                else
                    WorkspacePolicy.read request.Root

            if WorkspaceSlice.digest project document <> request.PolicySlice then
                invalidOp "BUILD004: Worker policy slice differs from the requested slice."

            let policy =
                if whole then
                    document
                else
                    WorkspaceSlice.create project document

            let buildInputs =
                WorkspacePolicy.buildInputPaths request.Root policy
                |> List.map (fun (path, digest) -> { Path = path; Digest = digest })

            if request.BuildInputs <> buildInputs then
                invalidOp "BUILD004: Worker build inputs differ from the protected inventory."

            let protectedImports = buildInputs |> Set.ofList

            if
                request.ImportedBuildInputs
                |> List.exists (fun item -> not (Set.contains item protectedImports))
            then
                invalidOp "BUILD004: Evaluated repository import lacks a reviewed build pin."

            match WorkspaceAudit.validatePolicyEdges request.Root policy project request.ProjectReferences with
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

            let resolved, observed, explain =
                WorkspacePolicy.context
                    request.Root
                    request.Project
                    (Set.ofList request.Generated)
                    request.References
                    budget
                    policy
                    result
                    syntax

            let context = resolved
            let emit = emitWith SafetyLimits.workspaceDiagnosticCount context explain
            observations <- observed

            let emitCompiler (diagnostic: FSharpDiagnostic) (location: range) =
                let rule = "FS" + diagnostic.ErrorNumber.ToString("0000")

                if Set.contains location.FileName sourceFiles then
                    emit rule location "" diagnostic.Message "Fix the compiler diagnostic." None
                else
                    // Compiler diagnostics can use a project, runtime or
                    // unknown location. They are blockers, not source-level
                    // permission uses, and must remain reportable by protocol.
                    emitWith
                        SafetyLimits.workspaceDiagnosticCount
                        initialContext
                        describeGlobal
                        rule
                        (Range.mkRange request.Project (Position.mkPos 1 0) (Position.mkPos 1 1))
                        ""
                        diagnostic.Message
                        "Fix the compiler diagnostic."
                        None

            for rule, r, symbol, message, alternative in syntaxDiagnostics do
                emit rule r symbol message alternative None

            for diagnostic in result.Diagnostics do
                if
                    diagnostic.Severity = FSharpDiagnosticSeverity.Error
                    || (diagnostic.Severity = FSharpDiagnosticSeverity.Warning
                        && not (SafetyPolicy.optionalCompilerWarning diagnostic))
                then
                    emitCompiler diagnostic diagnostic.Range

            let compileFailed =
                let compilerDiagnostics, failure, artifact =
                    compileForDiagnostics budget request files

                for diagnostic in compilerDiagnostics do
                    if
                        diagnostic.Severity = FSharpDiagnosticSeverity.Error
                        || diagnostic.Severity = FSharpDiagnosticSeverity.Warning
                    then
                        let r = diagnostic.Range

                        let compatibleAwaiter =
                            List.contains "--warnaserror-:3180" request.Arguments
                            && WorkspaceCompilerPolicy.compatibleTaskAwaiterWarning
                                diagnostic.ErrorNumber
                                diagnostic.Subcategory
                                r.FileName
                                r.StartLine
                                r.StartColumn
                                r.EndLine
                                r.EndColumn
                                diagnostic.Message

                        if
                            not (SafetyPolicy.optionalCompilerWarning diagnostic)
                            && (diagnostic.Severity <> FSharpDiagnosticSeverity.Warning || not compatibleAwaiter)
                        then
                            let location =
                                if Set.contains r.FileName sourceFiles then
                                    r
                                else
                                    Range.mkRange request.Project (Position.mkPos 1 0) (Position.mkPos 1 1)

                            emitCompiler diagnostic location

                failure.IsSome
                || not artifact
                || (compilerDiagnostics
                    |> Array.exists (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error))

            if
                result.HasCriticalErrors
                || compileFailed
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
                SafetyAnalysis.inspect budget context advisory (Set.ofList files) result syntax emit
                complete <- true

            uses <- observed ()

            let stablePolicy =
                if advisory then
                    WorkspacePolicy.readForReview request.Root
                else
                    WorkspacePolicy.read request.Root

            if stablePolicy <> document || not (unchanged ()) then
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

        let analysis =
            { Project = request.Project
              ProjectReferences = request.ProjectReferences
              Report =
                { Policy = request.PolicySlice
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
              Uses = uses
              Cached = false }

        { Advisory = advisory
          Analysis = analysis
          Findings =
            findings
            |> Seq.distinct
            |> Seq.sortBy (fun item ->
                item.Diagnostic.File,
                item.Diagnostic.StartLine,
                item.Diagnostic.StartColumn,
                item.Diagnostic.Rule,
                item.Identity)
            |> Seq.toList }

    let check request = (analyze false false request).Analysis

    let review request = analyze true false request

    /// Analyze with the whole policy document instead of the project's slice. This is
    /// equivalence evidence for the slice; workspace commands never call it.
    let checkWholePolicy request = (analyze false true request).Analysis

    let reviewWholePolicy request = analyze true true request
