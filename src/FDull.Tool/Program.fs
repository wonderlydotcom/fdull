namespace FDull.Tool

open System
open System.IO
open FDull
open FDull.Transport

module Program =
    let private usage () =
        Console.WriteLine "FDull — F# with fewer sharp edges."

        Console.WriteLine
            "fdull defaults | init <root> [--scope <file>] | lint <root> [--format text|json|sarif] | review <root> [--format text|json] | data-digest <assembly> | verify <root> | coverage"

        2

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

            report.ExcludedProjects
            |> List.iter (fun excluded ->
                Console.WriteLine("Excluded project (not analyzed): " + excluded.File + " — " + excluded.Reason))

            if not report.Complete then
                Console.WriteLine incomplete

            report.Errors |> List.iter Console.Error.WriteLine

            diagnostics |> List.iter (SafetyPolicy.format >> Console.WriteLine)

            printfn "%d projects; %d reported diagnostics." report.Projects.Length diagnostics.Length

        report.ExitCode

    let private displayReview format (report: WorkspaceReviewReport) =
        match format with
        | "json" -> Console.WriteLine(Codec.encode report)
        | _ -> Console.WriteLine(WorkspaceReview.format report)

        report.ExitCode

    [<EntryPoint>]
    let main arguments =
        try
            match Array.toList arguments with
            | [ "--version" ] ->
                Console.WriteLine "0.2.0-preview.1"
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
            | [ "lint"; root ] -> Workspace.check root |> display "lint" "text"
            | [ "lint"; root; "--format"; format ] when List.contains format [ "text"; "json"; "sarif" ] ->
                Workspace.check root |> display "lint" format
            | [ "verify"; root ] -> Project.verify root |> display "verify" "text"
            | [ "review"; root ] -> Workspace.review root |> displayReview "text"
            | [ "review"; root; "--format"; format ] when List.contains format [ "text"; "json" ] ->
                Workspace.review root |> displayReview format
            | [ "coverage" ] ->
                SafetyRules.inventory SafetyPolicy.version |> Codec.encode |> Console.WriteLine
                0
            | _ -> usage ()
        with error ->
            Console.Error.WriteLine "FDull: BLOCKED — command failed; no clean result (exit 3)."
            Console.Error.WriteLine("FDull failed: " + error.Message)
            3
