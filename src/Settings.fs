namespace EdLauncher

module Settings =
    open Types
    open System
    open System.IO

    let defaults =
        { Platform = Dev
          DisplayMode = Pancake
          AutoRun = false
          AutoQuit = false
          WatchForCrashes = true
          RemoteLogging = true
          ProductWhitelist = Set.empty
          ForceLocal = false
          Proton = None
          CbLauncherDir = "."
          ApiUri = Uri("http://localhost:8080") }
    
    let parseArgs defaults (findCbLaunchDir: unit -> Result<string,string>) (argv: string[]) =
        let proton, cbLaunchDir, args =
            if argv.Length > 2 && argv.[0] <> null && argv.[0].Contains("steamapps/common/Proton") then
                Some (argv.[0], argv.[1]), Path.GetDirectoryName(argv.[2]) |> Ok, argv.[2..]
            else
                None, findCbLaunchDir(), argv
        let getArg (flag:string) i =
            if i + 1 < args.Length && not (String.IsNullOrEmpty args.[i + 1]) && not (args.[i + 1].StartsWith '/') then
                flag.ToLowerInvariant(), Some args.[i + 1]
            else
                flag.ToLowerInvariant(), None
        
        cbLaunchDir
        |> Result.map (fun cbLaunchDir ->
            args
            |> Array.mapi (fun index value -> index, value)
            |> Array.filter (fun (_, arg) -> not (String.IsNullOrEmpty(arg)))
            |> Array.fold (fun s (i, arg) ->
                match getArg arg i with
                | "/steamid", Some id   -> { s with Platform = Steam id; ForceLocal = true }
                | "/oculus", Some nonce -> { s with Platform = Oculus nonce; ForceLocal = true }
                | "/noremotelogs", _    -> { s with RemoteLogging = false }
                | "/nowatchdog", _      -> { s with WatchForCrashes = false }
                | "/vr", _              -> { s with DisplayMode = Vr }
                | "/autorun", _         -> { s with AutoRun = true }
                | "/autoquit", _        -> { s with AutoQuit = true }
                | "/forcelocal", _      -> { s with ForceLocal = true }
                | "/ed", _              -> { s with ProductWhitelist = s.ProductWhitelist.Add "ed" }
                | "/edh", _             -> { s with ProductWhitelist = s.ProductWhitelist.Add "edh" }
                | "/eda", _             -> { s with ProductWhitelist = s.ProductWhitelist.Add "eda" }
                | _ -> s)
                { defaults with Proton = proton; CbLauncherDir = cbLaunchDir })
        
    let getSettings args =
        let findCbLaunchDir = fun () -> Ok "/mnt/games/Steam/Linux/steamapps/common/Elite Dangerous" // TODO: search for common paths
        let apiUri = Uri("https://api.zaonce.net") // TODO: read from config
        
        parseArgs defaults findCbLaunchDir args
        |> Result.map (fun settings -> { settings with ApiUri = apiUri })