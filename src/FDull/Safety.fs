namespace FDull

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open FDull.Transport

type SafetyRequest =
    { Version: int
      Directory: string
      Output: string
      Sources: SafetyInput list
      References: SafetyInput list }

type SafetyResponse =
    { Version: int
      Request: string
      Report: SafetyReport }

/// Parent-side safety authority. The worker analyzes source but never evaluates it.
module Safety =
    let private tool () =
        SafetyPolicy.fileDigest typeof<SafetyReport>.Assembly.Location

    let private failure directory arguments sources references code rule message =
        { Policy = SafetyPolicy.version
          Tool = tool ()
          Complete = false
          ExitCode = code
          Sources = sources
          References = references
          Arguments = arguments
          Diagnostics =
            [ { Rule = rule
                Severity = "error"
                File = directory
                StartLine = 1
                StartColumn = 1
                EndLine = 1
                EndColumn = 1
                Project = "fdull.source"
                Profile = "PURE"
                Symbol = ""
                Message = message
                Alternative = "Run the complete pinned platform toolchain with supported inputs."
                Policy = SafetyPolicy.version } ] }

    let private inputsMatch inputs =
        inputs
        |> List.forall (fun input -> File.Exists input.Path && SafetyPolicy.fileDigest input.Path = input.Digest)

    let verifyInputs (report: SafetyReport) =
        try
            report.Complete
            && report.ExitCode = 0
            && not report.Sources.IsEmpty
            && not report.References.IsEmpty
            && inputsMatch (report.Sources @ report.References)
        with _ ->
            false

    let private requestDigest text =
        Encoding.UTF8.GetBytes(text: string) |> SafetyPolicy.digest

    /// Validate the response against the parent's actual request. A parseable report
    /// alone never establishes successful analysis or matching compilation inputs.
    let validateResponse (request: SafetyRequest) requestText engineTool (output: SafetyProcessOutput) =
        let arguments =
            SafetyPolicy.arguments (request.References |> List.map _.Path) request.Directory request.Output

        let failed code rule message =
            failure request.Directory arguments request.Sources request.References code rule message

        try
            let response = Codec.decode<SafetyResponse> output.Output
            let report = response.Report

            if
                request.Version <> 1
                || requestText <> Codec.encode request
                || request.Sources.IsEmpty
                || request.References.IsEmpty
                || request.Sources.Length > SafetyLimits.sourceCount
                || request.References.Length > SafetyLimits.referenceCount
                || (request.Sources |> List.map _.Path |> Set.ofList |> Set.count)
                   <> request.Sources.Length
                || (request.References |> List.map _.Path |> Set.ofList |> Set.count)
                   <> request.References.Length
            then
                failed 3 "BUILD003" "The parent request has an invalid input inventory or protocol."
            elif
                response.Version <> 1
                || response.Request <> requestDigest requestText
                || report.Policy <> SafetyPolicy.version
                || report.Tool <> engineTool
                || report.Sources <> request.Sources
                || report.References <> request.References
                || report.Arguments <> arguments
            then
                failed 4 "BUILD004" "The worker report does not match the requested inputs, tool or policy."
            elif
                report.ExitCode <> output.ExitCode
                || report.ExitCode < 0
                || report.ExitCode > 4
                || (report.ExitCode = 0 && (not report.Complete || not report.Diagnostics.IsEmpty))
                || (report.ExitCode = 1 && (not report.Complete || report.Diagnostics.IsEmpty))
                || (report.ExitCode >= 2 && (report.Complete || report.Diagnostics.IsEmpty))
                || (report.ExitCode = 0 && not (String.IsNullOrEmpty output.Error))
                || report.Diagnostics.Length > SafetyLimits.diagnosticCount + 1
                || (report.Diagnostics
                    |> List.exists (fun d ->
                        d.Severity <> "error"
                        || d.Policy <> SafetyPolicy.version
                        || d.Project <> "fdull.source"
                        || d.Profile <> "PURE"
                        || not (SafetyRules.knownDiagnostic d.Rule)
                        || String.IsNullOrWhiteSpace d.Message
                        || d.StartLine < 1
                        || d.StartColumn < 1
                        || (d.EndLine, d.EndColumn) < (d.StartLine, d.StartColumn)
                        || (d.Rule = "BUILD003" && report.Complete)))
            then
                failed 3 "BUILD003" "The worker reported inconsistent completion or exit status."
            elif not (inputsMatch (request.Sources @ request.References)) then
                failed 4 "BUILD004" "A requested input changed while the worker was running."
            else
                report
        with _ ->
            failed
                3
                "BUILD003"
                $"The safety worker exited with code %d{output.ExitCode} without a valid complete report."

    let private workerDirectory () =
        Path.Combine(AppContext.BaseDirectory, "safety-worker")

    let private workerIdentity directory =
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        |> Array.filter (fun file -> not (file.EndsWith(".pdb", StringComparison.Ordinal)))
        |> Array.sortBy (fun file -> Path.GetRelativePath(directory, file))
        |> Array.map (fun file ->
            { Path = Path.GetRelativePath(directory, file).Replace('\\', '/')
              Digest = SafetyPolicy.fileDigest file })
        |> Codec.encode
        |> requestDigest

    let check references directory output files =
        let arguments = SafetyPolicy.arguments references directory output

        let failed code rule message =
            failure directory arguments [] [] code rule message

        try
            if
                List.isEmpty files
                || files.Length > SafetyLimits.sourceCount
                || List.isEmpty references
                || references.Length > SafetyLimits.referenceCount
                || (Set.ofList files).Count <> files.Length
                || (Set.ofList references).Count <> references.Length
            then
                failed 3 "BUILD003" "Analysis requires a bounded, unique and complete input inventory."
            elif (files @ references |> List.exists (File.Exists >> not)) then
                failed 3 "BUILD003" "A source or reference is missing."
            elif
                files
                |> List.exists (fun file -> FileInfo(file).Length > SafetyLimits.sourceBytes)
            then
                failed 3 "BUILD003" "A source exceeds the supported analysis size."
            else
                let snapshot paths =
                    paths
                    |> List.map (fun path ->
                        { Path = path
                          Digest = SafetyPolicy.fileDigest path })

                let sources = snapshot files
                let referenceInputs = snapshot references

                let failed code rule message =
                    failure directory arguments sources referenceInputs code rule message

                let request =
                    { Version = 1
                      Directory = directory
                      Output = output
                      Sources = sources
                      References = referenceInputs }

                let input = Codec.encode request
                let worker = workerDirectory ()
                let executable = Path.Combine(worker, "fdull-worker.dll")
                let engineTool = tool ()

                if
                    not (File.Exists executable)
                    || not (File.Exists(Path.Combine(worker, "fdull-worker.runtimeconfig.json")))
                then
                    failed
                        3
                        "BUILD003"
                        "The pinned safety worker is missing. Build or reinstall the complete platform CLI."
                elif SafetyPolicy.fileDigest (Path.Combine(worker, "FDull.dll")) <> engineTool then
                    failed 2 "BUILD002" "The safety worker engine differs from the invoking platform compiler."
                elif input.Length > SafetyLimits.messageCharacters then
                    failed 3 "BUILD003" "The safety request exceeds the supported message size."
                else
                    let identity = workerIdentity worker

                    let runtimeRoot =
                        let rec parent count (directory: DirectoryInfo) =
                            if count = 0 then
                                directory.FullName
                            else
                                directory.Parent
                                |> FDull.Transport.External.required "toolchain.runtime-parent"
                                |> parent (count - 1)

                        parent 3 (DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()))

                    let start =
                        ProcessStartInfo(Path.Combine(runtimeRoot, "dotnet"), WorkingDirectory = worker)

                    start.ArgumentList.Add executable
                    start.Environment.Clear()
                    start.Environment["DOTNET_ROOT"] <- runtimeRoot
                    start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] <- "1"
                    start.Environment["DOTNET_GCHeapHardLimit"] <- SafetyLimits.managedHeapBytes.ToString("X")

                    let limits =
                        { Milliseconds = SafetyLimits.workerMilliseconds
                          OutputCharacters = SafetyLimits.messageCharacters
                          WorkingSetBytes = SafetyLimits.workingSetBytes }

                    match SafetyProcess.run limits start input with
                    | Error message -> failed 3 "BUILD003" message
                    | Ok response ->
                        let report = validateResponse request input engineTool response

                        if workerIdentity worker <> identity then
                            failed 4 "BUILD004" "The safety worker changed during analysis."
                        else
                            { report with Tool = identity }
        with error ->
            failed 3 "BUILD003" ("Safety analysis failed: " + error.Message)

    /// Stdio protocol used only by the platform-owned worker executable.
    let serve (input: TextReader) (output: TextWriter) =
        try
            let text =
                SafetyProcess.readBounded SafetyLimits.messageCharacters input
                |> _.GetAwaiter().GetResult()

            let request = Codec.decode<SafetyRequest> text

            if request.Version <> 1 then
                invalidOp "Unsupported safety worker protocol."

            if
                request.Sources.IsEmpty
                || request.Sources.Length > SafetyLimits.sourceCount
                || request.References.IsEmpty
                || request.References.Length > SafetyLimits.referenceCount
            then
                invalidOp "Invalid safety worker input inventory."

            let report =
                if not (inputsMatch (request.Sources @ request.References)) then
                    let arguments =
                        SafetyPolicy.arguments (request.References |> List.map _.Path) request.Directory request.Output

                    failure
                        request.Directory
                        arguments
                        request.Sources
                        request.References
                        4
                        "BUILD004"
                        "The worker received changed inputs."
                else
                    SafetyEngine.check
                        (request.References |> List.map _.Path)
                        request.Directory
                        request.Output
                        (request.Sources |> List.map _.Path)

            let response =
                { Version = 1
                  Request = requestDigest text
                  Report = report }

            let encoded = Codec.encode response

            if encoded.Length > SafetyLimits.messageCharacters then
                invalidOp "Safety report exceeded its output budget."

            output.Write encoded
            output.Flush()
            report.ExitCode
        with error ->
            Console.Error.WriteLine("BUILD003: " + error.Message)
            3
