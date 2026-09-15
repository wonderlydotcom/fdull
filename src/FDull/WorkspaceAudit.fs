namespace FDull

open System
open System.IO
open System.Xml.Linq

/// A consumer supplies an exact graph; the profile direction rules stay in the engine.
module WorkspaceAudit =
    let private contains (parent: string) (child: string) =
        child = parent || child.StartsWith(parent + "/", StringComparison.Ordinal)

    let internal externalProjects (policy: WorkspacePolicyDocument) =
        let implementations =
            WorkspacePolicy.externalEntries policy
            |> List.filter (fun entry -> entry.Kind = "implementation")

        policy.Inputs
        |> List.map _.File
        |> List.filter (fun file ->
            List.contains (Path.GetExtension file) [ ".csproj"; ".vbproj" ]
            && implementations |> List.exists (fun entry -> contains entry.Path file))
        |> Set.ofList

    let private validateEdgesCore (projects: WorkspaceProject list) external exact project references =
        let graph = projects |> List.map (fun item -> item.File, item) |> Map.ofList

        match Map.tryFind project graph with
        | None -> Error "ARCH002: Unclassified project."
        | Some owner ->
            let expected = owner.References
            let expectedSet = Set.ofList expected
            let observedSet = Set.ofList references

            let permitted target =
                match Map.tryFind target graph with
                | None -> Set.contains target external
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
                && (expected @ references)
                   |> List.exists (fun target ->
                       Map.tryFind target graph |> Option.exists (fun value -> value.Profile = "TEST"))
            then
                Error "ARCH004: Shipping code cannot depend on test projects."
            elif
                List.contains project expected
                || expected.Length <> expectedSet.Count
                || List.contains project references
                || references.Length <> observedSet.Count
                || (if exact then
                        observedSet <> expectedSet
                    else
                        not (Set.isSubset observedSet expectedSet))
            then
                Error "ARCH001: Project references differ from the protected graph."
            elif
                expected
                |> List.exists (fun target -> not (Map.containsKey target graph || Set.contains target external))
            then
                Error "ARCH001: A project reference is neither protected F# nor reviewed external implementation."
            elif not (List.forall permitted expected) then
                Error "ARCH001: A dependency has an unapproved profile direction."
            else
                Ok()

    let validateEdges projects project references =
        validateEdgesCore projects Set.empty true project references

    let internal validateDeclaredEdges projects external project references =
        validateEdgesCore projects external false project references

    let internal validateEdgesWithExternal projects external project references =
        validateEdgesCore projects external true project references

    let private protectedGraph (projects: WorkspaceProject list) =
        let names = projects |> List.map _.File |> Set.ofList

        projects
        |> List.map (fun project ->
            { project with
                References =
                    project.References
                    |> List.filter (fun reference -> Set.contains reference names) })

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

        removeLeaves (protectedGraph projects)

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

        dependencyFirst (protectedGraph projects)

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
            SafetyBudget(maximumSteps = 100000, maximumDepth = 32, milliseconds = 10000)

        let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)

        let insideRoot path =
            let relative = Path.GetRelativePath(root, path).Replace('\\', '/')

            relative = "."
            || (not (Path.IsPathRooted relative)
                && relative <> ".."
                && not (relative.StartsWith("../", StringComparison.Ordinal)))

        let rec walk depth relative =
            budget.Visit depth

            Directory.EnumerateFileSystemEntries(Path.Combine(root, relative))
            |> Seq.sort
            |> Seq.toList
            |> List.collect (fun path ->
                budget.Visit depth
                let name = Path.GetFileName path |> Transport.External.required "inventory.filename"
                let next = Path.GetRelativePath(root, path).Replace('\\', '/')

                if
                    List.contains
                        name
                        [ "bin"
                          "obj"
                          ".git"
                          ".fdull"
                          "artifacts"
                          ".artifacts"
                          ".nuget-feed"
                          ".worktrees"
                          "node_modules" ]
                    || List.contains next [ ".claude/worktrees"; ".pi/git"; ".pi/worktrees" ]
                then
                    []
                elif File.GetAttributes(path) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint then
                    let target =
                        if Directory.Exists path then
                            DirectoryInfo(path).ResolveLinkTarget(true)
                        else
                            FileInfo(path).ResolveLinkTarget(true)
                        |> Transport.External.required ("ARCH003: Broken linked input: " + next)

                    let resolved = Path.GetFullPath target.FullName

                    if not target.Exists || not (insideRoot resolved) then
                        invalidOp ("ARCH003: Linked input escapes the workspace or is missing: " + next)

                    []
                elif Directory.Exists path then
                    walk (depth + 1) next
                else
                    [ next ])

        try
            walk 0 ""
        with error when
            error.Message = "Analysis traversal exceeded the supported work budget."
            || error.Message = "Analysis nesting exceeded the supported work budget."
            || error.Message = "Analysis exceeded the supported time budget." ->
            invalidOp "BUILD003: Workspace inventory exceeded its bounded traversal budget."

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
                    .Value.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)

            Path.GetRelativePath(root, Path.GetFullPath(path, directory)).Replace('\\', '/'))
        |> Seq.toList

    let validate root (policy: WorkspacePolicyDocument) =
        let files = inventory root
        let external = WorkspacePolicy.externalEntries policy

        let classified file =
            external |> List.exists (fun entry -> contains entry.Path file)

        for entry in external do
            let full = Path.Combine(root, entry.Path)
            let owned = files |> List.filter (contains entry.Path)

            if (not (File.Exists full || Directory.Exists full)) || owned.IsEmpty then
                invalidOp ("BUILD005: External classification is missing or unused: " + entry.Path)

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

        if policy.Projects.Length > 128 then
            invalidOp (
                "ARCH001: The protected F# project graph contains "
                + string policy.Projects.Length
                + " projects; the supported maximum is 128."
            )

        if not (acyclic policy.Projects) then
            invalidOp "ARCH001: The protected F# project graph contains a cycle."

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
                && not (classified file)
            then
                invalidOp (
                    "ARCH003: Non-F# implementation or automation requires an explicit external classification: "
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

        let externalProjectReferences = externalProjects policy

        for project in policy.Projects do
            if not (Set.contains project.Profile WorkspacePolicy.profiles) then
                invalidOp "ARCH002: Unknown project profile."

            match
                validateDeclaredEdges
                    policy.Projects
                    externalProjectReferences
                    project.File
                    (references root project.File)
            with
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
