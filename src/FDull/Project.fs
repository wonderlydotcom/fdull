namespace FDull

open System
open System.IO
open System.Diagnostics
open FDull.Transport

/// Wall-clock cost of one project's `verify` steps, 0 for a step that did not run. Diagnostic output only;
/// never part of a report.
type VerifyTiming =
    { Project: string
      EvaluateMilliseconds: int64
      RestoreMilliseconds: int64
      BuildMilliseconds: int64
      AnalyzeMilliseconds: int64
      RebuildMilliseconds: int64
      ReexportMilliseconds: int64 }

/// Wall-clock cost of `verify`'s steps that cover the whole selection: one restore and one build, both 0
/// when each project was restored and built on its own, and the final validation pass. Diagnostic output only.
type VerifyTotals =
    { Restore: int64
      Build: int64
      FinalValidation: int64 }

/// Standalone project lifecycle. Policy creation records inputs, never exemptions.
module Project =
    let defaults () =
        use stream =
            typeof<WorkspacePolicyDocument>.Assembly.GetManifestResourceStream "FDull.defaults.props"
            |> External.required "project.defaults"

        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    let internal run root (arguments: string list) =
        let start = ProcessStartInfo("dotnet", WorkingDirectory = root)

        for argument in arguments do
            start.ArgumentList.Add argument

        start.Environment["DOTNET_GCHeapHardLimit"] <- "80000000"
        start.Environment["MSBUILDDISABLENODEREUSE"] <- "1"

        match
            SafetyProcess.run
                { Milliseconds = 600000
                  OutputCharacters = SafetyLimits.messageCharacters
                  WorkingSetBytes = 2147483648L }
                start
                ""
        with
        | Error error -> Error error
        | Ok result when result.ExitCode <> 0 -> Error(result.Error + result.Output)
        | Ok _ -> Ok()

    let private require =
        function
        | Ok value -> value
        | Error error -> invalidOp error

    let initializeWithScope directory scopeFile =
        let root = Path.GetFullPath directory
        let path = Path.Combine(root, "fdull.json")

        if File.Exists path then
            invalidOp "BUILD005: fdull.json already exists; initialization never replaces reviewed policy."

        let files = WorkspaceAudit.inventory root

        let external, exclusions, scopeInputs =
            match scopeFile with
            | None -> [], [], []
            | Some supplied ->
                let full = Path.GetFullPath(supplied, root)
                let relative = Path.GetRelativePath(root, full).Replace('\\', '/')

                if not (WorkspacePolicy.localFile relative) || not (File.Exists full) then
                    invalidOp "BUILD005: The scope document must be a file inside the workspace."

                let scope = File.ReadAllText full |> WorkspacePolicy.decodeScope

                if scope.Version <> 1 then
                    invalidOp "BUILD005: Missing or unsupported workspace scope."

                WorkspacePolicy.validateExternal scope.External
                let exclusions = scope.ExcludedProjects |> Option.defaultValue []
                scope.External, exclusions, [ relative ]

        WorkspacePolicy.validateExcludedProjects exclusions
        let excluded = exclusions |> List.map _.File |> Set.ofList

        let projectFiles =
            files
            |> List.filter (fun file -> Path.GetExtension file = ".fsproj" && not (Set.contains file excluded))

        if projectFiles.IsEmpty then
            invalidOp "ARCH002: No included F# projects were found."

        let invocations =
            projectFiles
            |> List.map (fun project -> project, Workspace.export root project |> require)

        let sources =
            invocations
            |> List.collect (fun (project, invocation) ->
                invocation.Sources
                |> List.filter (fun file -> not (List.contains file invocation.Generated))
                |> List.map (fun file ->
                    { File = Path.GetRelativePath(root, file).Replace('\\', '/')
                      Project = project
                      Profile = "PURE"
                      Digest = SafetyPolicy.fileDigest file }))

        let controls =
            files
            |> List.filter (fun file ->
                List.contains
                    (Path.GetExtension file)
                    [ ".props"
                      ".targets"
                      ".fsproj"
                      ".csproj"
                      ".vbproj"
                      ".sln"
                      ".slnx"
                      ".yml"
                      ".yaml" ]
                || List.contains
                    ((Path.GetFileName file |> External.required "project.input-name").ToLowerInvariant())
                    [ "global.json"; "nuget.config"; "packages.lock.json"; "dotnet-tools.json" ])

        let policy =
            { Version = 1
              Repository = None
              Sources = sources
              Projects =
                invocations
                |> List.map (fun (project, invocation) ->
                    { File = project
                      Profile = "PURE"
                      References = invocation.ProjectReferences })
              Inputs =
                (controls @ scopeInputs)
                |> List.distinct
                |> List.map (fun file ->
                    { File = file
                      Digest = SafetyPolicy.fileDigest (Path.Combine(root, file)) })
              External = if external.IsEmpty then None else Some external
              ExcludedProjects = if exclusions.IsEmpty then None else Some exclusions
              DataContracts = None
              Capabilities = []
              Domains = []
              Constructors = [] }

        WorkspaceAudit.validate root policy
        File.WriteAllText(path, Codec.encode policy)
        path

    let initialize directory =
        let scope = Path.Combine(Path.GetFullPath directory, "fdull.scope.json")
        initializeWithScope directory (if File.Exists scope then Some scope else None)

    /// A complete result is evidence for one exact, ordered compiler invocation.
    let matchesInvocation (project: WorkspaceProjectReport) (invocation: WorkspaceInvocation) =
        project.Report.Complete
        && project.Report.ExitCode = 0
        && project.Report.Diagnostics.IsEmpty
        && project.Report.Arguments = invocation.Arguments
        && (project.Report.Sources |> List.map _.Path) = invocation.Sources
        && (project.Report.References |> List.map _.Path) = invocation.References
        && project.ProjectReferences = invocation.ProjectReferences

    let private measure action =
        let clock = Stopwatch.StartNew()
        let value = action ()
        value, clock.ElapsedMilliseconds

    /// Values of steps run through `Workspace.schedule`, in project order. The first failure in that
    /// order fails verification, as it would have when the steps ran one at a time.
    let private completed outcomes =
        outcomes
        |> List.map (fun (_, outcome) ->
            match outcome with
            | Ok value -> value
            | Error(error: exn) -> invalidOp error.Message)

    /// Verify with up to `jobs` projects building or analyzing at once, and report each project's timing
    /// and the final validation time. A project is built, analyzed and rebuilt only after every project it
    /// references has finished, so the job count never changes a passing report.
    let verifyTimed jobs directory =
        let root = Path.GetFullPath directory
        let policy = WorkspacePolicy.read root

        if policy.Repository.IsSome then
            invalidOp
                "BUILD005: Version-two policies require Repository.verify so cross-policy dependencies share one verification schedule."

        WorkspaceAudit.validate root policy
        let projects = WorkspaceAudit.order policy.Projects

        let references =
            policy.Projects
            |> List.map (fun project -> project.File, project.References)
            |> Map.ofList

        let evaluated =
            Workspace.schedule
                jobs
                Map.empty
                (fun project -> measure (fun () -> Workspace.outputDirectories root project |> require))
                projects
            |> completed

        // Concurrent builds of projects sharing a directory would write the same files.
        let sequential =
            Workspace.sharedDirectories root (List.zip projects (evaluated |> List.map fst))

        let concurrent = if sequential.IsEmpty then jobs else 1

        // A restore writes its project's whole reference closure, so restores stay sequential.
        let restored =
            projects
            |> List.map (fun project ->
                measure (fun () -> run root [ "restore"; project; "--locked-mode"; "-m:1"; "-nr:false" ] |> require)
                |> snd)

        // A reviewed external project is built only through its consumers, one project at a time.
        let external =
            policy.Projects
            |> List.exists (fun project ->
                project.References
                |> List.exists (fun reference -> not (Map.containsKey reference references)))

        let built =
            Workspace.schedule
                (if external then 1 else concurrent)
                references
                (fun project ->
                    measure (fun () ->
                        run
                            root
                            ([ "build"
                               project
                               "-c"
                               "Release"
                               "--no-restore"
                               "-m:1"
                               "--disable-build-servers" ]
                             @ (if external then
                                    []
                                else
                                    [ "-p:BuildProjectReferences=false" ]))
                        |> require)
                    |> snd)
                projects
            |> completed

        // Analyze each consumer against its dependencies' final compiled bytes.
        // Rebuilding a previously analyzed dependency would invalidate its consumers.
        let report, analyzed =
            Workspace.checkUsing root concurrent (fun project ->
                let clock = Stopwatch.StartNew()

                match Workspace.checkProject root project with
                | Error error -> Error error, (project, clock.ElapsedMilliseconds, 0L, 0L)
                | Ok result when result.Report.ExitCode <> 0 -> Ok result, (project, clock.ElapsedMilliseconds, 0L, 0L)
                | Ok result ->
                    let analyze = clock.ElapsedMilliseconds

                    run
                        root
                        [ "build"
                          project
                          "-t:Rebuild"
                          "-p:BuildProjectReferences=false"
                          "-c"
                          "Release"
                          "--no-restore"
                          "-m:1"
                          "--disable-build-servers" ]
                    |> require

                    let rebuild = clock.ElapsedMilliseconds - analyze

                    if not (Safety.verifyInputs result.Report) then
                        invalidOp ("BUILD004: Source/reference inputs changed while compiling " + project)

                    let invocation = Workspace.export root project |> require
                    Workspace.validateImports root project policy invocation |> ignore

                    if not (matchesInvocation result invocation) then
                        invalidOp ("BUILD004: The evaluated compiler invocation changed while compiling " + project)

                    Ok result, (project, analyze, rebuild, clock.ElapsedMilliseconds - analyze - rebuild))

        let clock = Stopwatch.StartNew()

        if report.ExitCode = 0 then
            for project in report.Projects do
                if not (Safety.verifyInputs project.Report) then
                    invalidOp "BUILD004: Source/reference inputs changed during compilation."

                let invocation = Workspace.export root project.Project |> require
                Workspace.validateImports root project.Project policy invocation |> ignore

                if not (matchesInvocation project invocation) then
                    invalidOp "BUILD004: The evaluated compiler invocation changed during compilation."

            let finalPolicy = WorkspacePolicy.read root

            if finalPolicy <> policy then
                invalidOp "BUILD004: Policy changed during compilation."

            WorkspaceAudit.validate root finalPolicy

        let finalValidation = clock.ElapsedMilliseconds

        let timings =
            List.zip projects (List.zip3 evaluated restored built)
            |> List.map (fun (project, ((_, evaluate), restore, build)) ->
                let analyze, rebuild, reexport =
                    analyzed
                    |> List.tryFind (fun (file, _, _, _) -> file = project)
                    |> Option.map (fun (_, analyze, rebuild, reexport) -> analyze, rebuild, reexport)
                    |> Option.defaultValue (0L, 0L, 0L)

                ({ Project = project
                   EvaluateMilliseconds = evaluate
                   RestoreMilliseconds = restore
                   BuildMilliseconds = build
                   AnalyzeMilliseconds = analyze
                   RebuildMilliseconds = rebuild
                   ReexportMilliseconds = reexport }
                : VerifyTiming))

        { report with Sequential = sequential },
        (timings,
         { Restore = 0L
           Build = 0L
           FinalValidation = finalValidation })

    let verify directory =
        verifyTimed Workspace.defaultJobs directory |> fst
