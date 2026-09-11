namespace FDull

open System
open System.IO
open System.Diagnostics
open FDull.Transport

/// Standalone project lifecycle. Policy creation records inputs, never exemptions.
module Project =
    let defaults () =
        use stream =
            typeof<WorkspacePolicyDocument>.Assembly.GetManifestResourceStream "FDull.defaults.props"
            |> External.required "project.defaults"

        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    let private run root (arguments: string list) =
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

        let external, scopeInputs =
            match scopeFile with
            | None -> [], []
            | Some supplied ->
                let full = Path.GetFullPath(supplied, root)
                let relative = Path.GetRelativePath(root, full).Replace('\\', '/')

                if not (WorkspacePolicy.localFile relative) || not (File.Exists full) then
                    invalidOp "BUILD005: The scope document must be a file inside the workspace."

                let scope = File.ReadAllText full |> Codec.decode<WorkspaceScopeDocument>

                if scope.Version <> 1 then
                    invalidOp "BUILD005: Missing or unsupported workspace scope."

                WorkspacePolicy.validateExternal scope.External
                scope.External, [ relative ]

        let projectFiles =
            files |> List.filter (fun file -> Path.GetExtension file = ".fsproj")

        if projectFiles.IsEmpty then
            invalidOp "ARCH002: No F# projects were found."

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
              Capabilities = []
              Domains = []
              Constructors = [] }

        WorkspaceAudit.validate root policy
        File.WriteAllText(path, Codec.encode policy)
        path

    let initialize directory = initializeWithScope directory None

    /// A complete result is evidence for one exact, ordered compiler invocation.
    let matchesInvocation (project: WorkspaceProjectReport) (invocation: WorkspaceInvocation) =
        project.Report.Complete
        && project.Report.ExitCode = 0
        && project.Report.Diagnostics.IsEmpty
        && project.Report.Arguments = invocation.Arguments
        && (project.Report.Sources |> List.map _.Path) = invocation.Sources
        && (project.Report.References |> List.map _.Path) = invocation.References
        && project.ProjectReferences = invocation.ProjectReferences

    let verify directory =
        let root = Path.GetFullPath directory
        let policy = WorkspacePolicy.read root
        WorkspaceAudit.validate root policy

        for project in policy.Projects do
            run root [ "restore"; project.File; "--locked-mode"; "-m:1"; "-nr:false" ]
            |> require

            run
                root
                [ "build"
                  project.File
                  "-c"
                  "Release"
                  "--no-restore"
                  "-m:1"
                  "--disable-build-servers" ]
            |> require

        // Analyze each consumer against its dependencies' final compiled bytes.
        // Rebuilding a previously analyzed dependency would invalidate its consumers.
        let report =
            Workspace.checkUsing root (fun project ->
                match Workspace.checkProject root project with
                | Error error -> Error error
                | Ok result when result.Report.ExitCode <> 0 -> Ok result
                | Ok result ->
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

                    if not (Safety.verifyInputs result.Report) then
                        invalidOp ("BUILD004: Source/reference inputs changed while compiling " + project)

                    let invocation = Workspace.export root project |> require

                    if not (matchesInvocation result invocation) then
                        invalidOp ("BUILD004: The evaluated compiler invocation changed while compiling " + project)

                    Ok result)

        if report.ExitCode = 0 then
            for project in report.Projects do
                if not (Safety.verifyInputs project.Report) then
                    invalidOp "BUILD004: Source/reference inputs changed during compilation."

                let invocation = Workspace.export root project.Project |> require

                if not (matchesInvocation project invocation) then
                    invalidOp "BUILD004: The evaluated compiler invocation changed during compilation."

            let finalPolicy = WorkspacePolicy.read root

            if finalPolicy <> policy then
                invalidOp "BUILD004: Policy changed during compilation."

            WorkspaceAudit.validate root finalPolicy

        report
