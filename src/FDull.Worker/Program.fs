namespace FDull.Worker

open System
open FDull

module Program =
    [<EntryPoint>]
    let main arguments =
        match Array.toList arguments with
        | [] -> Safety.serve Console.In Console.Out
        | [ "--workspace" ] -> Workspace.serve Console.In Console.Out
        | _ -> 2
