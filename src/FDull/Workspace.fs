namespace FDull

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open FDull.Transport

type WorkspaceReport =
    { Complete: bool
      ExitCode: int
      Projects: WorkspaceProjectReport list
      ExcludedProjects: WorkspaceProjectExclusion list
      Errors: string list }

type WorkspaceInvocation =
    { Sources: string list
      References: string list
      Arguments: string list
      Generated: string list
      ProjectReferences: string list }

/// Wall-clock cost of one project's analysis. Diagnostic output only; never part of a report.
type WorkspaceTiming =
    { Project: string
      ExportMilliseconds: int64
      WorkerMilliseconds: int64
      ValidationMilliseconds: int64 }

module Workspace =
    let validatePolicy platform =
        try
            let root = Path.GetFullPath platform
            let policy = WorkspacePolicy.read root
            WorkspaceAudit.validate root policy
            Ok()
        with error ->
            Error error.Message

    let private input path =
        { Path = path
          Digest = SafetyPolicy.fileDigest path }

    let private runtimeRoot () =
        let rec parent count (directory: DirectoryInfo) =
            if count = 0 then
                directory.FullName
            else
                directory.Parent
                |> FDull.Transport.External.required "toolchain.runtime-parent"
                |> parent (count - 1)

        parent 3 (DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()))

    let private runTool directory arguments timeout output =
        let start =
            ProcessStartInfo(Path.Combine(runtimeRoot (), "dotnet"), WorkingDirectory = directory)

        for argument in arguments do
            start.ArgumentList.Add argument

        start.Environment["DOTNET_GCHeapHardLimit"] <- "20000000"

        SafetyProcess.run
            { Milliseconds = timeout
              OutputCharacters = output
              WorkingSetBytes = SafetyLimits.workingSetBytes }
            start
            ""

    let export root project =
        let path = Path.Combine(root, project)

        let directory =
            Path.GetDirectoryName path
            |> FDull.Transport.External.required "workspace.project-parent"

        let args =
            [ "msbuild"
              path
              "-t:Compile"
              "-p:Configuration=Release"
              "-p:ProvideCommandLineArgs=true"
              "-p:SkipCompilerExecution=true"
              "-p:NonExistentFile=obj/fdull-export-never-created"
              "-p:BuildProjectReferences=false"
              "-p:FDullExport=true"
              "-getItem:FscCommandLineArgs,ProjectReference"
              "-m:1"
              "-nr:false"
              "-nologo" ]

        match runTool root args 30000 SafetyLimits.messageCharacters with
        | Error error -> Error error
        | Ok result when result.ExitCode <> 0 -> Error(result.Error + result.Output)
        | Ok result ->
            use document = JsonDocument.Parse result.Output

            let arguments =
                document.RootElement.GetProperty("Items").GetProperty("FscCommandLineArgs").EnumerateArray()
                |> Seq.map (fun item ->
                    item.GetProperty("Identity").GetString()
                    |> FDull.Transport.External.required "workspace.argument")
                |> Seq.toList

            let full file = Path.GetFullPath(file, directory)

            let projectReferences =
                document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
                |> Seq.map (fun item ->
                    item.GetProperty("FullPath").GetString()
                    |> External.required "workspace.project-reference"
                    |> fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
                |> Seq.toList

            let sources =
                arguments
                |> List.filter (fun arg ->
                    not (arg.StartsWith("-", StringComparison.Ordinal))
                    && List.contains (Path.GetExtension arg) [ ".fs"; ".fsi" ])
                |> List.map full

            let references =
                arguments
                |> List.choose (fun arg ->
                    if arg.StartsWith("-r:", StringComparison.Ordinal) then
                        Some(full (arg.Substring 3))
                    else
                        None)

            match WorkspaceGenerated.supported root directory project projectReferences sources references with
            | Error error -> Error error
            | Ok generatedSet ->
                let generated = sources |> List.filter (fun file -> Set.contains file generatedSet)

                let unsupportedGenerated =
                    sources
                    |> List.filter (fun file ->
                        let relativeToProject = Path.GetRelativePath(directory, file).Replace('\\', '/')
                        let relativeToRoot = Path.GetRelativePath(root, file).Replace('\\', '/')

                        (relativeToProject.StartsWith("obj/Release/net10.0/", StringComparison.Ordinal)
                         || relativeToRoot = ".."
                         || relativeToRoot.StartsWith("../", StringComparison.Ordinal))
                        && not (Set.contains file generatedSet))

                let sourceSet = Set.ofList sources

                let flags =
                    arguments
                    |> List.filter (fun argument -> not (Set.contains (full argument) sourceSet))
                    |> List.map (fun argument ->
                        if argument.StartsWith("-r:", StringComparison.Ordinal) then
                            "-r:" + full (argument.Substring 3)
                        elif argument.StartsWith("-o:", StringComparison.Ordinal) then
                            "-o:" + full (argument.Substring 3)
                        else
                            argument)

                match unsupportedGenerated with
                | file :: _ ->
                    Error(
                        "BUILD003: MSBuild exported an unsupported generated F# source: "
                        + Path.GetRelativePath(root, file).Replace('\\', '/')
                    )
                | [] when
                    sources.IsEmpty
                    || references.IsEmpty
                    || (sources |> List.filter WorkspaceGenerated.isTestSdkProgram |> List.length) > 1
                    || (Set.ofList sources).Count <> sources.Length
                    ->
                    Error "BUILD003: MSBuild did not export a complete ordered invocation."
                | [] ->
                    Ok
                        { Sources = sources
                          References = references
                          Arguments = flags
                          Generated = generated
                          ProjectReferences = projectReferences }

    let private requestFrom advisory root project (invocation: WorkspaceInvocation) =
        { Root = root
          Project = Path.Combine(root, project)
          ProjectReferences = invocation.ProjectReferences
          Arguments = invocation.Arguments
          Sources = invocation.Sources |> List.map input
          References = invocation.References |> List.map input
          Generated = invocation.Generated
          BuildInputs =
            (if advisory then
                 WorkspacePolicy.readForReview root
             else
                 WorkspacePolicy.read root)
                .Inputs
            |> List.map (fun pin ->
                { Path = Path.Combine(root, pin.File)
                  Digest = pin.Digest })
          Policy = input (Path.Combine(root, "fdull.json")) }

    /// Trust a worker response only after it matches the request and passes protocol validation.
    let private accept advisory (request: WorkspaceRequest) engine closureChanged (output: SafetyProcessOutput) =
        let review =
            if advisory then
                Codec.decode<WorkspaceReviewProjectReport> output.Output
            else
                { Advisory = false
                  Analysis = Codec.decode<WorkspaceProjectReport> output.Output
                  Findings = [] }

        let result = review.Analysis
        let report = result.Report

        if
            report.Sources <> request.Sources
            || report.References <> request.References
            || report.Arguments <> request.Arguments
            || report.Policy <> request.Policy.Digest
            || report.Tool <> engine
            || closureChanged
            || request.Policy :: request.Sources @ request.References @ request.BuildInputs
               |> List.exists (fun expected -> input expected.Path <> expected)
        then
            Error "BUILD004: The workspace worker report differs from its requested inputs."
        else
            let validation =
                if advisory then
                    WorkspaceReview.validate
                        request
                        (WorkspacePolicy.readForReview request.Root)
                        output.ExitCode
                        output.Error
                        review
                else
                    WorkspaceProtocol.validate
                        request
                        (WorkspacePolicy.read request.Root)
                        output.ExitCode
                        output.Error
                        result

            validation |> Result.map (fun () -> review)

    let private analyzeTimed advisory (request: WorkspaceRequest) =
        let worker = Path.Combine(AppContext.BaseDirectory, "safety-worker")
        let engine = Path.Combine(worker, "FDull.dll")

        if
            not (File.Exists engine)
            || SafetyPolicy.fileDigest engine
               <> SafetyPolicy.fileDigest typeof<SafetyReport>.Assembly.Location
        then
            Error "BUILD002: The workspace worker differs from the invoking compiler.", 0L
        else
            let closure () =
                Directory.GetFiles(worker, "*", SearchOption.AllDirectories)
                |> Array.sort
                |> Array.map input
                |> Codec.encode
                |> System.Text.Encoding.UTF8.GetBytes
                |> SafetyPolicy.digest

            let before = closure ()

            let start =
                ProcessStartInfo(
                    Path.Combine(runtimeRoot (), "dotnet"),
                    WorkingDirectory =
                        (Path.GetDirectoryName request.Project
                         |> FDull.Transport.External.required "workspace.project-parent")
                )

            start.ArgumentList.Add(Path.Combine(worker, "fdull-worker.dll"))
            start.ArgumentList.Add(if advisory then "--workspace-review" else "--workspace")
            start.Environment.Clear()
            start.Environment["DOTNET_ROOT"] <- runtimeRoot ()
            start.Environment["DOTNET_GCHeapHardLimit"] <- "20000000"
            let text = Codec.encode request
            let clock = Stopwatch.StartNew()

            let run =
                SafetyProcess.run
                    { Milliseconds = 60000
                      OutputCharacters =
                        if advisory then
                            SafetyLimits.workspaceReviewCharacters
                        else
                            SafetyLimits.workspaceReportCharacters
                      WorkingSetBytes = SafetyLimits.workingSetBytes }
                    start
                    text

            let elapsed = clock.ElapsedMilliseconds

            match run with
            | Error error -> Error("BUILD003: " + error), elapsed
            | Ok output ->
                accept advisory request (SafetyPolicy.fileDigest engine) (before <> closure ()) output, elapsed

    /// The parent's own work is everything outside the export and worker processes.
    let private measured advisory root project =
        let clock = Stopwatch.StartNew()
        let invocation = export root project
        let exported = clock.ElapsedMilliseconds

        let result, worker =
            match invocation with
            | Error error -> Error error, 0L
            | Ok invocation -> requestFrom advisory root project invocation |> analyzeTimed advisory

        result,
        { Project = project
          ExportMilliseconds = exported
          WorkerMilliseconds = worker
          ValidationMilliseconds = clock.ElapsedMilliseconds - exported - worker }

    let private request root project =
        export root project |> Result.map (requestFrom false root project)

    let private analyze request =
        analyzeTimed false request |> fst |> Result.map _.Analysis

    let checkProject platform project =
        try
            let root = Path.GetFullPath platform
            let policy = WorkspacePolicy.read root
            WorkspaceAudit.validate root policy

            let relative =
                Path.GetRelativePath(root, Path.GetFullPath(project, root)).Replace('\\', '/')

            if policy.Sources |> List.exists (fun source -> source.Project = relative) then
                request root relative |> Result.bind analyze
            else
                Error "ARCH002: The project is not assigned by workspace policy."
        with error ->
            Error("BUILD003: " + error.Message)

    let private checkMeasured platform checkProject =
        let root = Path.GetFullPath platform
        let errors = ResizeArray<string>()
        let projects = ResizeArray<WorkspaceProjectReport>()
        let exclusions = ResizeArray<WorkspaceProjectExclusion>()
        let timings = ResizeArray<WorkspaceTiming>()

        try
            let policy = WorkspacePolicy.read root
            WorkspaceAudit.validate root policy
            exclusions.AddRange(WorkspacePolicy.excludedProjects policy)
            let projectFiles = WorkspaceAudit.order policy.Projects

            for project in projectFiles do
                let result, timing = checkProject project
                Option.iter timings.Add timing

                match result with
                | Ok report -> projects.Add report
                | Error error -> errors.Add(project + ": " + error)

            if
                errors.Count = 0
                && projects |> Seq.forall (fun project -> project.Report.Complete)
            then
                let observed = projects |> Seq.collect _.Uses |> Seq.toList

                for unused in WorkspaceAudit.unused policy observed do
                    errors.Add("BUILD005: Unused protected capability: " + unused)

                for unused in WorkspaceAudit.unusedConstructors policy observed do
                    errors.Add("BUILD005: Unused protected constructor: " + unused)

                for unused in WorkspaceAudit.unusedDataContracts policy observed do
                    errors.Add(
                        "BUILD005: Unused reusable data contract or assembly pin: "
                        + unused.Project
                        + " | "
                        + unused.Identity
                    )
        with error ->
            errors.Add error.Message

        let complete =
            errors.Count = 0
            && projects |> Seq.forall (fun project -> project.Report.Complete)

        { Complete = complete
          ExitCode =
            if not complete then
                3
            elif projects |> Seq.exists (fun project -> project.Report.ExitCode <> 0) then
                1
            else
                0
          Projects = List.ofSeq projects
          ExcludedProjects = List.ofSeq exclusions
          Errors = List.ofSeq errors },
        List.ofSeq timings

    let internal checkUsing platform checkProject =
        checkMeasured platform (fun project -> checkProject project, None) |> fst

    /// Lint the workspace and report each project's export, worker and validation time.
    let checkTimed platform =
        let root = Path.GetFullPath platform

        checkMeasured root (fun project ->
            let result, timing = measured false root project
            result |> Result.map _.Analysis, Some timing)

    let check platform = checkTimed platform |> fst

    /// Analyze a current-source snapshot for human review, without updating or certifying policy.
    let reviewTimed platform =
        let root = Path.GetFullPath platform
        let errors = ResizeArray<string>()
        let projects = ResizeArray<WorkspaceReviewProjectReport>()
        let timings = ResizeArray<WorkspaceTiming>()
        let mutable policy = None
        let mutable snapshot = []

        try
            let document = WorkspacePolicy.readForReview root
            WorkspaceAudit.validate root document
            policy <- Some document
            let pin = input (Path.Combine(root, "fdull.json"))

            snapshot <-
                document.Sources
                |> List.map (fun source -> input (Path.Combine(root, source.File)))

            for project in WorkspaceAudit.order document.Projects do
                let result, timing = measured true root project
                timings.Add timing

                match result with
                | Ok report -> projects.Add report
                | Error error -> errors.Add(project + ": " + error)

            if
                pin <> input pin.Path
                || snapshot |> List.exists (fun expected -> input expected.Path <> expected)
                || projects
                   |> Seq.exists (fun project ->
                       project.Analysis.Report.Sources @ project.Analysis.Report.References
                       |> List.exists (fun expected -> input expected.Path <> expected))
                || document <> WorkspacePolicy.readForReview root
            then
                errors.Add "BUILD004: Review inputs changed during analysis."
        with error ->
            errors.Add error.Message

        let report =
            match policy with
            | Some document -> WorkspaceReview.create root document snapshot (List.ofSeq projects) (List.ofSeq errors)
            | None ->
                { Advisory = true
                  AnalysisComplete = false
                  ExitCode = 3
                  Projects = []
                  ExcludedProjects = []
                  Items = []
                  SourceChanges = []
                  UnusedCapabilities = []
                  UnusedConstructors = []
                  UnusedDataContracts = []
                  Errors = List.ofSeq errors }

        report, List.ofSeq timings

    let review platform = reviewTimed platform |> fst

    let private serveUsing advisory (input: TextReader) (output: TextWriter) =
        try
            let request =
                SafetyProcess.readBounded SafetyLimits.messageCharacters input
                |> _.GetAwaiter().GetResult()
                |> Codec.decode<WorkspaceRequest>

            let code, text =
                if advisory then
                    let review = WorkspaceEngine.review request
                    review.Analysis.Report.ExitCode, Codec.encode review
                else
                    let report = WorkspaceEngine.check request
                    report.Report.ExitCode, Codec.encode report

            output.Write text
            output.Flush()
            code
        with error ->
            Console.Error.WriteLine("BUILD003: " + error.Message)
            3

    let serve input output = serveUsing false input output

    let serveReview input output = serveUsing true input output
