namespace FDull

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open FDull.Transport

/// Everything a worker result depends on. The request's whole-file policy pin is blanked: the
/// worker can only observe the policy slice, whose digest the request still carries.
type WorkspaceCacheKey =
    { Version: int
      Advisory: bool
      Worker: string
      Runtime: string
      Request: WorkspaceRequest }

/// One stored worker response with the key it answers.
type WorkspaceCacheEntry =
    { Key: WorkspaceCacheKey
      ExitCode: int
      Output: string
      Error: string }

/// Per-project result cache for `lint` and `review`. Never used by `verify`. Every hit is still
/// checked by the parent exactly as a fresh worker response is. Entries are trusted local state,
/// like build output: an entry edited to stay consistent is believed, so certification is `verify`'s.
module WorkspaceCache =
    let private maximumBytes = 64L * 1024L * 1024L

    let private directory root = Path.Combine(root, ".fdull", "cache")

    let private linked (path: string) =
        (FileInfo path).LinkTarget |> isNull |> not

    let key advisory worker (request: WorkspaceRequest) =
        { Version = 1
          Advisory = advisory
          Worker = worker
          Runtime = RuntimeEnvironment.GetRuntimeDirectory() + " " + string Environment.Version
          Request =
            { request with
                Policy = { request.Policy with Digest = "" } } }

    let private file root key =
        let name =
            key
            |> Codec.encode
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexStringLower

        Path.Combine(directory root, name + ".json")

    /// The stored worker response for exactly this key, or None for any missing, unreadable,
    /// oversized or mismatched entry.
    let read root key : SafetyProcessOutput option =
        try
            let path = file root key

            if not (File.Exists path) || FileInfo(path).Length > maximumBytes then
                None
            else
                let entry = File.ReadAllText path |> Codec.decode<WorkspaceCacheEntry>

                if entry.Key = key then
                    Some
                        { ExitCode = entry.ExitCode
                          Output = entry.Output
                          Error = entry.Error }
                else
                    None
        with _ ->
            None

    /// Store an accepted worker response. Best effort: a failed write only costs a later miss.
    let write root key (output: SafetyProcessOutput) =
        try
            let parent = Path.Combine(root, ".fdull")
            let cache = directory root

            if not (linked parent) && not (linked cache) then
                Directory.CreateDirectory cache |> ignore

                if not (linked parent) && not (linked cache) then
                    let text =
                        Codec.encode
                            { Key = key
                              ExitCode = output.ExitCode
                              Output = output.Output
                              Error = output.Error }

                    if int64 (Encoding.UTF8.GetByteCount text) <= maximumBytes then
                        let path = file root key
                        let temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp"

                        try
                            File.WriteAllText(temporary, text)
                            File.Move(temporary, path, true)
                        finally
                            if File.Exists temporary then
                                File.Delete temporary
        with _ ->
            ()

    /// Delete `<root>/.fdull/cache`. Refuses symbolic links so it never deletes outside the root.
    let clear platform =
        let root = Path.GetFullPath platform
        let parent = Path.Combine(root, ".fdull")
        let cache = directory root

        if linked parent || linked cache then
            Error "The cache directory is a symbolic link; nothing was deleted."
        elif Directory.Exists cache then
            Directory.Delete(cache, true)
            Ok("FDull cache: cleared " + cache)
        else
            Ok("FDull cache: nothing to clear at " + cache)
