namespace FDull

open System
open System.IO
open System.Xml.Linq

/// A consumer supplies an exact graph; the profile direction rules stay in the engine.
module WorkspaceAudit =
    let validateEdges (projects: WorkspaceProject list) project references =
        let graph = projects |> List.map (fun item -> item.File, item) |> Map.ofList

        match Map.tryFind project graph with
        | None -> Error "ARCH002: Unclassified project."
        | Some owner ->
            let permitted target =
                match Map.tryFind target graph with
                | None -> false
                | Some dependency ->
                    match owner.Profile with
                    | "PURE"
                    | "PURE_HELPER" -> List.contains dependency.Profile [ "PURE"; "PURE_HELPER"; "CONTRACTS" ]
                    | "CONTRACTS" -> dependency.Profile = "CONTRACTS"
                    | "BOUNDARY"
                    | "ADAPTER" ->
                        List.contains dependency.Profile [ "PURE"; "PURE_HELPER"; "CONTRACTS"; "BOUNDARY"; "ADAPTER" ]
                    | "GUARD_TOOLING" -> dependency.Profile <> "TEST"
                    | "TEST" -> true
                    | _ -> false

            if
                owner.Profile <> "TEST"
                && references
                   |> List.exists (fun target ->
                       Map.tryFind target graph |> Option.exists (fun value -> value.Profile = "TEST"))
            then
                Error "ARCH004: Shipping code cannot depend on test projects."
            elif
                List.contains project references
                || references.Length <> (Set.ofList references).Count
                || Set.ofList references <> Set.ofList owner.References
            then
                Error "ARCH001: Project references differ from the protected graph."
            elif not (List.forall permitted references) then
                Error "ARCH001: A dependency has an unapproved profile direction."
            else
                Ok()

    let private acyclic (projects: WorkspaceProject list) =
        let rec removeLeaves remaining =
            match remaining with
            | [] -> true
            | _ ->
                let leaves =
                    remaining
                    |> List.filter (fun item -> item.References.IsEmpty)
                    |> List.map _.File
                    |> Set.ofList

                if leaves.IsEmpty then
                    false
                else
                    remaining
                    |> List.filter (fun item -> not (Set.contains item.File leaves))
                    |> List.map (fun item ->
                        { item with
                            References = item.References |> List.filter (fun file -> not (Set.contains file leaves)) })
                    |> removeLeaves

        removeLeaves projects

    let internal order (projects: WorkspaceProject list) =
        let rec dependencyFirst remaining =
            match remaining with
            | [] -> []
            | _ ->
                let leaves = remaining |> List.filter (fun item -> item.References.IsEmpty)

                if leaves.IsEmpty then
                    invalidOp "ARCH001: The project graph contains a cycle."

                let names = leaves |> List.map _.File |> Set.ofList

                let next =
                    remaining
                    |> List.filter (fun item -> not (Set.contains item.File names))
                    |> List.map (fun item ->
                        { item with
                            References = item.References |> List.filter (fun file -> not (Set.contains file names)) })

                (leaves |> List.map _.File) @ dependencyFirst next

        dependencyFirst projects

    let unused (policy: WorkspacePolicyDocument) (uses: WorkspaceUse list) =
        let observed =
            uses
            |> List.map (fun item -> item.File, item.Owner, item.Kind, item.Identity)
            |> Set.ofList

        policy.Capabilities
        |> List.filter (fun entry -> not (Set.contains (entry.File, entry.Owner, entry.Kind, entry.Identity) observed))
        |> List.map (fun entry -> entry.File + " | " + entry.Owner + " | " + entry.Kind + " | " + entry.Identity)

    let unusedConstructors (policy: WorkspacePolicyDocument) (uses: WorkspaceUse list) =
        let observed =
            uses
            |> List.filter (fun item -> item.Kind = "constructor")
            |> List.map _.Identity
            |> Set.ofList

        policy.Constructors
        |> List.filter (fun identity -> not (Set.contains identity observed))

    let inventory root =
        let budget =
            SafetyBudget(maximumSteps = 20000, maximumDepth = 32, milliseconds = 10000)

        let rec walk depth relative =
            budget.Visit depth

            Directory.EnumerateFileSystemEntries(Path.Combine(root, relative))
            |> Seq.sort
            |> Seq.toList
            |> List.collect (fun path ->
                budget.Visit depth
                let name = Path.GetFileName path |> Transport.External.required "inventory.filename"
                let next = Path.GetRelativePath(root, path).Replace('\\', '/')

                if List.contains name [ "bin"; "obj"; ".git"; ".fdull"; "artifacts" ] then
                    []
                elif File.GetAttributes(path) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint then
                    invalidOp ("ARCH003: Linked inputs are unsupported: " + next)
                elif Directory.Exists path then
                    walk (depth + 1) next
                else
                    [ next ])

        walk 0 ""

    let references (root: string) (project: string) =
        let directory =
            Path.GetDirectoryName(Path.Combine(root, project))
            |> Transport.External.required "project.directory"

        let xml = XDocument.Load(Path.Combine(root, project))

        xml.Descendants(XName.Get "ProjectReference")
        |> Seq.map (fun item ->
            let path =
                (item.Attribute(XName.Get "Include")
                 |> Transport.External.required "project.reference")
                    .Value

            Path.GetRelativePath(root, Path.GetFullPath(path, directory)).Replace('\\', '/'))
        |> Seq.toList

    let validate root (policy: WorkspacePolicyDocument) =
        let files = inventory root
        let projects = policy.Projects |> List.map _.File |> Set.ofList

        let actualProjects =
            files
            |> List.filter (fun file -> Path.GetExtension file = ".fsproj")
            |> Set.ofList

        if
            projects.IsEmpty
            || projects.Count <> policy.Projects.Length
            || projects <> actualProjects
        then
            invalidOp "ARCH002: Actual and protected project inventories must match exactly."

        if policy.Projects.Length > 128 || not (acyclic policy.Projects) then
            invalidOp "ARCH001: The project graph must be acyclic and contain at most 128 projects."

        let declared = policy.Sources |> List.map _.File |> Set.ofList

        let actualSources =
            files
            |> List.filter (fun file -> List.contains (Path.GetExtension file) [ ".fs"; ".fsi" ])
            |> Set.ofList

        if declared <> actualSources then
            invalidOp "ARCH002: Unclassified or missing F# source."

        let pins = policy.Inputs |> List.map _.File |> Set.ofList

        for file in files do
            let extension =
                (Path.GetExtension file |> Transport.External.required "inventory.extension").ToLowerInvariant()

            if
                List.contains
                    extension
                    [ ".fsx"
                      ".cs"
                      ".csx"
                      ".vb"
                      ".js"
                      ".mjs"
                      ".cjs"
                      ".ts"
                      ".tsx"
                      ".py"
                      ".rb"
                      ".sh"
                      ".bash"
                      ".ps1"
                      ".cmd"
                      ".bat"
                      ".rsp" ]
            then
                invalidOp (
                    "ARCH003: Authored implementation and automation must be classified compiled F#: "
                    + file
                )

            if
                (List.contains
                    extension
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
                     ((Path.GetFileName file |> Transport.External.required "inventory.filename").ToLowerInvariant())
                     [ "nuget.config"; "global.json"; "packages.lock.json"; "dotnet-tools.json" ])
                && not (Set.contains file pins)
            then
                invalidOp ("BUILD005: Unpinned build input: " + file)

        for project in policy.Projects do
            if not (Set.contains project.Profile WorkspacePolicy.profiles) then
                invalidOp "ARCH002: Unknown project profile."

            match validateEdges policy.Projects project.File (references root project.File) with
            | Error error -> invalidOp error
            | Ok() -> ()

            if not (policy.Sources |> List.exists (fun source -> source.Project = project.File)) then
                invalidOp "ARCH002: Every project must contain classified F# source."

        for source in policy.Sources do
            match policy.Projects |> List.tryFind (fun project -> project.File = source.Project) with
            | Some project when project.Profile = source.Profile -> ()
            | _ -> invalidOp "ARCH002: Source/project profile assignment mismatch."

        let required =
            policy.Projects
            |> List.collect (fun project ->
                [ project.File
                  Path.Combine(
                      Path.GetDirectoryName project.File
                      |> Transport.External.required "project.lock-directory",
                      "packages.lock.json"
                  ) ])

        if not (Set.isSubset (Set.ofList required) pins) then
            invalidOp "BUILD005: Missing project/lock input pins."
