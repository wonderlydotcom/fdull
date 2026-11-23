namespace FDull

open System
open System.IO
open FDull.Transport

/// One Bazel target of the workspace and the file that holds its compiler arguments, one per line,
/// exactly as rules_dotnet passed them to the F# compiler.
type WorkspaceManifestProject = { Name: string; Arguments: string }

/// What a Bazel build tells FDull about one workspace: its F# targets, and the build system's
/// inventory of every file in the workspace directory.
type WorkspaceManifestDocument =
    { Version: int
      Projects: WorkspaceManifestProject list
      Files: string list }

/// One manifest project's compiler arguments in the spellings `WorkspaceCompilerPolicy` knows, with
/// absolute paths. `Sources` and `Flags` each keep the order of the argument file.
type internal WorkspaceManifestArguments =
    { Project: string
      Sources: string list
      Flags: string list }

/// The Bazel front end. A manifest written by the build replaces the MSBuild export and the directory
/// walk, neither of which can run in a sandbox that holds only declared inputs.
module WorkspaceManifest =
    let private manifestBytes = 64L * 1024L * 1024L

    let private argumentBytes = 4L * 1024L * 1024L

    let private inventoryCount = 100000

    let private bounded limit (file: string) =
        if FileInfo(file).Length > limit then
            invalidOp ("BUILD003: A build-written file exceeds the supported size: " + file)

    /// Read a manifest and check its own shape. A relative path resolves against the working directory.
    let read (file: string) =
        bounded manifestBytes file
        let manifest = File.ReadAllText file |> Codec.decode<WorkspaceManifestDocument>
        let names = manifest.Projects |> List.map _.Name

        if manifest.Version <> 1 then
            invalidOp "BUILD003: Missing or unsupported build manifest version."

        if
            names.IsEmpty
            || names.Length > 128
            || names.Length <> (Set.ofList names).Count
            || names |> List.exists (WorkspacePolicy.target >> not)
            || manifest.Projects
               |> List.exists (fun project -> String.IsNullOrWhiteSpace project.Arguments)
        then
            invalidOp "BUILD003: The build manifest must name 1 to 128 distinct targets, each with an argument file."

        if
            manifest.Files.Length > inventoryCount
            || manifest.Files |> List.exists String.IsNullOrWhiteSpace
        then
            invalidOp "BUILD003: The build manifest inventory is invalid or exceeds 100,000 files."

        manifest

    /// The manifest's inventory relative to the workspace root, in the form the audit compares with the
    /// policy. Only names are used: most of these files are absent from a sandbox.
    let inventory (root: string) (manifest: WorkspaceManifestDocument) =
        let root = Path.GetFullPath root

        manifest.Files
        |> List.map (fun file ->
            let relative = Path.GetRelativePath(root, Path.GetFullPath file).Replace('\\', '/')

            if not (WorkspacePolicy.localFile relative) then
                invalidOp ("BUILD003: A manifest file lies outside the workspace: " + file)

            relative)
        |> List.distinct
        |> List.sort

    type private Argument =
        | Source of string
        | Flag of string

    let private pathFlags =
        [ "--out:", "-o:"
          "-r:", "-r:"
          "--doc:", "--doc:"
          "--pdb:", "--pdb:"
          "--refout:", "--refout:" ]

    let private spellings =
        [ "-d:", "--define:"
          "/warn:", "--warn:"
          "/warnaserror", "--warnaserror"
          "/nowarn:", "--nowarn:" ]

    let private classify (directory: string) (argument: string) =
        let renamed table =
            table
            |> List.tryPick (fun (prefix: string, canonical: string) ->
                if argument.StartsWith(prefix, StringComparison.Ordinal) then
                    Some(canonical, argument.Substring prefix.Length)
                else
                    None)

        match renamed pathFlags, renamed spellings with
        | Some(flag, path), _ when path <> "" -> Flag(flag + Path.GetFullPath(path, directory))
        | Some _, _ -> Flag argument
        | None, Some(flag, rest) -> Flag(flag + rest)
        | None, None ->
            if
                not (argument.StartsWith("-", StringComparison.Ordinal))
                && List.contains (Path.GetExtension argument) [ ".fs"; ".fsi" ]
            then
                Source(Path.GetFullPath(argument, directory))
            else
                Flag argument

    /// rules_dotnet's argument spellings rewritten to the ones `WorkspaceCompilerPolicy` knows, with
    /// every path made absolute against the full path `directory`. An argument this does not recognize
    /// passes through unchanged, so the compiler policy still rejects it.
    let rewrite (directory: string) (arguments: string list) =
        arguments
        |> List.map (fun argument ->
            match classify directory argument with
            | Source file -> file
            | Flag flag -> flag)

    /// The values of every flag that starts with `prefix`, in order.
    let internal values (prefix: string) (flags: string list) =
        flags
        |> List.choose (fun flag ->
            if flag.StartsWith(prefix, StringComparison.Ordinal) then
                Some(flag.Substring prefix.Length)
            else
                None)

    /// A project's compiled output and reference output.
    let private outputs (flags: string list) =
        values "-o:" flags @ values "--refout:" flags

    /// Read every project's argument file once. Paths resolve against the working directory, as they
    /// did for the compiler. Two projects cannot write one output: the derived graph would be ambiguous.
    let internal load (manifest: WorkspaceManifestDocument) =
        let directory = Directory.GetCurrentDirectory()

        let projects =
            manifest.Projects
            |> List.map (fun project ->
                bounded argumentBytes project.Arguments

                let arguments =
                    File.ReadAllLines project.Arguments
                    |> Array.toList
                    |> List.filter (fun line -> line <> "")
                    |> List.map (classify directory)

                { Project = project.Name
                  Sources =
                    arguments
                    |> List.choose (fun argument ->
                        match argument with
                        | Source file -> Some file
                        | Flag _ -> None)
                  Flags =
                    arguments
                    |> List.choose (fun argument ->
                        match argument with
                        | Flag flag -> Some flag
                        | Source _ -> None) })

        let written = projects |> List.collect (fun project -> outputs project.Flags)

        if written.Length <> (Set.ofList written).Count then
            invalidOp "BUILD003: Two manifest projects write the same compiler output."

        projects

    /// The manifest projects whose compiled or reference output `project` passes to its compiler. The
    /// graph comes from the invocations themselves, so the manifest carries no reference list.
    let internal projectReferences (all: WorkspaceManifestArguments list) (project: WorkspaceManifestArguments) =
        let referenced = values "-r:" project.Flags |> Set.ofList

        all
        |> List.filter (fun other ->
            other.Project <> project.Project
            && outputs other.Flags
               |> List.exists (fun output -> Set.contains output referenced))
        |> List.map _.Project
