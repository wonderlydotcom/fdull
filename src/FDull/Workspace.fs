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
      Errors: string list }

type WorkspaceInvocation =
    { Sources: string list
      References: string list
      Arguments: string list
      Generated: string list
      ProjectReferences: string list }

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
              "-getItem:FscCommandLineArgs,ProjectReference,Compile"
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

            let sources =
                document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
                |> Seq.map (fun item ->
                    item.GetProperty("FullPath").GetString() |> External.required "workspace.source")
                |> Seq.map Path.GetFullPath
                |> Seq.filter (fun file -> List.contains (Path.GetExtension file) [ ".fs"; ".fsi" ])
                |> Seq.toList

            let projectReferences =
                document.RootElement.GetProperty("Items").GetProperty("ProjectReference").EnumerateArray()
                |> Seq.map (fun item ->
                    item.GetProperty("FullPath").GetString()
                    |> External.required "workspace.project-reference"
                    |> fun path -> Path.GetRelativePath(root, path).Replace('\\', '/'))
                |> Seq.toList

            let references =
                arguments
                |> List.choose (fun arg ->
                    if arg.StartsWith("-r:", StringComparison.Ordinal) then
                        Some(full (arg.Substring 3))
                    else
                        None)

            let generated =
                [ full "obj/Release/net10.0/.NETCoreApp,Version=v10.0.AssemblyAttributes.fs"
                  full (
                      "obj/Release/net10.0/"
                      + Path.GetFileNameWithoutExtension project
                      + ".AssemblyInfo.fs"
                  ) ]
                |> List.filter (fun file -> List.contains file sources)

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

            if
                sources.IsEmpty
                || references.IsEmpty
                || (Set.ofList sources).Count <> sources.Length
            then
                Error "BUILD003: MSBuild did not export a complete ordered invocation."
            else
                Ok
                    { Sources = sources
                      References = references
                      Arguments = flags
                      Generated = generated
                      ProjectReferences = projectReferences }

    let private request root project =
        export root project
        |> Result.map (fun invocation ->
            { Root = root
              Project = Path.Combine(root, project)
              ProjectReferences = invocation.ProjectReferences
              Arguments = invocation.Arguments
              Sources = invocation.Sources |> List.map input
              References = invocation.References |> List.map input
              Generated = invocation.Generated
              BuildInputs =
                (WorkspacePolicy.read root).Inputs
                |> List.map (fun pin ->
                    { Path = Path.Combine(root, pin.File)
                      Digest = pin.Digest })
              Policy = input (Path.Combine(root, "fdull.json")) })

    let private analyze (request: WorkspaceRequest) =
        let worker = Path.Combine(AppContext.BaseDirectory, "safety-worker")
        let engine = Path.Combine(worker, "FDull.dll")

        if
            not (File.Exists engine)
            || SafetyPolicy.fileDigest engine
               <> SafetyPolicy.fileDigest typeof<SafetyReport>.Assembly.Location
        then
            Error "BUILD002: The workspace worker differs from the invoking compiler."
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
            start.ArgumentList.Add "--workspace"
            start.Environment.Clear()
            start.Environment["DOTNET_ROOT"] <- runtimeRoot ()
            start.Environment["DOTNET_GCHeapHardLimit"] <- "20000000"
            let text = Codec.encode request

            match
                SafetyProcess.run
                    { Milliseconds = 60000
                      OutputCharacters = SafetyLimits.messageCharacters
                      WorkingSetBytes = SafetyLimits.workingSetBytes }
                    start
                    text
            with
            | Error error -> Error("BUILD003: " + error)
            | Ok output ->
                let result = Codec.decode<WorkspaceProjectReport> output.Output
                let report = result.Report

                if
                    report.Sources <> request.Sources
                    || report.References <> request.References
                    || report.Arguments <> request.Arguments
                    || report.Policy <> request.Policy.Digest
                    || report.Tool <> SafetyPolicy.fileDigest engine
                    || before <> closure ()
                    || request.Policy :: request.Sources @ request.References @ request.BuildInputs
                       |> List.exists (fun expected -> input expected.Path <> expected)
                then
                    Error "BUILD004: The workspace worker report differs from its requested inputs."
                else
                    WorkspaceProtocol.validate
                        request
                        (WorkspacePolicy.read request.Root)
                        output.ExitCode
                        output.Error
                        result
                    |> Result.map (fun () -> result)

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

    let internal checkUsing platform checkProject =
        let root = Path.GetFullPath platform
        let errors = ResizeArray<string>()
        let projects = ResizeArray<WorkspaceProjectReport>()

        try
            let policy = WorkspacePolicy.read root
            WorkspaceAudit.validate root policy
            let projectFiles = WorkspaceAudit.order policy.Projects

            for project in projectFiles do
                match checkProject project with
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
          Errors = List.ofSeq errors }

    let check platform =
        let root = Path.GetFullPath platform
        checkUsing root (fun project -> request root project |> Result.bind analyze)

    let serve (input: TextReader) (output: TextWriter) =
        try
            let request =
                SafetyProcess.readBounded SafetyLimits.messageCharacters input
                |> _.GetAwaiter().GetResult()
                |> Codec.decode<WorkspaceRequest>

            let report = WorkspaceEngine.check request
            output.Write(Codec.encode report)
            output.Flush()
            report.Report.ExitCode
        with error ->
            Console.Error.WriteLine("BUILD003: " + error.Message)
            3
