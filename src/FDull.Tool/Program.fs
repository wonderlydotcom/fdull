namespace FDull.Tool

open System
open System.Diagnostics
open System.Globalization
open System.IO
open FDull
open FDull.Transport

/// One reported location: a finding's rule id, or the code of what blocked the check.
type CheckOutputItem =
    { Rule: string
      File: string
      StartLine: int
      StartColumn: int
      EndLine: int
      EndColumn: int
      Symbol: string
      Message: string }

/// Machine-readable `fdull check` result for CI.
type CheckOutput =
    { Status: string
      ExitCode: int
      Project: string
      Files: string list
      Rules: string list
      Claim: string
      Reason: string option
      Findings: CheckOutputItem list }

type CheckFormat =
    | CheckText
    | CheckJson

module Program =
    let private usage () =
        Console.WriteLine "FDull — F# with fewer sharp edges."

        Console.WriteLine
            "fdull defaults | init <root> [--scope <file>] | check <project.fsproj> --rules <ids> <file-or-directory>... [--format text|json] | lint <root> [--format text|json|sarif] [--jobs <1-64>] [--memory <256-16384>] [--no-cache] [--timings] [--manifest <file>] [--report <file>] | review <root> [--format text|json] [--jobs <1-64>] [--memory <256-16384>] [--no-cache] [--timings] [--manifest <file>] [--report <file>] | cache clear <root> | data-digest <assembly> | policy-digest <fdull.json> | verify <root> [--all | --since <commit>] [--jobs <1-64>] [--memory <256-16384>] [--cache | --no-cache] [--timings] | coverage"

        2

    /// Concurrent projects: a decimal count from 1 to 64.
    let private jobCount (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, count when count >= 1 && count <= 64 -> Some count
        | _ -> None

    /// Analysis worker heap ceiling: a decimal MiB count within the supported worker range.
    let private memorySize (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, mebibytes when
            mebibytes >= SafetyLimits.minimumWorkerMemoryMiB
            && mebibytes <= SafetyLimits.maximumWorkerMemoryMiB
            ->
            Some mebibytes
        | _ -> None

    /// Each option may appear once, in any order, after the root. Anything else is a usage error.
    let rec private options formats (selected: Map<string, string>) arguments =
        match arguments with
        | [] -> Some selected
        | "--format" :: format :: rest when List.contains format formats && not (selected.ContainsKey "format") ->
            options formats (selected.Add("format", format)) rest
        | "--jobs" :: count :: rest when (jobCount count).IsSome && not (selected.ContainsKey "jobs") ->
            options formats (selected.Add("jobs", count)) rest
        | "--memory" :: size :: rest when (memorySize size).IsSome && not (selected.ContainsKey "memory") ->
            options formats (selected.Add("memory", size)) rest
        | "--timings" :: rest when not (selected.ContainsKey "timings") ->
            options formats (selected.Add("timings", "")) rest
        | "--no-cache" :: rest when not (selected.ContainsKey "no-cache") ->
            options formats (selected.Add("no-cache", "")) rest
        | "--manifest" :: file :: rest when
            not (file.StartsWith("--", StringComparison.Ordinal))
            && not (selected.ContainsKey "manifest")
            ->
            options formats (selected.Add("manifest", file)) rest
        | "--report" :: file :: rest when
            not (file.StartsWith("--", StringComparison.Ordinal))
            && not (selected.ContainsKey "report")
            ->
            options formats (selected.Add("report", file)) rest
        | _ -> None

    let private jobs (selected: Map<string, string>) =
        selected.TryFind "jobs"
        |> Option.bind jobCount
        |> Option.defaultValue Workspace.defaultJobs

    let private memory (selected: Map<string, string>) =
        selected.TryFind "memory"
        |> Option.bind memorySize
        |> Option.defaultValue SafetyLimits.workerMemoryMiB

    let rec private verificationOptions selected (selection: RepositorySelection option) arguments =
        match arguments with
        | [] -> Some(selected, selection)
        | "--all" :: rest when selection.IsNone -> verificationOptions selected (Some All) rest
        | "--since" :: baseline :: rest when
            selection.IsNone && not (baseline.StartsWith("--", StringComparison.Ordinal))
            ->
            verificationOptions selected (Some(Since baseline)) rest
        | "--jobs" :: count :: rest when (jobCount count).IsSome && not (Map.containsKey "jobs" selected) ->
            verificationOptions (Map.add "jobs" count selected) selection rest
        | "--memory" :: size :: rest when (memorySize size).IsSome && not (Map.containsKey "memory" selected) ->
            verificationOptions (Map.add "memory" size selected) selection rest
        | "--timings" :: rest when not (Map.containsKey "timings" selected) ->
            verificationOptions (Map.add "timings" "" selected) selection rest
        | "--cache" :: rest when not (Map.containsKey "cache" selected || Map.containsKey "no-cache" selected) ->
            verificationOptions (Map.add "cache" "" selected) selection rest
        | "--no-cache" :: rest when not (Map.containsKey "cache" selected || Map.containsKey "no-cache" selected) ->
            verificationOptions (Map.add "no-cache" "" selected) selection rest
        | _ -> None

    /// `lint` and `review` use the result cache unless `--no-cache` is given.
    let private workspaceOptions (selected: Map<string, string>) : WorkspaceOptions =
        { Jobs = jobs selected
          Cache = not (selected.ContainsKey "no-cache")
          MemoryMiB = memory selected }

    /// `--manifest <file>` checks a Bazel workspace from the manifest its build wrote. The library then
    /// never uses the result cache, so `--no-cache` changes nothing.
    let private lint root (selected: Map<string, string>) =
        match selected.TryFind "manifest" with
        | Some manifest -> Workspace.checkManifestTimed (workspaceOptions selected) root manifest
        | None -> Workspace.checkTimed (workspaceOptions selected) root

    let private review root (selected: Map<string, string>) =
        match selected.TryFind "manifest" with
        | Some manifest -> Workspace.reviewManifestTimed (workspaceOptions selected) root manifest
        | None -> Workspace.reviewTimed (workspaceOptions selected) root

    /// Under a manifest the working directory is the build's execution root, and an absolute path names
    /// a sandbox that is gone when the log is read. Text output then names each diagnostic's file
    /// relative to the working directory. JSON and SARIF keep absolute paths.
    let private located (selected: Map<string, string>) format (report: WorkspaceReport) =
        if format = "text" && selected.ContainsKey "manifest" then
            { report with
                Projects =
                    report.Projects
                    |> List.map (fun project ->
                        { project with
                            Report =
                                { project.Report with
                                    Diagnostics =
                                        project.Report.Diagnostics
                                        |> List.map (fun diagnostic ->
                                            { diagnostic with
                                                File =
                                                    Path.GetRelativePath(
                                                        Environment.CurrentDirectory,
                                                        diagnostic.File
                                                    ) }) } }) }
        else
            report

    /// `--report <file>` also writes what the command prints to stdout to a file: a Bazel action needs a
    /// declared output and cannot redirect stdout. Stdout is held while the command runs, then written
    /// to the console and the file, whatever the exit code and even when the command throws.
    let private reported (file: string option) (command: unit -> int) =
        match file with
        | None -> command ()
        | Some path ->
            let console = Console.Out
            use capture = new StringWriter()
            Console.SetOut capture

            try
                command ()
            finally
                Console.SetOut console
                console.Write(capture.ToString())
                File.WriteAllText(path, capture.ToString())

    /// Repository verification reads and writes cached results only when explicitly requested.
    let private repositoryOptions (selected: Map<string, string>) : WorkspaceOptions =
        { Jobs = jobs selected
          Cache = selected.ContainsKey "cache"
          MemoryMiB = memory selected }

    /// Timings go to stderr so stdout and exit codes stay identical with or without them.
    /// `describe` gives one line per project and a prefix for the summary line.
    let private timed enabled describe analyze =
        let clock = Stopwatch.StartNew()
        let report, timings = analyze ()

        if enabled then
            let (lines: string list), (summary: string) = describe timings

            for line in lines do
                Console.Error.WriteLine("FDull timing: " + line)

            Console.Error.WriteLine(
                $"FDull timing: %s{summary}total=%d{clock.ElapsedMilliseconds}ms projects=%d{lines.Length}"
            )

        report

    let private workspaceTimings (timings: WorkspaceTiming list) =
        timings
        |> List.map (fun timing ->
            $"%s{timing.Project} export=%d{timing.ExportMilliseconds}ms worker=%d{timing.WorkerMilliseconds}ms validation=%d{timing.ValidationMilliseconds}ms"),
        ""

    let private verifyTimings (timings: VerifyTiming list, totals: VerifyTotals) =
        timings
        |> List.map (fun timing ->
            $"%s{timing.Project} evaluate=%d{timing.EvaluateMilliseconds}ms restore=%d{timing.RestoreMilliseconds}ms build=%d{timing.BuildMilliseconds}ms analyze=%d{timing.AnalyzeMilliseconds}ms rebuild=%d{timing.RebuildMilliseconds}ms reexport=%d{timing.ReexportMilliseconds}ms"),
        $"restore=%d{totals.Restore}ms build=%d{totals.Build}ms final-validation=%d{totals.FinalValidation}ms "

    let private display command format (report: WorkspaceReport) =
        let diagnostics =
            report.Projects |> List.collect (fun project -> project.Report.Diagnostics)

        let status =
            if not report.Complete then
                $"FDull %s{command}: BLOCKED — analysis incomplete; no clean result (exit %d{report.ExitCode})."
            elif report.ExitCode <> 0 then
                $"FDull %s{command}: FAIL — complete analysis found violations (exit %d{report.ExitCode})."
            elif command = "verify" then
                "FDull verify: PASS — verification completed for the included projects."
            else
                "FDull lint: PASS — complete analysis found no violations."

        let incomplete =
            $"%d{diagnostics.Length} reported diagnostics; findings are partial and do not establish a clean result. Resolve the blockers and rerun fdull %s{command}."

        if not report.Complete && format <> "text" then
            Console.Error.WriteLine status
            Console.Error.WriteLine incomplete

        match format with
        | "json" -> Console.WriteLine(Codec.encode report)
        | "sarif" ->
            let combined: SafetyReport =
                { Policy = SafetyPolicy.version
                  Tool = SafetyPolicy.fileDigest typeof<SafetyReport>.Assembly.Location
                  Complete = report.Complete
                  ExitCode = report.ExitCode
                  Sources = []
                  References = []
                  Arguments = []
                  Diagnostics = diagnostics }

            let exclusions =
                report.ExcludedProjects |> List.map (fun entry -> entry.File, entry.Reason)

            Console.WriteLine(SafetyPolicy.toSarifWithExclusions exclusions combined)
            report.Errors |> List.iter Console.Error.WriteLine
        | _ ->
            Console.WriteLine status
            report.Sequential |> List.iter Console.WriteLine

            report.ExcludedProjects
            |> List.iter (fun excluded ->
                Console.WriteLine("Excluded project (not analyzed): " + excluded.File + " — " + excluded.Reason))

            if not report.Complete then
                Console.WriteLine incomplete

            report.Errors |> List.iter Console.Error.WriteLine

            diagnostics |> List.iter (SafetyPolicy.format >> Console.WriteLine)

            if report.Cached > 0 then
                printfn
                    "%d projects (%d from cache); %d reported diagnostics."
                    report.Projects.Length
                    report.Cached
                    diagnostics.Length
            else
                printfn "%d projects; %d reported diagnostics." report.Projects.Length diagnostics.Length

        report.ExitCode

    let private displayReview format (report: WorkspaceReviewReport) =
        match format with
        | "json" -> Console.WriteLine(Codec.encode report)
        | _ -> Console.WriteLine(WorkspaceReview.format report)

        report.ExitCode

    let private displayRepository (report: RepositoryReport) =
        let plan = report.Plan

        let mode =
            match plan.Selection with
            | All -> "all"
            | Since _ -> "affected"
            | Service _ -> "explicit service"

        Console.WriteLine("FDull repository verify: " + mode + " scope at " + plan.Root)

        plan.Baseline
        |> Option.iter (fun commit -> Console.WriteLine("Comparison commit: " + commit))

        Console.WriteLine("Repository content outside policy roots is outside the verification claim.")

        for service in plan.Services do
            let status =
                if List.contains service.Id plan.Affected then
                    "affected"
                elif List.contains service.Id plan.BuildOnly then
                    "dependency (built only; not re-verified)"
                elif List.contains service.Id plan.Dependencies then
                    "dependency"
                else
                    "skipped (not verified)"

            let reasons =
                Map.tryFind service.Id plan.Reasons
                |> Option.defaultValue []
                |> String.concat "; "

            Console.WriteLine(service.Id + ": " + status + (if reasons = "" then "" else " — " + reasons))

        for service in report.Services do
            Console.WriteLine("Service: " + service.Service)
            display "verify" "text" service.Report |> ignore

        report.Errors |> List.iter Console.Error.WriteLine

        if not report.Complete then
            Console.WriteLine "FDull repository verify: BLOCKED — requested scope is incomplete (exit 3)."
        elif report.ExitCode <> 0 then
            Console.WriteLine "FDull repository verify: FAIL — requested scope found violations (exit 1)."
        elif plan.Affected.IsEmpty && plan.Dependencies.IsEmpty then
            Console.WriteLine "FDull repository verify: no affected services; unchanged services were not verified."
        else
            let projects = report.Services |> List.sumBy _.Report.Projects.Length
            let cached = report.Services |> List.sumBy _.Report.Cached

            Console.WriteLine(
                "FDull repository verify: PASS — selected services completed verification"
                + (if plan.BuildOnly.IsEmpty then
                       ""
                   else
                       "; build-only dependencies rely on the comparison commit's verification")
                + $" (%d{projects} projects, %d{cached} from cache)."
            )

        report.ExitCode

    /// `--rules` and `--format` may appear once each; every other argument is a source selector.
    let rec private checkOptions (rules: string option) (format: CheckFormat option) selectors arguments =
        match arguments with
        | [] -> Some(rules, Option.defaultValue CheckText format, List.rev selectors)
        | "--rules" :: value :: rest when rules.IsNone -> checkOptions (Some value) format selectors rest
        | "--format" :: "text" :: rest when format.IsNone -> checkOptions rules (Some CheckText) selectors rest
        | "--format" :: "json" :: rest when format.IsNone -> checkOptions rules (Some CheckJson) selectors rest
        | selector :: rest when not (selector.StartsWith("--", StringComparison.Ordinal)) ->
            checkOptions rules format (selector :: selectors) rest
        | _ -> None

    let private displayCheck format (report: CheckReport) =
        let exitCode = RuleCheck.exitCode report.Outcome

        let status, reason, findings =
            match report.Outcome with
            | Clean -> "pass", None, []
            | Violations findings ->
                "fail",
                None,
                findings
                |> List.map (fun item ->
                    { Rule = RuleCheck.ruleId item.Rule
                      File = item.File
                      StartLine = item.StartLine
                      StartColumn = item.StartColumn
                      EndLine = item.EndLine
                      EndColumn = item.EndColumn
                      Symbol = item.Symbol
                      Message = item.Message })
            | Invalid reason -> "invalid", Some reason, []
            | Incomplete(reason, blockers) ->
                "incomplete",
                Some reason,
                blockers
                |> List.map (fun item ->
                    { Rule = item.Code
                      File = item.File
                      StartLine = item.StartLine
                      StartColumn = item.StartColumn
                      EndLine = item.EndLine
                      EndColumn = item.EndColumn
                      Symbol = ""
                      Message = item.Message })

        match format with
        | CheckJson ->
            { Status = status
              ExitCode = exitCode
              Project = report.Project
              Files = report.Files
              Rules = report.Rules |> List.map RuleCheck.ruleId
              Claim = RuleCheck.claim report
              Reason = reason
              Findings = findings }
            |> Codec.encode
            |> Console.WriteLine
        | CheckText ->
            let headline =
                match report.Outcome with
                | Clean -> "FDull check: PASS — no findings for the selected rules."
                | Violations findings -> $"FDull check: FAIL — %d{findings.Length} findings for the selected rules."
                | Invalid _ -> "FDull check: INVALID — the request cannot be checked; no clean result."
                | Incomplete _ -> "FDull check: BLOCKED — analysis incomplete; no clean result."

            Console.WriteLine headline
            reason |> Option.iter (fun text -> Console.WriteLine("Reason: " + text))
            Console.WriteLine("Scope: " + RuleCheck.claim report)

            for file in report.Files do
                Console.WriteLine("Selected: " + file)

            for item in findings do
                let subject =
                    if item.Symbol = "" then
                        item.Rule
                    else
                        item.Rule + " " + item.Symbol

                Console.WriteLine(
                    $"%s{item.File}(%d{item.StartLine},%d{item.StartColumn}): %s{subject}: %s{item.Message}"
                )

        exitCode

    let private verify root selected (selection: RepositorySelection option) =
        // A standalone (version-one) verify never uses the result cache.
        if
            selection.IsNone
            && Repository.standalone root
            && (Map.containsKey "cache" selected || Map.containsKey "no-cache" selected)
        then
            usage ()
        elif selection.IsNone && Repository.standalone root then
            timed (Map.containsKey "timings" selected) verifyTimings (fun () ->
                Project.verifyTimedWith (repositoryOptions selected) root)
            |> display "verify" "text"
        else
            let full, selection = Repository.resolveSelection root selection

            timed (Map.containsKey "timings" selected) verifyTimings (fun () ->
                Repository.verifyTimedWith (repositoryOptions selected) full (Option.defaultValue All selection))
            |> displayRepository

    [<EntryPoint>]
    let main arguments =
        try
            match Array.toList arguments with
            | [ "--version" ] ->
                Console.WriteLine "0.3.0-preview.2"
                0
            | [ "defaults" ] ->
                Project.defaults () |> Console.Write
                0
            | [ "data-digest"; assembly ] ->
                DataContractPolicy.assemblyDigest assembly |> Console.WriteLine
                0
            | [ "policy-digest"; policy ] ->
                WorkspacePolicyDigest.file policy |> Console.WriteLine
                0
            | [ "init"; root ] ->
                Project.initialize root |> Console.WriteLine
                0
            | [ "init"; root; "--scope"; scope ] ->
                Project.initializeWithScope root (Some scope) |> Console.WriteLine
                0
            | "lint" :: root :: rest when not (root.StartsWith("--", StringComparison.Ordinal)) ->
                match options [ "text"; "json"; "sarif" ] Map.empty rest with
                | Some selected ->
                    let format = selected.TryFind "format" |> Option.defaultValue "text"

                    reported (selected.TryFind "report") (fun () ->
                        timed (selected.ContainsKey "timings") workspaceTimings (fun () -> lint root selected)
                        |> located selected format
                        |> display "lint" format)
                | None -> usage ()
            | "check" :: project :: rest when not (project.StartsWith("--", StringComparison.Ordinal)) ->
                match checkOptions None None [] rest with
                | Some(rules, format, selectors) ->
                    RuleCheck.run project (Option.defaultValue "" rules) selectors
                    |> displayCheck format
                | None -> usage ()
            | "verify" :: root :: rest when not (root.StartsWith("--", StringComparison.Ordinal)) ->
                // Verify reports only text.
                match verificationOptions Map.empty None rest with
                | Some(selected, selection) -> verify root selected selection
                | _ -> usage ()
            | "review" :: root :: rest when not (root.StartsWith("--", StringComparison.Ordinal)) ->
                match options [ "text"; "json" ] Map.empty rest with
                | Some selected ->
                    reported (selected.TryFind "report") (fun () ->
                        timed (selected.ContainsKey "timings") workspaceTimings (fun () -> review root selected)
                        |> displayReview (selected.TryFind "format" |> Option.defaultValue "text"))
                | None -> usage ()
            | [ "cache"; "clear"; root ] ->
                match WorkspaceCache.clear root with
                | Ok message ->
                    Console.WriteLine message
                    0
                | Error error ->
                    Console.Error.WriteLine("FDull cache: " + error)
                    3
            | [ "coverage" ] ->
                SafetyRules.inventory SafetyPolicy.version |> Codec.encode |> Console.WriteLine
                0
            | _ -> usage ()
        with error ->
            Console.Error.WriteLine "FDull: BLOCKED — command failed; no clean result (exit 3)."
            Console.Error.WriteLine("FDull failed: " + error.Message)
            3
