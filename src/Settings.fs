module Settings

open Types
open System

let defaults =
    { Platform = Dev
      ProductMode = Pancake
      AutoRun = true
      AutoQuit = true
      WatchForCrashes = true
      RemoteLogging = false
      ForceLocal = false }
    
let parseArgs log defaults (args: string[]) =
    let getArg (flag:string) i =
        if i + 1 < args.Length && not (String.IsNullOrEmpty args.[i + 1]) && not (args.[i + 1].StartsWith '/') then
            flag.ToLowerInvariant(), Some args.[i + 1]
        else
            flag.ToLowerInvariant(), None
    args
    |> Array.mapi (fun index value -> index, value)
    |> Array.filter (fun (_, arg) -> not (String.IsNullOrEmpty(arg)))
    |> Array.fold (fun s (i, arg) ->
        match getArg arg i with
        | "/steamid", Some id   -> { s with Platform = Steam id; ForceLocal = true }
        | "/oculus", Some nonce -> { s with Platform = Oculus nonce; ForceLocal = true }
        | "/noremotelogs", _    -> { s with RemoteLogging = false }
        | "/nowatchdog", _      -> { s with WatchForCrashes = false }
        | "/vr", _              -> { s with ProductMode = Vr }
        | "/autorun", _         -> { s with AutoRun = true }
        | "/autoquit", _        -> { s with AutoQuit = true }
        | "/forcelocal", _      -> { s with ForceLocal = true }
        | _ ->
            log.Warn <| sprintf "Ignoring argument '%s'" arg
            s
        ) defaults