namespace FDull

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks

type RepositorySelection =
    | All
    | Since of string
    | Service of string

type RepositoryService =
    { Id: string
      Root: string
      Policy: WorkspacePolicyDocument }

type RepositoryPlanResult =
    { Root: string
      Selection: RepositorySelection
      Baseline: string option
      Services: RepositoryService list
      Affected: string list
      Dependencies: string list
      Skipped: string list
      Reasons: Map<string, string list>
      Snapshot: Map<string, string> }

/// Repository selection is advisory to the executor. Its snapshot must be rechecked
/// after all selected builds, and each selected policy still needs full validation.
module RepositoryPlan =
    let private comparer = StringComparison.Ordinal
    let private utf8 = UTF8Encoding(false, true)

    let private omittedNames =
        Set.ofList
            [ "bin"
              "obj"
              ".git"
              ".fdull"
              "artifacts"
              ".artifacts"
              ".nuget-feed"
              ".worktrees"
              "node_modules" ]

    let private omittedPaths =
        Set.ofList [ ".claude/worktrees"; ".pi/git"; ".pi/worktrees" ]

    let private omittedGitPath (path: string) =
        (path.Split('/') |> Array.exists (fun part -> Set.contains part omittedNames))
        || (omittedPaths
            |> Set.exists (fun prefix -> path = prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal)))

    let private contains (parent: string) (child: string) =
        parent = "." || child = parent || child.StartsWith(parent + "/", comparer)

    let private relative root path =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    let private inside root path =
        let item = relative root path

        item = "."
        || (item <> ".."
            && not (item.StartsWith("../", comparer))
            && not (Path.IsPathRooted item))

    let private cleanPath root path =
        let full = Path.GetFullPath path |> Path.TrimEndingDirectorySeparator

        if not (inside root full) then
            invalidOp ("ARCH003: Path escapes repository: " + path)

        full

    let private fileDigest path =
        use input = File.OpenRead path

        if input.Length > 64L * 1024L * 1024L then
            invalidOp ("BUILD003: Selection input exceeds supported size: " + path)

        SHA256.HashData input |> Convert.ToHexStringLower

    let private inventory (root: string) =
        let budget =
            SafetyBudget(maximumSteps = 100000, maximumDepth = 32, milliseconds = 10000)

        let totalBytes = ref 0L

        let rec walk depth directory : seq<string> =
            budget.Visit depth

            Directory.EnumerateFileSystemEntries directory
            |> Seq.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))
            |> Seq.collect (fun entry ->
                budget.Visit depth
                let rel = relative root entry

                let name =
                    Path.GetFileName entry
                    |> Transport.External.required "repository.inventory-name"

                if Set.contains name omittedNames || Set.contains rel omittedPaths then
                    Seq.empty
                elif File.GetAttributes(entry) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint then
                    // Reject aliases as well as escapes: one canonical file must have one owner.
                    invalidOp ("ARCH003: Repository discovery does not support linked paths: " + rel)
                elif Directory.Exists entry then
                    walk (depth + 1) entry
                else
                    let size = FileInfo(entry).Length
                    totalBytes.Value <- totalBytes.Value + size

                    if totalBytes.Value > 512L * 1024L * 1024L then
                        invalidOp "BUILD003: Repository selection inputs exceed supported size."

                    Seq.singleton rel)

        try
            walk 0 root |> Seq.toList
        with error when
            error.Message.Contains("supported work budget", comparer)
            || error.Message.Contains("time budget", comparer)
            || error.Message.Contains("supported depth", comparer) ->
            invalidOp "BUILD003: Repository discovery exceeded its bounded traversal budget."

    let private snapshot root (files: string list) =
        files
        |> List.map (fun path -> path, fileDigest (Path.Combine(root, path)))
        |> Map.ofList

    let private readBounded (stream: Stream) =
        Task.Run(fun () ->
            use result = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 8192
            let mutable doneReading = false

            while not doneReading do
                let count = stream.Read(buffer, 0, buffer.Length)

                if count = 0 then
                    doneReading <- true
                else
                    if result.Length + int64 count > 16L * 1024L * 1024L then
                        invalidOp "BUILD003: Git output exceeded the supported size."

                    result.Write(buffer, 0, count)

            result.ToArray())

    let private git root arguments =
        let start = ProcessStartInfo("git", WorkingDirectory = root)

        for option in
            [ "-c"
              "core.fsmonitor=false"
              "-c"
              "core.hooksPath=/dev/null"
              "-c"
              "diff.external=" ] do
            start.ArgumentList.Add option

        for argument in arguments do
            start.ArgumentList.Add argument

        start.Environment["GIT_OPTIONAL_LOCKS"] <- "0"
        start.UseShellExecute <- false
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        use child = new Process(StartInfo = start)

        if not (child.Start()) then
            invalidOp "BUILD003: Git did not start."

        let output = readBounded child.StandardOutput.BaseStream
        let errors = readBounded child.StandardError.BaseStream
        let clock = Stopwatch.StartNew()

        try
            while not child.HasExited
                  && clock.ElapsedMilliseconds < 15000L
                  && not output.IsFaulted
                  && not errors.IsFaulted do
                child.Refresh()

                if not child.HasExited && child.WorkingSet64 > 1073741824L then
                    invalidOp "BUILD003: Git exceeded its working-set budget."

                child.WaitForExit 25 |> ignore

            if not child.HasExited then
                child.Kill(true)
                child.WaitForExit 5000 |> ignore
                invalidOp "BUILD003: Git exceeded its time or output budget."

            if not (Task.WaitAll([| output :> Task; errors :> Task |], 5000)) then
                invalidOp "BUILD003: Git streams did not close after process exit."

            let stdout = output.GetAwaiter().GetResult()
            let stderr = errors.GetAwaiter().GetResult()

            if child.ExitCode <> 0 then
                let detail = utf8.GetString(stderr).Trim()
                invalidOp ("BUILD003: Git failed: " + detail)

            stdout
        finally
            if not child.HasExited then
                child.Kill(true)

    let private gitText root arguments =
        git root arguments |> utf8.GetString |> (fun value -> value.Trim())

    let private nulFields (bytes: byte array) =
        if bytes.Length = 0 then
            []
        elif bytes[bytes.Length - 1] <> 0uy then
            invalidOp "BUILD003: Incomplete NUL-delimited Git output."
        else
            let rec fields offset count accumulated =
                if offset = bytes.Length then
                    List.rev accumulated
                else
                    if count >= 100000 then
                        invalidOp "BUILD003: Git path record count exceeds the supported bound."

                    let finish = Array.IndexOf(bytes, 0uy, offset)

                    if finish < 0 then
                        invalidOp "BUILD003: Incomplete NUL-delimited Git output."

                    let value = utf8.GetString(bytes, offset, finish - offset)
                    fields (finish + 1) (count + 1) (value :: accumulated)

            fields 0 0 []

    let private validGitPath (path: string) =
        not (String.IsNullOrEmpty path)
        && not (Path.IsPathRooted path)
        && not (path.Contains('\\'))
        && (path.Split('/')
            |> Array.forall (fun segment -> segment <> "" && segment <> "." && segment <> ".."))

    let private normalizedGitPath (path: string) =
        if not (validGitPath path) then
            invalidOp ("BUILD003: Unsafe Git path: " + path)

        path

    let private changedPaths root baseline =
        let names =
            git
                root
                [ "diff"
                  "--no-ext-diff"
                  "--no-textconv"
                  "--name-status"
                  "-z"
                  "--find-renames"
                  baseline
                  "--" ]
            |> nulFields

        let rec decode (items: string list) count accumulated =
            match items with
            | [] -> List.rev accumulated
            | _ when count >= 100000 -> invalidOp "BUILD003: Git change record count exceeds the supported bound."
            | status :: oldPath :: newPath :: tail when
                status.StartsWith("R", comparer) || status.StartsWith("C", comparer)
                ->
                let oldPath = normalizedGitPath oldPath
                let newPath = normalizedGitPath newPath
                decode tail (count + 1) (newPath :: oldPath :: accumulated)
            | status :: path :: tail when status.Length > 0 ->
                decode tail (count + 1) (normalizedGitPath path :: accumulated)
            | _ -> invalidOp "BUILD003: Malformed Git change record."

        let untracked =
            git root [ "ls-files"; "--others"; "--exclude-standard"; "-z" ] |> nulFields

        (decode names 0 [] @ (untracked |> List.map normalizedGitPath)) |> Set.ofList

    let private historicFiles root baseline =
        git root [ "ls-tree"; "-r"; "-z"; "--name-only"; baseline ]
        |> nulFields
        |> List.map normalizedGitPath
        |> List.filter (omittedGitPath >> not)

    let private baselineBlob root baseline path =
        // Commit ID is resolved before this call; path is a validated tree entry.
        git root [ "show"; baseline + ":" + path ] |> utf8.GetString

    let private serviceId root path = relative root path

    let private serviceForPath (services: RepositoryService list) path =
        services |> List.tryFind (fun service -> contains service.Id path)

    let private discover (root: string) (files: string list) =
        let visibleFiles = Set.ofList files

        let policies =
            files
            |> List.filter (fun file -> Path.GetFileName file = "fdull.json")
            |> List.map (fun file ->
                Path.GetDirectoryName(Path.Combine(root, file))
                |> Transport.External.required "repository.policy-directory"
                |> cleanPath root)
            |> List.sortWith (fun left right -> StringComparer.Ordinal.Compare(left, right))

        if policies.IsEmpty then
            invalidOp "ARCH002: No service policies were discovered."

        if policies.Length > 256 then
            invalidOp "BUILD003: Too many service policies."

        let roots = policies |> List.map (serviceId root)

        if
            roots
            |> List.exists (fun left -> roots |> List.exists (fun right -> left <> right && contains left right))
        then
            invalidOp "ARCH002: Nested policy ownership is unsupported."

        policies
        |> List.map (fun serviceRoot ->
            let policy = WorkspacePolicy.readStructure serviceRoot
            WorkspacePolicy.validateRepositoryStructure serviceRoot policy

            match policy.Repository with
            | Some repository ->
                let declared =
                    Path.GetFullPath(repository.Root, serviceRoot)
                    |> Path.TrimEndingDirectorySeparator

                if declared <> root then
                    invalidOp "BUILD005: Service repository root does not match coordination root."
            | None when serviceRoot = root -> ()
            | None ->
                invalidOp "BUILD005: A service below the repository root requires a version-two repository policy."

            let localInputs =
                policy.Inputs
                |> List.map (fun pin -> relative root (Path.Combine(serviceRoot, pin.File)))

            let sharedInputs =
                policy.Repository
                |> Option.map (fun repository -> repository.SharedInputs |> List.map _.File)
                |> Option.defaultValue []

            match
                (localInputs @ sharedInputs)
                |> List.tryFind (fun input -> not (Set.contains input visibleFiles))
            with
            | Some input -> invalidOp ("BUILD005: Protected input is outside repository discovery: " + input)
            | None -> ()

            WorkspaceAudit.validate serviceRoot policy

            { Id = serviceId root serviceRoot
              Root = serviceRoot
              Policy = policy })

    let private validateOwnership (files: string list) (services: RepositoryService list) =
        for path in files do
            if List.contains (Path.GetExtension path) [ ".fsproj"; ".fs"; ".fsi" ] then
                match serviceForPath services path with
                | Some _ -> ()
                | None -> invalidOp ("ARCH002: Unowned F# project or source: " + path)

    let private graph (services: RepositoryService list) =
        let projects =
            services
            |> List.collect (fun service ->
                service.Policy.Projects
                |> List.map (fun project ->
                    let file =
                        if service.Id = "." then
                            project.File
                        else
                            service.Id + "/" + project.File

                    file, (service.Id, project.Profile)))
            |> Map.ofList

        let edges =
            services
            |> List.collect (fun service ->
                let reviewedExternal = WorkspaceAudit.externalProjects service.Policy

                let local =
                    service.Policy.Projects
                    |> List.collect (fun project ->
                        project.References
                        |> List.filter (fun target -> not (Set.contains target reviewedExternal))
                        |> List.map (fun target ->
                            let source =
                                if service.Id = "." then
                                    project.File
                                else
                                    service.Id + "/" + project.File

                            let dest =
                                if service.Id = "." then
                                    target
                                else
                                    service.Id + "/" + target

                            source, dest))

                let cross =
                    service.Policy.Repository
                    |> Option.map (fun repository ->
                        repository.References
                        |> List.map (fun edge ->
                            let source =
                                if service.Id = "." then
                                    edge.Project
                                else
                                    service.Id + "/" + edge.Project

                            source, edge.Target))
                    |> Option.defaultValue []

                local @ cross)

        let serviceEdges =
            edges
            |> List.choose (fun (source, target) ->
                match Map.tryFind source projects, Map.tryFind target projects with
                | Some(owner, _), Some(dependency, _) -> Some(owner, dependency)
                | _ -> None)
            |> Set.ofList

        projects, edges, serviceEdges

    let private validateGraph services =
        let projects, edges, serviceEdges = graph services

        if projects.Count > 2048 then
            invalidOp "BUILD003: Repository project graph exceeds supported size."

        for service in services do
            match service.Policy.Repository with
            | None -> ()
            | Some repository ->
                for edge in repository.References do
                    let owner =
                        services
                        |> List.tryFind (fun candidate ->
                            let path =
                                if candidate.Id = "." then
                                    "fdull.json"
                                else
                                    candidate.Id + "/fdull.json"

                            path = edge.Policy)

                    match owner, Map.tryFind edge.Target projects with
                    | Some candidate, Some(actualOwner, actualProfile) when
                        candidate.Id = actualOwner && edge.Profile = actualProfile
                        ->
                        ()
                    | _ ->
                        invalidOp (
                            "ARCH001: Cross-policy reference target, owner, or profile differs: "
                            + edge.Target
                        )

        for source, target in edges do
            if not (Map.containsKey source projects && Map.containsKey target projects) then
                invalidOp ("ARCH001: Repository project reference has an unknown target: " + target)

        let rec visit stack visited node =
            if Set.contains node stack then
                invalidOp "ARCH001: Repository project graph contains a cycle."
            elif Set.contains node visited then
                visited
            else
                edges
                |> List.filter (fun (source, _) -> source = node)
                |> List.fold (fun seen (_, target) -> visit (Set.add node stack) seen target) (Set.add node visited)

        projects
        |> Map.toList
        |> List.fold (fun seen (file, _) -> visit Set.empty seen file) Set.empty
        |> ignore

        serviceEdges

    let private historicGraph root baseline (files: string list) =
        let policyFiles =
            files |> List.filter (fun file -> Path.GetFileName file = "fdull.json")

        if policyFiles.Length > 256 then
            invalidOp "BUILD003: Baseline has too many service policies."

        policyFiles
        |> List.map (fun path ->
            let id =
                Path.GetDirectoryName(Path.Combine(root, path))
                |> Transport.External.required "repository.historical-policy-directory"
                |> relative root

            let policy = baselineBlob root baseline path |> WorkspacePolicy.decodeStructure

            { Id = id
              Root = Path.Combine(root, id)
              Policy = policy })

    let private reverseClosure (edges: Set<string * string>) initial =
        let rec expand selected =
            let next =
                edges
                |> Seq.filter (fun (_, dependency) -> Set.contains dependency selected)
                |> Seq.map fst
                |> Set.ofSeq

            let result = Set.union selected next
            if result = selected then selected else expand result

        expand initial

    let private dependencyClosure (edges: Set<string * string>) initial =
        let rec expand selected =
            let next =
                edges
                |> Seq.filter (fun (owner, _) -> Set.contains owner selected)
                |> Seq.map snd
                |> Set.ofSeq

            let result = Set.union selected next
            if result = selected then selected else expand result

        expand initial

    let private affectedForChange (services: RepositoryService list) (changed: string) =
        let fromDirectory =
            serviceForPath services changed |> Option.map _.Id |> Option.toList

        let fromShared =
            services
            |> List.choose (fun service ->
                service.Policy.Repository
                |> Option.bind (fun repository ->
                    if repository.SharedInputs |> List.exists (fun pin -> pin.File = changed) then
                        Some service.Id
                    else
                        None))

        fromDirectory @ fromShared

    let private planCore (root: string) selection =
        let root = Path.GetFullPath root |> Path.TrimEndingDirectorySeparator

        if not (Directory.Exists root) then
            invalidOp "ARCH002: Repository root does not exist."

        let rec checkRootAlias (path: string) =
            if File.GetAttributes(path) &&& FileAttributes.ReparsePoint = FileAttributes.ReparsePoint then
                invalidOp ("ARCH003: Repository root uses a linked path: " + path)

            let pathRoot =
                Path.GetPathRoot path |> Transport.External.required "repository.path-root"

            if path <> pathRoot then
                Path.GetDirectoryName path
                |> Transport.External.required "repository.root-parent"
                |> checkRootAlias

        checkRootAlias root
        let files = inventory root
        let currentSnapshot = snapshot root files
        let services = discover root files
        validateOwnership files services
        let currentEdges = validateGraph services
        let ids = services |> List.map _.Id |> Set.ofList

        let baseline, affected, reasons =
            match selection with
            | All -> None, ids, (ids |> Seq.map (fun id -> id, [ "full verification" ]) |> Map.ofSeq)
            | Service id ->
                if not (Set.contains id ids) then
                    invalidOp ("ARCH002: Unknown service policy: " + id)

                None, Set.singleton id, Map.ofList [ id, [ "explicit service verification" ] ]
            | Since revision ->
                if String.IsNullOrWhiteSpace revision || revision.StartsWith("-", comparer) then
                    invalidOp "BUILD003: An explicit commit baseline is required."

                let top =
                    gitText root [ "rev-parse"; "--show-toplevel" ]
                    |> Path.GetFullPath
                    |> Path.TrimEndingDirectorySeparator

                if top <> root then
                    invalidOp "BUILD003: Coordination root must equal the Git worktree root."

                let commit =
                    gitText root [ "rev-parse"; "--verify"; "--end-of-options"; revision + "^{commit}" ]

                if
                    commit.Length <> 40
                    || commit |> Seq.exists (fun character -> not (Uri.IsHexDigit character))
                then
                    invalidOp "BUILD003: Git did not resolve an immutable commit ID."

                let baselineFiles = historicFiles root commit
                let historical = historicGraph root commit baselineFiles
                let historicalIds = historical |> List.map _.Id

                if
                    historicalIds
                    |> List.exists (fun left ->
                        historicalIds |> List.exists (fun right -> left <> right && contains left right))
                then
                    invalidOp "ARCH002: Baseline contains nested policy roots."

                let historicalEdges = validateGraph historical
                let addedInventory = Set.difference (Set.ofList files) (Set.ofList baselineFiles)

                let changed = Set.union (changedPaths root commit) addedInventory

                let direct =
                    changed
                    |> Seq.collect (fun path ->
                        affectedForChange services path
                        @ (historical
                           |> List.filter (fun service -> contains service.Id path)
                           |> List.map _.Id))
                    |> Set.ofSeq

                let changedGraph =
                    historicalEdges <> currentEdges || Set.ofList historicalIds <> ids

                let initial = if changedGraph then ids else Set.intersect ids direct
                let unionEdges = Set.union currentEdges historicalEdges
                let affected = reverseClosure unionEdges initial |> Set.intersect ids

                let reasons =
                    ids
                    |> Seq.choose (fun id ->
                        if Set.contains id affected then
                            let why =
                                if changedGraph then
                                    [ "repository ownership or dependency graph changed" ]
                                elif Set.contains id initial then
                                    [ "changed service or reviewed shared input" ]
                                else
                                    [ "transitive consumer of changed service" ]

                            Some(id, why)
                        else
                            None)
                    |> Map.ofSeq

                Some commit, affected, reasons

        let required = dependencyClosure currentEdges affected
        let dependencies = Set.difference required affected
        let skipped = Set.difference ids required

        let reasons =
            dependencies
            |> Seq.fold (fun state id -> Map.add id [ "dependency required by selected service" ] state) reasons

        if snapshot root (inventory root) <> currentSnapshot then
            invalidOp "BUILD005: Repository selection inputs changed while planning."

        { Root = root
          Selection = selection
          Baseline = baseline
          Services = services
          Affected = affected |> Set.toList
          Dependencies = dependencies |> Set.toList
          Skipped = skipped |> Set.toList
          Reasons = reasons
          Snapshot = currentSnapshot }

    let plan root selection =
        try
            Ok(planCore root selection)
        with error ->
            Error error.Message

    let revalidate (plan: RepositoryPlanResult) =
        try
            let files = inventory plan.Root
            let latest = snapshot plan.Root files

            if latest <> plan.Snapshot then
                invalidOp "BUILD005: Repository selection inputs changed during verification."

            match plan.Selection, plan.Baseline with
            | Since _, Some baseline ->
                let replanned = planCore plan.Root (Since baseline)

                if
                    replanned.Affected <> plan.Affected
                    || replanned.Dependencies <> plan.Dependencies
                then
                    invalidOp "BUILD005: Repository selection changed during verification."
            | All, None -> ()
            | Service _, None -> ()
            | All, Some _
            | Service _, Some _
            | Since _, None -> invalidOp "BUILD005: Invalid repository plan baseline."

            Ok()
        with error ->
            Error error.Message
