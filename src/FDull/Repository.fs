namespace FDull

open System.Diagnostics
open System.IO

type RepositoryServiceResult =
    { Service: string
      Report: WorkspaceReport }

/// A repository result certifies only the requested selection. Skipped services have no result.
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
          Cached = 0
          ExcludedProjects = WorkspacePolicy.excludedProjects policy
          Sequential = []
          Errors = errors @ unused }

    let private execute jobs (plan: RepositoryPlanResult) =
        let selected = Set.ofList (plan.Affected @ plan.Dependencies)

        let services =
            plan.Services |> List.filter (fun service -> Set.contains service.Id selected)
        // Full validation is deliberately deferred until after structural selection.
        let policies =
            services
            |> List.map (fun service ->
                let policy = WorkspacePolicy.read service.Root

                if policy <> service.Policy then
                    invalidOp "BUILD004: Policy changed after selection."

                WorkspaceAudit.validate service.Root policy
                service.Id, policy)
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
        // Restore traverses reference closures. Even independent service runs share this one sequence.
        let restored =
            projects
            |> List.map (fun project ->
                let service, _ = Map.find project owned

                project,
                (measure (fun () ->
                    Project.run service.Root [ "restore"; project; "--locked-mode"; "-m:1"; "-nr:false" ]
                    |> require)
                 |> snd))
            |> Map.ofList

        let external =
            graph
            |> List.exists (fun project ->
                project.References
                |> List.exists (fun reference -> not (Map.containsKey reference owned)))

        let built =
            Workspace.schedule
                (if external then 1 else concurrent)
                dependencies
                (fun project ->
                    let service, _ = Map.find project owned

                    measure (fun () ->
                        Project.run
                            service.Root
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
        // A dependency reaches its final bytes before any consumer is analyzed.
        let analyzed =
            Workspace.schedule
                concurrent
                dependencies
                (fun project ->
                    let service, local = Map.find project owned

                    let result, analyze =
                        measure (fun () -> Workspace.checkProject service.Root local.File)

                    match result with
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
                projects
            |> completed

        let reports =
            services
            |> List.map (fun service ->
                let results =
                    projects
                    |> List.choose (fun project ->
                        let owner, _ = Map.find project owned

                        if owner.Id = service.Id then
                            Some(fst (Map.find project analyzed))
                        else
                            None)

                { Service = service.Id
                  Report =
                    { summarize (Map.find service.Id policies) results with
                        Sequential = sequential } })

        let _, finalValidation =
            measure (fun () ->
                for project in projects do
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
                    | _ -> ()

                // Exports execute MSBuild targets too. Check all accepted bytes after the last export.
                for project in projects do
                    match fst (Map.find project analyzed) with
                    | Ok result when result.Report.ExitCode = 0 && not (Safety.verifyInputs result.Report) ->
                        invalidOp "BUILD004: Inputs changed during final repository exports."
                    | _ -> ()

                for service in services do
                    let current = WorkspacePolicy.read service.Root

                    if current <> Map.find service.Id policies then
                        invalidOp "BUILD004: Policy changed during repository verification."

                    WorkspaceAudit.validate service.Root current

                RepositoryPlan.revalidate plan |> require)

        let timings =
            projects
            |> List.map (fun project ->
                let analyze, rebuild, reexport = snd (Map.find project analyzed)

                { Project = Path.GetRelativePath(plan.Root, project).Replace('\\', '/')
                  EvaluateMilliseconds = snd (Map.find project evaluated)
                  RestoreMilliseconds = Map.find project restored
                  BuildMilliseconds = Map.find project built
                  AnalyzeMilliseconds = analyze
                  RebuildMilliseconds = rebuild
                  ReexportMilliseconds = reexport })

        reports, (timings, finalValidation)

    let verifyTimed jobs directory selection =
        let plan = RepositoryPlan.plan directory selection |> require

        try
            let reports, timings = execute jobs plan
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
            ([], 0L)

    let verify directory selection =
        verifyTimed Workspace.defaultJobs directory selection |> fst
