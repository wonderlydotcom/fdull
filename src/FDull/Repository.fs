namespace FDull

open System
open System.Diagnostics
open System.IO
open System.Security

type RepositoryServiceResult =
    { Service: string
      Report: WorkspaceReport }

/// A repository result certifies only the requested selection. Skipped and build-only services have no result.
type RepositoryReport =
    { Complete: bool
      ExitCode: int
      Plan: RepositoryPlanResult
      Services: RepositoryServiceResult list
      Errors: string list }

module Repository =
    /// Preserve version-one verification while rejecting a root policy with nested owners.
    let standalone directory =
        let root = Path.GetFullPath directory

        if not (File.Exists(Path.Combine(root, "fdull.json"))) then
            false
        else
            let policy = WorkspacePolicy.readStructure root

            if policy.Repository.IsSome then
                false
            else
                if
                    WorkspaceAudit.inventory root
                    |> List.exists (fun file -> file <> "fdull.json" && Path.GetFileName file = "fdull.json")
                then
                    invalidOp "ARCH002: Nested policy ownership is unsupported."

                true

    /// An explicit v2 service uses its reviewed repository root and selects only its dependency closure.
    let resolveSelection directory selection =
        let root = Path.GetFullPath directory |> Path.TrimEndingDirectorySeparator

        if File.Exists(Path.Combine(root, "fdull.json")) then
            let policy = WorkspacePolicy.readStructure root

            match policy.Repository with
            | Some _ ->
                WorkspacePolicy.validateRepositoryStructure root policy
                let repository = WorkspacePolicy.repositoryRoot root policy

                repository,
                Some(Option.defaultValue (Service(Path.GetRelativePath(repository, root).Replace('\\', '/'))) selection)
            | None -> root, selection
        else
            root, selection

    let private require =
        function
        | Ok value -> value
        | Error error -> invalidOp error

    let private measure action =
        let clock = Stopwatch.StartNew()
        let value = action ()
        value, clock.ElapsedMilliseconds

    let private completed outcomes =
        outcomes
        |> List.map (fun (name, outcome) ->
            match outcome with
            | Ok value -> name, value
            | Error(error: exn) -> invalidOp error.Message)
        |> Map.ofList

    let private summarize (policy: WorkspacePolicyDocument) (results: Result<WorkspaceProjectReport, string> list) =
        let projects =
            results
            |> List.choose (function
                | Ok project -> Some project
                | Error _ -> None)

        let errors =
            results
            |> List.choose (function
                | Error error -> Some error
                | Ok _ -> None)

        let analyzed =
            errors.IsEmpty
            && projects |> List.forall (fun project -> project.Report.Complete)

        let uses = projects |> List.collect _.Uses

        let unused =
            if analyzed then
                (WorkspaceAudit.unused policy uses
                 |> List.map (fun item -> "BUILD005: Unused protected capability: " + item))
                @ (WorkspaceAudit.unusedConstructors policy uses
                   |> List.map (fun item -> "BUILD005: Unused protected constructor: " + item))
                @ (WorkspaceAudit.unusedDataContracts policy uses
                   |> List.map (fun item ->
                       "BUILD005: Unused data contract or assembly pin: "
                       + item.Project
                       + " | "
                       + item.Identity))
            else
                []

        let complete = analyzed && unused.IsEmpty

        { Complete = complete
          ExitCode =
            if not complete then
                3
            elif projects |> List.exists (fun project -> project.Report.ExitCode <> 0) then
                1
            else
                0
          Projects = projects
          Cached = projects |> List.filter _.Cached |> List.length
          ExcludedProjects = WorkspacePolicy.excludedProjects policy
          Sequential = []
          Errors = errors @ unused }

    let private linked (path: string) =
        (FileInfo path).LinkTarget |> isNull |> not

    /// Restore, then build, every selected project in one MSBuild invocation each, through a solution
    /// written under `<root>/.fdull/verify` and deleted afterwards. As in any solution build, MSBuild builds
    /// each project once, including unowned projects such as C# protocol projects through their consumers.
    let private buildSelection root nodes (projects: string list) =
        let parent = Path.Combine(root, ".fdull")
        let directory = Path.Combine(parent, "verify")

        if linked parent || linked directory then
            invalidOp "BUILD005: The .fdull directory is a symbolic link."

        Directory.CreateDirectory directory |> ignore

        let solution =
            Path.Combine(directory, "selection-" + Guid.NewGuid().ToString("N") + ".slnx")

        let entries =
            projects
            |> List.map (fun project ->
                "  <Project Path=\""
                + SecurityElement.Escape(Path.GetRelativePath(directory, project).Replace('\\', '/'))
                + "\" />")

        File.WriteAllText(solution, "<Solution>\n" + String.concat "\n" entries + "\n</Solution>\n")

        try
            let _, restore =
                measure (fun () ->
                    Project.run root [ "restore"; solution; "--locked-mode"; "-m:1"; "-nr:false"; "-v:q" ]
                    |> require)

            let _, build =
                measure (fun () ->
                    Project.run
                        root
                        [ "build"
                          solution
                          "-c"
                          "Release"
                          "--no-restore"
                          // A solution builds references outside it in their default configuration otherwise.
                          "-p:ShouldUnsetParentConfigurationAndPlatform=false"
                          "-m:" + string nodes
                          "--disable-build-servers"
                          "-v:q" ]
                    |> require)

            restore, build
        finally
            File.Delete solution

    let private execute (options: WorkspaceOptions) (plan: RepositoryPlanResult) =
        let jobs = options.Jobs
        let selected = Set.ofList (plan.Affected @ plan.Dependencies)

        let services =
            plan.Services |> List.filter (fun service -> Set.contains service.Id selected)
        // Full validation is deliberately deferred until after structural selection. Each policy is read,
        // pinned and audited once here; every project's analysis uses it, and final validation re-reads it.
        let policies =
            services
            |> List.map (fun service ->
                let root = Path.GetFullPath service.Root
                let policy, pin = Workspace.readPinned root

                if policy <> service.Policy then
                    invalidOp "BUILD004: Policy changed after selection."

                WorkspaceAudit.validate root policy
                service.Id, (root, policy, pin))
            |> Map.ofList

        let owned =
            services
            |> List.collect (fun service ->
                service.Policy.Projects
                |> List.map (fun project -> Path.Combine(service.Root, project.File), (service, project)))
            |> Map.ofList

        let graph =
            owned
            |> Map.toList
            |> List.map (fun (path, (service, project)) ->
                let cross =
                    service.Policy.Repository
                    |> Option.map (fun repository ->
                        repository.References
                        |> List.filter (fun edge -> edge.Project = project.File)
                        |> List.map (fun edge -> Path.Combine(plan.Root, edge.Target)))
                    |> Option.defaultValue []

                { File = path
                  Profile = project.Profile
                  References =
                    (project.References |> List.map (fun file -> Path.Combine(service.Root, file)))
                    @ cross })

        let projects = WorkspaceAudit.order graph
        // Build-only dependencies supply consumer inputs. Their evidence stays with the baseline.
        let buildOnly = Set.ofList plan.BuildOnly

        let verified =
            services |> List.filter (fun service -> not (Set.contains service.Id buildOnly))

        let analyzedProjects =
            projects
            |> List.filter (fun project -> not (Set.contains (fst (Map.find project owned)).Id buildOnly))

        let dependencies =
            graph
            |> List.map (fun project -> project.File, project.References)
            |> Map.ofList

        let evaluated =
            Workspace.schedule
                jobs
                Map.empty
                (fun project ->
                    let service, local = Map.find project owned
                    measure (fun () -> Workspace.outputDirectories service.Root local.File |> require))
                projects
            |> completed

        let sequential =
            Workspace.sharedDirectories
                plan.Root
                (projects |> List.map (fun project -> project, fst (Map.find project evaluated)))

        let concurrent = if sequential.IsEmpty then jobs else 1
        // Projects sharing an output directory build on one MSBuild node, as they analyze one at a time.
        let restored, built = buildSelection plan.Root concurrent projects
        // A dependency reaches its final bytes before any consumer is analyzed.
        let analyzed =
            Workspace.schedule
                concurrent
                dependencies
                (fun project ->
                    let service, local = Map.find project owned

                    let root, policy, pin = Map.find service.Id policies

                    let result, analyze =
                        measure (fun () -> Workspace.checkPinned options root (policy, pin) local.File)

                    match result with
                    // An accepted cache entry answers for exactly these sources, compiler invocation and
                    // reference bytes, so the clean rebuild is skipped; final validation re-exports it.
                    | Ok result when result.Report.ExitCode = 0 && result.Cached -> Ok result, (analyze, 0L, 0L)
                    | Ok result when result.Report.ExitCode = 0 ->
                        let _, rebuild =
                            measure (fun () ->
                                Project.run
                                    service.Root
                                    [ "build"
                                      project
                                      "-t:Rebuild"
                                      "-p:BuildProjectReferences=false"
                                      "-c"
                                      "Release"
                                      "--no-restore"
                                      "-m:1"
                                      "--disable-build-servers" ]
                                |> require)

                        let _, reexport =
                            measure (fun () ->
                                if not (Safety.verifyInputs result.Report) then
                                    invalidOp ("BUILD004: Inputs changed while compiling " + project)

                                let invocation = Workspace.export service.Root local.File |> require

                                Workspace.validateImports service.Root local.File service.Policy invocation
                                |> ignore

                                if not (Project.matchesInvocation result invocation) then
                                    invalidOp ("BUILD004: Invocation changed while compiling " + project))

                        Ok result, (analyze, rebuild, reexport)
                    | _ -> result, (analyze, 0L, 0L))
                analyzedProjects
            |> completed

        let reports =
            verified
            |> List.map (fun service ->
                let results =
                    analyzedProjects
                    |> List.choose (fun project ->
                        let owner, _ = Map.find project owned

                        if owner.Id = service.Id then
                            Some(fst (Map.find project analyzed))
                        else
                            None)

                let _, policy, _ = Map.find service.Id policies

                { Service = service.Id
                  Report =
                    { summarize policy results with
                        Sequential = sequential } })

        let _, finalValidation =
            measure (fun () ->
                // Exports of different projects run concurrently, as in `lint`, unless they share a directory.
                Workspace.schedule
                    concurrent
                    Map.empty
                    (fun project ->
                        match fst (Map.find project analyzed) with
                        | Ok result when result.Report.ExitCode = 0 ->
                            let service, local = Map.find project owned

                            if not (Safety.verifyInputs result.Report) then
                                invalidOp "BUILD004: Inputs changed during repository compilation."

                            let invocation = Workspace.export service.Root local.File |> require

                            Workspace.validateImports service.Root local.File service.Policy invocation
                            |> ignore

                            if not (Project.matchesInvocation result invocation) then
                                invalidOp "BUILD004: Invocation changed during repository compilation."
                        | _ -> ())
                    analyzedProjects
                |> completed
                |> ignore

                // Exports execute MSBuild targets too. Check all accepted bytes after the last export.
                for project in analyzedProjects do
                    match fst (Map.find project analyzed) with
                    | Ok result when result.Report.ExitCode = 0 && not (Safety.verifyInputs result.Report) ->
                        invalidOp "BUILD004: Inputs changed during final repository exports."
                    | _ -> ()

                // Every request carried the pin read before analysis, so the policy bytes must still match it.
                for service in services do
                    let root, policy, pin = Map.find service.Id policies
                    let current, currentPin = Workspace.readPinned root

                    if current <> policy || currentPin <> pin then
                        invalidOp "BUILD004: Policy changed during repository verification."

                    WorkspaceAudit.validate root current

                RepositoryPlan.revalidate plan |> require)

        if options.Cache then
            for service in verified do
                WorkspaceCache.prune service.Root

        let timings =
            projects
            |> List.map (fun project ->
                let analyze, rebuild, reexport =
                    Map.tryFind project analyzed
                    |> Option.map snd
                    |> Option.defaultValue (0L, 0L, 0L)

                { Project = Path.GetRelativePath(plan.Root, project).Replace('\\', '/')
                  EvaluateMilliseconds = snd (Map.find project evaluated)
                  RestoreMilliseconds = 0L
                  BuildMilliseconds = 0L
                  AnalyzeMilliseconds = analyze
                  RebuildMilliseconds = rebuild
                  ReexportMilliseconds = reexport })

        reports,
        (timings,
         { Restore = restored
           Build = built
           FinalValidation = finalValidation })

    /// Verify the selection with `options.Jobs` concurrent projects, each analysis worker heap capped at
    /// `options.MemoryMiB`. With `options.Cache`, a project whose stored result is accepted is not
    /// re-analyzed or rebuilt, and complete fresh results are stored.
    let verifyTimedWith (options: WorkspaceOptions) directory selection =
        let plan = RepositoryPlan.plan directory selection |> require

        try
            let reports, timings = execute options plan
            let complete = reports |> List.forall (fun service -> service.Report.Complete)

            { Complete = complete
              ExitCode =
                if not complete then
                    3
                elif reports |> List.exists (fun service -> service.Report.ExitCode <> 0) then
                    1
                else
                    0
              Plan = plan
              Services = reports
              Errors = [] },
            timings
        with error ->
            { Complete = false
              ExitCode = 3
              Plan = plan
              Services = []
              Errors = [ error.Message ] },
            ([],
             { Restore = 0L
               Build = 0L
               FinalValidation = 0L })

    let verifyTimed jobs directory selection =
        verifyTimedWith
            { Workspace.defaultOptions with
                Jobs = jobs }
            directory
            selection

    let verify directory selection =
        verifyTimed Workspace.defaultJobs directory selection |> fst
