namespace FDull.Tool

open System
open System.Diagnostics
open System.Globalization
open System.IO
open FDull
open FDull.Transport

module Program =
    let private usage () =
        Console.WriteLine "FDull — F# with fewer sharp edges."

        Console.WriteLine
            "fdull defaults | init <root> [--scope <file>] | lint <root> [--format text|json|sarif] [--jobs <1-64>] [--no-cache] [--timings] | review <root> [--format text|json] [--jobs <1-64>] [--no-cache] [--timings] | cache clear <root> | data-digest <assembly> | verify <root> [--all | --since <commit>] [--jobs <1-64>] [--timings] | coverage"

        2

    /// Concurrent projects: a decimal count from 1 to 64.
    let private jobCount (value: string) =
        match Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, count when count >= 1 && count <= 64 -> Some count
        | _ -> None

    /// Each option may appear once, in any order, after the root. Anything else is a usage error.
    let rec private options formats (selected: Map<string, string>) arguments =
        match arguments with
        | [] -> Some selected
        | "--format" :: format :: rest when List.contains format formats && not (selected.ContainsKey "format") ->
            options formats (selected.Add("format", format)) rest
        | "--jobs" :: count :: rest when (jobCount count).IsSome && not (selected.ContainsKey "jobs") ->
            options formats (selected.Add("jobs", count)) rest
        | "--timings" :: rest when not (selected.ContainsKey "timings") ->
            options formats (selected.Add("timings", "")) rest
        | "--no-cache" :: rest when not (selected.ContainsKey "no-cache") ->
            options formats (selected.Add("no-cache", "")) rest
        | _ -> None

    let private jobs (selected: Map<string, string>) =
        selected.TryFind "jobs"
        |> Option.bind jobCount
        |> Option.defaultValue Workspace.defaultJobs

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
        | "--timings" :: rest when not (Map.containsKey "timings" selected) ->
            verificationOptions (Map.add "timings" "" selected) selection rest
        | _ -> None

    /// `lint` and `review` use the result cache unless `--no-cache` is given; `verify` never does.
    let private workspaceOptions (selected: Map<string, string>) : WorkspaceOptions =
        { Jobs = jobs selected
          Cache = not (selected.ContainsKey "no-cache") }

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

    let private verifyTimings (timings: VerifyTiming list, finalValidation: int64) =
        timings
        |> List.map (fun timing ->
            $"%s{timing.Project} evaluate=%d{timing.EvaluateMilliseconds}ms restore=%d{timing.RestoreMilliseconds}ms build=%d{timing.BuildMilliseconds}ms analyze=%d{timing.AnalyzeMilliseconds}ms rebuild=%d{timing.RebuildMilliseconds}ms reexport=%d{timing.ReexportMilliseconds}ms"),
        $"final-validation=%d{finalValidation}ms "

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
            Console.WriteLine "FDull repository verify: PASS — selected services completed verification."

        report.ExitCode

    let private verify root selected (selection: RepositorySelection option) =
        if selection.IsNone && Repository.standalone root then
            timed (Map.containsKey "timings" selected) verifyTimings (fun () ->
                Project.verifyTimed (jobs selected) root)
            |> display "verify" "text"
        else
            let full, selection = Repository.resolveSelection root selection

            timed (Map.containsKey "timings" selected) verifyTimings (fun () ->
                Repository.verifyTimed (jobs selected) full (Option.defaultValue All selection))
            |> displayRepository

    [<EntryPoint>]
    let main arguments =
        try
            match Array.toList arguments with
            | [ "--version" ] ->
                Console.WriteLine "0.2.0-preview.4"
                0
            | [ "defaults" ] ->
                Project.defaults () |> Console.Write
                0
            | [ "data-digest"; assembly ] ->
                DataContractPolicy.assemblyDigest assembly |> Console.WriteLine
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
                    timed (selected.ContainsKey "timings") workspaceTimings (fun () ->
                        Workspace.checkTimed (workspaceOptions selected) root)
                    |> display "lint" (selected.TryFind "format" |> Option.defaultValue "text")
                | None -> usage ()
            | "verify" :: root :: rest when not (root.StartsWith("--", StringComparison.Ordinal)) ->
                // Verify reports only text and never uses the result cache.
                match verificationOptions Map.empty None rest with
                | Some(selected, selection) -> verify root selected selection
                | _ -> usage ()
            | "review" :: root :: rest when not (root.StartsWith("--", StringComparison.Ordinal)) ->
                match options [ "text"; "json" ] Map.empty rest with
                | Some selected ->
                    timed (selected.ContainsKey "timings") workspaceTimings (fun () ->
                        Workspace.reviewTimed (workspaceOptions selected) root)
                    |> displayReview (selected.TryFind "format" |> Option.defaultValue "text")
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
