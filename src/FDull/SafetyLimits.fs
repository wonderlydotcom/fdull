namespace FDull

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading.Tasks

/// Supported checker resource limits, not application style/complexity rules.
module SafetyLimits =
    let analysisMilliseconds = 20000
    let workerMilliseconds = 30000
    let managedHeapBytes = 536870912L
    let workingSetBytes = 1073741824L
    let messageCharacters = 4 * 1024 * 1024
    let sourceBytes = 1024L * 1024L
    let sourceCount = 256
    let referenceCount = 512
    let traversalSteps = 250000
    let traversalDepth = 256
    let typeDepth = 64
    let diagnosticCount = 512

type internal SafetyBudget(?maximumSteps: int, ?maximumDepth: int, ?milliseconds: int) =
    let clock = Stopwatch.StartNew()
    let mutable steps = 0
    let maximumSteps = defaultArg maximumSteps SafetyLimits.traversalSteps
    let maximumDepth = defaultArg maximumDepth SafetyLimits.traversalDepth
    let milliseconds = defaultArg milliseconds SafetyLimits.analysisMilliseconds

    member _.Visit(depth: int) =
        steps <- steps + 1

        if depth > maximumDepth then
            invalidOp "Analysis traversal exceeded the supported depth."

        if steps > maximumSteps then
            invalidOp "Analysis traversal exceeded the supported work budget."

        if clock.ElapsedMilliseconds >= int64 milliseconds then
            invalidOp "Analysis exceeded its time budget."

    member _.RemainingMilliseconds = max 1 (milliseconds - int clock.ElapsedMilliseconds)

type SafetyProcessLimits =
    { Milliseconds: int
      OutputCharacters: int
      WorkingSetBytes: int64 }

type SafetyProcessOutput =
    { ExitCode: int
      Output: string
      Error: string }

/// Tooling-only process runner. The authoritative caller fixes the executable,
/// environment and limits; no application-supplied process is ever evaluated.
module SafetyProcess =
    let readBounded limit (reader: TextReader) =
        task {
            let buffer = Array.zeroCreate<char> 4096
            let output = StringBuilder()
            let finished = ref false

            while not finished.Value do
                let! count = reader.ReadAsync(buffer, 0, buffer.Length)

                if count = 0 then
                    finished.Value <- true
                elif output.Length + count > limit then
                    invalidOp "Worker output exceeded the supported message size."
                else
                    output.Append(buffer, 0, count) |> ignore

            return output.ToString()
        }

    let run limits (start: ProcessStartInfo) (input: string) =
        if
            limits.Milliseconds <= 0
            || limits.OutputCharacters <= 0
            || limits.WorkingSetBytes <= 0L
        then
            invalidArg "limits" "Process limits must be positive."

        start.UseShellExecute <- false
        start.RedirectStandardInput <- true
        start.RedirectStandardOutput <- true
        start.RedirectStandardError <- true
        use child = new Process(StartInfo = start)
        let started = ref false

        let stop () =
            if started.Value && not child.HasExited then
                try
                    child.Kill(true)
                with :? InvalidOperationException ->
                    ()

                if not (child.WaitForExit 5000) then
                    invalidOp "The safety worker could not be terminated."

        try
            try
                started.Value <- child.Start()

                if not started.Value then
                    Error "The safety worker did not start."
                else
                    let clock = Stopwatch.StartNew()
                    let output = readBounded limits.OutputCharacters child.StandardOutput
                    let errors = readBounded limits.OutputCharacters child.StandardError

                    let writing =
                        async {
                            do! child.StandardInput.WriteAsync input |> Async.AwaitTask
                            child.StandardInput.Close()
                        }
                        |> Async.StartAsTask

                    let mutable failure = None

                    while not child.HasExited && failure.IsNone do
                        if clock.ElapsedMilliseconds >= int64 limits.Milliseconds then
                            failure <- Some "The safety worker exceeded its time budget."
                        elif output.IsFaulted || errors.IsFaulted then
                            failure <- Some "The safety worker exceeded its output budget."
                        else
                            child.Refresh()

                            if not child.HasExited && child.WorkingSet64 > limits.WorkingSetBytes then
                                failure <- Some "The safety worker exceeded its working-set budget."
                            else
                                child.WaitForExit(25) |> ignore

                    match failure with
                    | Some reason ->
                        stop ()
                        Error reason
                    | None ->
                        let readers = Task.WhenAll [| output; errors |]

                        if not (readers.Wait 5000) then
                            Error "The safety worker did not close its output streams."
                        elif writing.IsFaulted && child.ExitCode = 0 then
                            Error "The safety worker did not consume its request."
                        else
                            Ok
                                { ExitCode = child.ExitCode
                                  Output = output.Result
                                  Error = errors.Result }
            with error ->
                Error("The safety worker failed: " + error.Message)
        finally
            stop ()
