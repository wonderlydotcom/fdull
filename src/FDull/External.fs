namespace FDull.Transport

open System.IO

/// Normalize required framework metadata at the adapter boundary. A missing
/// value indicates malformed external data; it cannot enter a domain value.
module External =
    let required context (value: 'a | null) : 'a =
        match value with
        | Null -> raise (InvalidDataException context)
        | NonNull present -> present
