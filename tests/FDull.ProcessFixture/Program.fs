namespace FDull.ProcessFixture

open System
open System.IO
open System.Threading

/// Deliberate process failures, invoked only by the bounded supervisor tests.
module Program =
    let private exercise mode =
        match mode with
        | "timeout" ->
            Thread.Sleep 30000
            0
        | "stdout" ->
            while true do
                Console.Out.Write(String.replicate 4096 "x")

            0
        | "stderr" ->
            while true do
                Console.Error.Write(String.replicate 4096 "x")

            0
        | "exit" ->
            Console.Error.Write "deliberate worker failure"
            17
        | "heap-limit" ->
            let memory = GC.GetGCMemoryInfo()
            Console.Out.Write memory.TotalAvailableMemoryBytes
            0
        | "oom" ->
            try
                let exceedsHeapLimit = Array.zeroCreate<byte> (768 * 1024 * 1024)
                GC.KeepAlive exceedsHeapLimit
                0
            with :? OutOfMemoryException ->
                Console.Error.Write "OutOfMemoryException"
                19
        | _ -> 2

    [<EntryPoint>]
    let main arguments =
        match Array.toList arguments with
        | [ mode; marker ] ->
            File.WriteAllText(marker, string Environment.ProcessId)
            exercise mode
        | _ -> 2
