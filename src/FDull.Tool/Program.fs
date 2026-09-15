namespace FDull.Tool

open System
open System.IO
open FDull
open FDull.Transport

module Program =
    let private usage () =
        Console.WriteLine "FDull — F# with fewer sharp edges."

        Console.WriteLine
            "fdull defaults | init <root> [--scope <file>] | lint <root> [--format text|json|sarif] | verify <root> | coverage"

        2

    let private display format (report: WorkspaceReport) =
        match format with
        | "json" -> Console.WriteLine(Codec.encode report)
        | "sarif" ->
            let diagnostics =
                report.Projects |> List.collect (fun project -> project.Report.Diagnostics)

            let combined: SafetyReport =
                { Policy = SafetyPolicy.version
                  Tool = SafetyPolicy.fileDigest typeof<SafetyReport>.Assembly.Location
                  Complete = report.Complete
                  ExitCode = report.ExitCode
                  Sources = []
                  References = []
                  Arguments = []
                  Diagnostics = diagnostics }

            Console.WriteLine(SafetyPolicy.toSarif combined)
            report.Errors |> List.iter Console.Error.WriteLine
        | _ ->
            report.Errors |> List.iter Console.Error.WriteLine

            for project in report.Projects do
                project.Report.Diagnostics
                |> List.iter (SafetyPolicy.format >> Console.WriteLine)

            printfn "FDull: %d projects, complete=%b, exit=%d" report.Projects.Length report.Complete report.ExitCode

        report.ExitCode

    [<EntryPoint>]
    let main arguments =
        try
            match Array.toList arguments with
            | [ "--version" ] ->
                Console.WriteLine "0.1.0-preview.5"
                0
            | [ "defaults" ] ->
                Project.defaults () |> Console.Write
                0
            | [ "init"; root ] ->
                Project.initialize root |> Console.WriteLine
                0
            | [ "init"; root; "--scope"; scope ] ->
                Project.initializeWithScope root (Some scope) |> Console.WriteLine
                0
            | [ "lint"; root ] -> Workspace.check root |> display "text"
            | [ "lint"; root; "--format"; format ] when List.contains format [ "text"; "json"; "sarif" ] ->
                Workspace.check root |> display format
            | [ "verify"; root ] -> Project.verify root |> display "text"
            | [ "coverage" ] ->
                SafetyRules.inventory SafetyPolicy.version |> Codec.encode |> Console.WriteLine
                0
            | _ -> usage ()
        with error ->
            Console.Error.WriteLine("FDull failed: " + error.Message)
            3
