namespace FDull

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading.Tasks
open FDull.Transport

type WorkspaceReport =
    {
        Complete: bool
        ExitCode: int
        Projects: WorkspaceProjectReport list
        /// Projects served from the result cache; always 0 with `--no-cache`.
        Cached: int
        ExcludedProjects: WorkspaceProjectExclusion list
        /// Why `verify` ran one project at a time whatever its job count; always empty for `lint`.
        Sequential: string list
        Errors: string list
    }

type WorkspaceInvocation =
    { Sources: string list
      References: string list
      Arguments: string list
      Generated: string list
      ProjectReferences: string list
      ImportedBuildInputs: string list }

/// Wall-clock cost of one project's analysis. Diagnostic output only; never part of a report.
type WorkspaceTiming =
    { Project: string
      ExportMilliseconds: int64
      WorkerMilliseconds: int64
      ValidationMilliseconds: int64 }

/// How `lint`, `review` and `verify` run. `MemoryMiB` is each analysis worker's managed heap ceiling,
/// from `SafetyLimits.minimumWorkerMemoryMiB` to `SafetyLimits.maximumWorkerMemoryMiB`. No option
/// changes a complete report except its `Cached` counts; too little memory makes a check incomplete.
type WorkspaceOptions =
    { Jobs: int
      Cache: bool
      MemoryMiB: int }

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
              "-getProperty:MSBuildAllProjects"
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

            let importedBuildInputs =
                document.RootElement.GetProperty("Properties").GetProperty("MSBuildAllProjects").GetString()
                |> Option.ofObj
                |> Option.defaultValue ""
                |> fun value -> value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun file -> Path.GetFullPath(file, directory))
                |> Array.distinct
                |> Array.sort
                |> Array.toList

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
                          ProjectReferences = projectReferences
                          ImportedBuildInputs = importedBuildInputs }

    /// The intermediate and build output directories MSBuild evaluates for a Release project. Only
    /// evaluates the project; no target runs and nothing is written.
    let outputDirectories root project =
        let path = Path.Combine(root, project)

        let directory =
            Path.GetDirectoryName path
            |> FDull.Transport.External.required "workspace.project-parent"

        let args =
            [ "msbuild"
              path
              "-p:Configuration=Release"
              "-getProperty:BaseIntermediateOutputPath"
              "-getProperty:OutDir"
              "-m:1"
              "-nr:false"
              "-nologo" ]

        match runTool root args 30000 SafetyLimits.messageCharacters with
        | Error error -> Error error
        | Ok result when result.ExitCode <> 0 -> Error(result.Error + result.Output)
        | Ok result ->
            use document = JsonDocument.Parse result.Output
            let properties = document.RootElement.GetProperty "Properties"

            [ "BaseIntermediateOutputPath"; "OutDir" ]
            |> List.map (fun name ->
                properties.GetProperty(name).GetString()
                |> External.required "workspace.output-directory"
                |> fun value ->
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Replace('\\', '/'), directory)))
            |> Ok

    /// One line for each pair of projects whose output directories are equal or nested. Concurrent builds
    /// of such projects would write the same files, so `verify` then runs one project at a time.
    let sharedDirectories root (directories: (string * string list) list) =
        let within (parent: string) (child: string) =
            let relative = Path.GetRelativePath(parent, child).Replace('\\', '/')

            not (
                relative = ".."
                || relative.StartsWith("../", StringComparison.Ordinal)
                || Path.IsPathRooted relative
            )

        let shared (first: string list) (second: string list) =
            first
            |> List.collect (fun one -> second |> List.map (fun other -> one, other))
            |> List.tryPick (fun (one, other) ->
                if within one other then Some one
                elif within other one then Some other
                else None)

        let rec pairs entries =
            match entries with
            | [] -> []
            | (project, owned) :: rest ->
                (rest
                 |> List.choose (fun (other, others) ->
                     shared owned others
                     |> Option.map (fun directory ->
                         let relative = Path.GetRelativePath(root, directory).Replace('\\', '/')
                         $"Verify runs one project at a time: %s{project} and %s{other} share %s{relative}.")))
                @ pairs rest

        pairs directories

    /// Read the policy once for a run, pinned to the exact file bytes it was read from.
    let private pinned advisory root =
        let path = Path.Combine(root, "fdull.json")
        let before = input path

        let document =
            if advisory then
                WorkspacePolicy.readForReview root
            else
                WorkspacePolicy.read root

        if input path <> before then
            invalidOp "BUILD004: The workspace policy changed while it was read."

        document, before

    let validateImports root project (document: WorkspacePolicyDocument) (invocation: WorkspaceInvocation) =
        WorkspacePolicy.validatePinnedImports root document

        let buildInputs =
            WorkspacePolicy.buildInputPaths root document
            |> List.map (fun (path, digest) -> { Path = path; Digest = digest })

        match document.Repository with
        | None -> []
        | Some _ ->
            let repoRoot = WorkspacePolicy.repositoryRoot root document
            let projectFile = Path.Combine(root, project)

            let projectDirectory =
                Path.GetDirectoryName projectFile
                |> External.required "workspace.project-parent"

            let projectName =
                Path.GetFileName projectFile |> External.required "workspace.project-name"

            let inside parent path =
                let relative = Path.GetRelativePath(parent, path).Replace('\\', '/')
                relative <> ".." && not (relative.StartsWith("../", StringComparison.Ordinal))

            let generated path =
                List.contains
                    path
                    [ Path.Combine(projectDirectory, "obj", projectName + ".nuget.g.props")
                      Path.Combine(projectDirectory, "obj", projectName + ".nuget.g.targets") ]

            invocation.ImportedBuildInputs
            |> List.filter (inside repoRoot)
            |> List.filter (generated >> not)
            |> List.map (fun path ->
                match buildInputs |> List.tryFind (fun item -> item.Path = path) with
                | Some item -> item
                | None -> invalidOp ("BUILD005: Evaluated repository import is unpinned: " + path))

    let private requestFrom root project (document: WorkspacePolicyDocument, pin) (invocation: WorkspaceInvocation) =
        let buildInputs =
            WorkspacePolicy.buildInputPaths root document
            |> List.map (fun (path, digest) -> { Path = path; Digest = digest })

        let importedBuildInputs = validateImports root project document invocation

        { Root = root
          Project = Path.Combine(root, project)
          ProjectReferences = invocation.ProjectReferences
          Arguments = invocation.Arguments
          Sources = invocation.Sources |> List.map input
          References = invocation.References |> List.map input
          Generated = invocation.Generated
          BuildInputs = buildInputs
          ImportedBuildInputs = importedBuildInputs
          Policy = pin
          PolicySlice = WorkspaceSlice.digest project document }

    /// Trust a worker response only after it matches the request and passes protocol validation.
    let private accept
        advisory
        document
        (request: WorkspaceRequest)
        engine
        closureChanged
        (output: SafetyProcessOutput)
        =
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
            || report.Policy <> request.PolicySlice
            || report.Tool <> engine
            || closureChanged
            || request.Policy :: request.Sources
               @ request.References
               @ request.BuildInputs
               @ request.ImportedBuildInputs
               |> List.exists (fun expected -> input expected.Path <> expected)
        then
            Error "BUILD004: The workspace worker report differs from its requested inputs."
        else
            let validation =
                if advisory then
                    WorkspaceReview.validate request document output.ExitCode output.Error review
                else
                    WorkspaceProtocol.validate request document output.ExitCode output.Error result

            validation |> Result.map (fun () -> review)

    /// Analyze one project. With `options.Cache`, a stored response for the same key is accepted through the
    /// same checks as a fresh one; anything else runs the worker, and a complete accepted response is stored.
    let private analyzeTimed (options: WorkspaceOptions) advisory document (request: WorkspaceRequest) =
        let worker = Path.Combine(AppContext.BaseDirectory, "safety-worker")
        let engine = Path.Combine(worker, "FDull.dll")

        if
            options.MemoryMiB < SafetyLimits.minimumWorkerMemoryMiB
            || options.MemoryMiB > SafetyLimits.maximumWorkerMemoryMiB
        then
            Error "BUILD003: The worker memory limit is outside the supported range.", 0L
        elif
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
            let key = WorkspaceCache.key advisory before request

            let hit =
                if options.Cache then
                    WorkspaceCache.read request.Root key
                    |> Option.bind (fun output ->
                        // An entry that cannot be decoded or accepted is a miss, never a failure.
                        try
                            accept advisory document request (SafetyPolicy.fileDigest engine) false output
                            |> Result.toOption
                        with _ ->
                            None)
                else
                    None

            match hit with
            | Some review ->
                Ok
                    { review with
                        Analysis = { review.Analysis with Cached = true } },
                0L
            | None ->

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
                start.Environment["DOTNET_GCHeapHardLimit"] <- SafetyLimits.heapHardLimit options.MemoryMiB
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
                          WorkingSetBytes = 2L * SafetyLimits.heapBytes options.MemoryMiB }
                        start
                        text

                let elapsed = clock.ElapsedMilliseconds

                match run with
                | Error error -> Error("BUILD003: " + error), elapsed
                | Ok output ->
                    let accepted =
                        accept advisory document request (SafetyPolicy.fileDigest engine) (before <> closure ()) output

                    match accepted with
                    | Ok review when options.Cache && review.Analysis.Report.Complete ->
                        WorkspaceCache.write request.Root key output
                    | _ -> ()

                    accepted, elapsed

    /// The parent's own work is everything outside the export and worker processes.
    let private measured options advisory root (document, pin) project =
        let clock = Stopwatch.StartNew()
        let invocation = export root project
        let exported = clock.ElapsedMilliseconds

        let result, worker =
            match invocation with
            | Error error -> Error error, 0L
            | Ok invocation ->
                requestFrom root project (document, pin) invocation
                |> analyzeTimed options advisory document

        result,
        { Project = project
          ExportMilliseconds = exported
          WorkerMilliseconds = worker
          ValidationMilliseconds = clock.ElapsedMilliseconds - exported - worker }

    /// The exact worker request `lint` sends for one workspace-relative project.
    let request platform project =
        try
            let root = Path.GetFullPath platform
            export root project |> Result.map (requestFrom root project (pinned false root))
        with error ->
            Error("BUILD003: " + error.Message)

    /// A workspace's policy and the pin of its `fdull.json` bytes, as every analysis request records them.
    let internal readPinned root = pinned false root

    /// Analyze one project of the workspace at the full path `root` against a policy read with `readPinned`
    /// and already audited, so a caller analyzing many projects reads and audits the policy once.
    let internal checkPinned options root (policy: WorkspacePolicyDocument, pin) project =
        try
            let relative =
                Path.GetRelativePath(root, Path.GetFullPath(project, root)).Replace('\\', '/')

            if policy.Sources |> List.exists (fun source -> source.Project = relative) then
                export root relative
                |> Result.map (requestFrom root relative (policy, pin))
                |> Result.bind (analyzeTimed options false policy >> fst >> Result.map _.Analysis)
            else
                Error "ARCH002: The project is not assigned by workspace policy."
        with error ->
            Error("BUILD003: " + error.Message)

    /// Analyze one project as `verify` does. With `options.Cache`, an accepted stored result is returned
    /// with `Cached` set, and a complete fresh result is stored. `options.Jobs` is unused.
    let checkProjectWith (options: WorkspaceOptions) platform project =
        try
            let root = Path.GetFullPath platform
            let policy, pin = readPinned root
            WorkspaceAudit.validate root policy
            checkPinned options root (policy, pin) project
        with error ->
            Error("BUILD003: " + error.Message)

    /// Projects analyzed at once by default.
    let defaultJobs = min Environment.ProcessorCount 4

    /// `defaultJobs` concurrent projects, each worker heap capped at `SafetyLimits.workerMemoryMiB`, and no cache.
    let defaultOptions =
        { Jobs = defaultJobs
          Cache = false
          MemoryMiB = SafetyLimits.workerMemoryMiB }

    let checkProject platform project =
        checkProjectWith defaultOptions platform project

    let private attempt work item =
        try
            Ok(work item)
        with error ->
            Error error

    /// Run `work` for each of `items`, at most `jobs` at once. An item starts only after every item it
    /// references in `dependencies` has finished; references outside `items` are ignored, and `items`
    /// lists dependencies first. Once an item throws, only items before it still start, and in-flight
    /// work is awaited. Outcomes come back in `items` order and stop at the first exception, as a
    /// sequential loop would, so the job count never changes them.
    let schedule jobs (dependencies: Map<string, string list>) work (items: string list) =
        let indexed = items |> List.mapi (fun index item -> index, item)

        let rec run finished running =
            let completed item =
                finished |> List.exists (fun (_, name, _) -> name = item)

            let startable (index, item) =
                not (completed item)
                && not (running |> List.exists (fun (_, name, _) -> name = item))
                && not (
                    finished
                    |> List.exists (fun (failed, _, outcome) -> failed < index && Result.isError outcome)
                )
                && (dependencies
                    |> Map.tryFind item
                    |> Option.defaultValue []
                    |> List.forall (fun dependency -> completed dependency || not (List.contains dependency items)))

            let running =
                running
                @ (indexed
                   |> List.filter startable
                   |> List.truncate (jobs - running.Length)
                   |> List.map (fun (index, item) -> index, item, Task.Run(fun () -> attempt work item)))

            if running.IsEmpty then
                finished
            else
                Task.WaitAny(running |> List.map (fun (_, _, task) -> task :> Task) |> Array.ofList)
                |> ignore

                let ended, pending =
                    running |> List.partition (fun (_, _, task) -> task.IsCompleted)

                run
                    (finished
                     @ (ended |> List.map (fun (index, item, task) -> index, item, task.Result)))
                    pending

        let finished = run [] []

        let rec collect remaining =
            match remaining with
            | [] -> []
            | item :: rest ->
                match finished |> List.tryFind (fun (_, name, _) -> name = item) with
                | Some(_, _, Ok value) -> (item, Ok value) :: collect rest
                | Some(_, _, Error error) -> [ item, Error error ]
                | None -> invalidOp "ARCH001: The project graph contains a cycle."

        collect items

    /// With `ordered`, a project's analysis waits for every project it references, as `verify` requires.
    let private checkMeasured platform jobs ordered checkProject =
        let root = Path.GetFullPath platform
        let errors = ResizeArray<string>()
        let projects = ResizeArray<WorkspaceProjectReport>()
        let exclusions = ResizeArray<WorkspaceProjectExclusion>()
        let timings = ResizeArray()

        try
            let policy, pin = pinned false root
            WorkspaceAudit.validate root policy

            // A version-two service also gets the repository checks that `verify` runs before building.
            if policy.Repository.IsSome then
                errors.AddRange(RepositoryPlan.check (WorkspacePolicy.repositoryRoot root policy) root)

            exclusions.AddRange(WorkspacePolicy.excludedProjects policy)
            let projectFiles = WorkspaceAudit.order policy.Projects

            let dependencies =
                if ordered then
                    policy.Projects
                    |> List.map (fun project -> project.File, project.References)
                    |> Map.ofList
                else
                    Map.empty

            for project, outcome in schedule jobs dependencies (checkProject (policy, pin)) projectFiles do
                match outcome with
                | Ok(result, timing) ->
                    Option.iter timings.Add timing

                    match result with
                    | Ok report -> projects.Add report
                    | Error error -> errors.Add(project + ": " + error)
                | Error(error: exn) -> errors.Add error.Message

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
                        "BUILD005: Unused data contract or assembly pin: "
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
          Cached = projects |> Seq.filter _.Cached |> Seq.length
          ExcludedProjects = List.ofSeq exclusions
          Sequential = []
          Errors = List.ofSeq errors },
        List.ofSeq timings

    /// Verification rebuilds each analyzed project, rewriting its consumers' inputs, so a project's check
    /// starts only after every project it references has finished. `checkProject` returns its timing.
    let internal checkUsing platform jobs checkProject =
        checkMeasured platform jobs true (fun _ project ->
            let result, timing = checkProject project
            result, Some timing)

    /// Lint the workspace with up to `options.Jobs` concurrent project analyses, optionally using the
    /// result cache, and report each project's export, worker and validation time.
    let checkTimed (options: WorkspaceOptions) platform =
        let root = Path.GetFullPath platform

        let outcome =
            checkMeasured root options.Jobs false (fun policy project ->
                let result, timing = measured options false root policy project
                result |> Result.map _.Analysis, Some timing)

        if options.Cache then
            WorkspaceCache.prune root

        outcome

    let check platform =
        checkTimed defaultOptions platform |> fst

    /// Analyze a current-source snapshot for human review, without updating or certifying policy.
    let reviewTimed (options: WorkspaceOptions) platform =
        let root = Path.GetFullPath platform
        let errors = ResizeArray<string>()
        let projects = ResizeArray<WorkspaceReviewProjectReport>()
        let timings = ResizeArray<WorkspaceTiming>()
        let mutable policy = None
        let mutable snapshot = []

        try
            let document, pin = pinned true root
            WorkspaceAudit.validate root document
            policy <- Some document

            snapshot <-
                document.Sources
                |> List.map (fun source -> input (Path.Combine(root, source.File)))

            for project, outcome in
                schedule
                    options.Jobs
                    Map.empty
                    (measured options true root (document, pin))
                    (WorkspaceAudit.order document.Projects) do
                match outcome with
                | Ok(result, timing) ->
                    timings.Add timing

                    match result with
                    | Ok report -> projects.Add report
                    | Error error -> errors.Add(project + ": " + error)
                | Error(error: exn) -> errors.Add error.Message

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
                  Cached = 0
                  ExcludedProjects = []
                  Items = []
                  SourceChanges = []
                  UnusedCapabilities = []
                  UnusedConstructors = []
                  UnusedDataContracts = []
                  Errors = List.ofSeq errors }

        if options.Cache then
            WorkspaceCache.prune root

        report, List.ofSeq timings

    let review platform =
        reviewTimed defaultOptions platform |> fst

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
