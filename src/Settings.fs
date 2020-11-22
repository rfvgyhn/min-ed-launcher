namespace EdLauncher

module Settings =
    open Types
    open System
    open System.IO
    open System.Diagnostics
    open FsConfig
    open Microsoft.Extensions.Configuration

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
          ApiUri = Uri("http://localhost:8080")
          Restart = false, 0L
          Processes = List.empty }
    type private EpicArg = ExchangeCode of string | Type of string | Env of string | UserId of string | Locale of string | RefreshToken of string | TokenName of string | Log of bool
    let parseArgs defaults (findCbLaunchDir: unit -> Result<string,string>) (argv: string[]) =
        let proton, cbLaunchDir, args =
            if argv.Length > 2 && argv.[0] <> null && argv.[0].Contains("steamapps/common/Proton") then
                Some (argv.[0], argv.[1]), Path.GetDirectoryName(argv.[2]) |> Ok, argv.[2..]
            else
                None, findCbLaunchDir(), argv
        let getArg (arg:string) i =
            if i + 1 < args.Length && not (String.IsNullOrEmpty args.[i + 1]) && (not (args.[i + 1].StartsWith '/') && not (args.[i].StartsWith '-')) then // /arg argValue
                arg.ToLowerInvariant(), Some args.[i + 1]
            else if arg.Contains("=") then // -arg=argvalue
                let parts = arg.Split("=")
                parts.[0].ToLowerInvariant(), Some parts.[1]
            else
                arg.ToLowerInvariant(), None
        
        let applyEpicArg platform arg =
            let apply arg details =
                arg
                |> Option.map (fun arg ->
                    match arg with
                    | ExchangeCode p -> { details with ExchangeCode = p }
                    | Type t         -> { details with Type = t }
                    | Env e          -> { details with Env = e }
                    | UserId id      -> { details with UserId = id }
                    | Locale l       -> { details with Locale = l }
                    | RefreshToken t -> { details with RefreshToken = Some t }
                    | TokenName n    -> { details with TokenName = n }
                    | Log v      -> { details with Log = v })
                |> Option.defaultValue details
                |> Epic
                
            match platform with
            | Epic d -> apply arg d
            | Dev -> apply arg EpicDetails.Empty
            | _ -> platform
        
        cbLaunchDir
        |> Result.map (fun cbLaunchDir ->
            args
            |> Array.mapi (fun index value -> index, value)
            |> Array.filter (fun (_, arg) -> not (String.IsNullOrEmpty(arg)))
            |> Array.fold (fun s (i, arg) ->
                let epicArg arg = applyEpicArg s.Platform (Some arg)
                match getArg arg i with
                | "/steamid", _                   -> { s with Platform = Steam; ForceLocal = true }
                | "/steam", _                     -> { s with Platform = Steam; ForceLocal = true }
                | "/epic", _                      -> { s with Platform = applyEpicArg s.Platform None; ForceLocal = true }
                | "-auth_password", Some password -> { s with Platform = epicArg (ExchangeCode password) }
                | "-auth_type", Some t            -> { s with Platform = epicArg (Type t) }
                | "-epicenv", Some env            -> { s with Platform = epicArg (Env env) }
                | "-epicuserid", Some id          -> { s with Platform = epicArg (UserId id) }
                | "-epiclocale", Some locale      -> { s with Platform = epicArg (Locale locale) }
                | "/epicrefreshtoken", Some token -> { s with Platform = epicArg (RefreshToken token) }
                | "/epictokenname", Some name     -> { s with Platform = epicArg (TokenName name) }
                | "/logepicinfo", _               -> { s with Platform = epicArg (Log true) }
                | "/oculus", Some nonce           -> { s with Platform = Oculus nonce; ForceLocal = true }
                | "/vr", _                        -> { s with DisplayMode = Vr; AutoRun = true }
                | "/autorun", _                   -> { s with AutoRun = true }
                | "/autoquit", _                  -> { s with AutoQuit = true }
                | "/forcelocal", _                -> { s with ForceLocal = true }
                | "/ed", _                        -> { s with ProductWhitelist = s.ProductWhitelist.Add "ed" }
                | "/edh", _                       -> { s with ProductWhitelist = s.ProductWhitelist.Add "edh" }
                | "/eda", _                       -> { s with ProductWhitelist = s.ProductWhitelist.Add "eda" }
                | _ -> s)
                { defaults with Proton = proton; CbLauncherDir = cbLaunchDir })
    [<CLIMutable>]
    type ProcessConfig =
        { FileName: string
          Arguments: string option }
    [<CLIMutable>]
    type RestartConfig =
        { Enabled: bool
          ShutdownTimeout: int64 }
    [<CLIMutable>]
    type Config =
        { ApiUri: string
          RemoteLogging: bool
          WatchForCrashes: bool
          GameLocation: string option
          Restart: RestartConfig
          Processes: ProcessConfig list }
    let parseConfig fileName =
        let configRoot = ConfigurationBuilder()
                            .AddJsonFile(fileName, false)
                            .Build()
        match AppConfig(configRoot).Get<Config>() with
        | Ok config ->
            // FsConfig doesn't support list of records so handle it manually
            let processes =
                configRoot.GetSection("processes").GetChildren()
                |> Seq.map (fun section ->
                    let fileName = section.GetValue<string>("fileName")
                    let arguments = section.GetValue<string>("arguments")
                    if fileName = null then
                        None
                    else
                        Some { FileName = fileName; Arguments = if arguments = null then None else Some arguments })
                |> Seq.choose id
                |> Seq.toList
            { config with Processes = processes } |> ConfigParseResult.Ok
        | Error error -> Error error
        
    let getSettings args fileConfig =
        let findCbLaunchDir = fun () ->
            let home = Environment.expandEnvVars("~");
            [ sprintf "%s\Steam\steamapps\common\Elite Dangerous" (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
              sprintf "%s/.steam/steam/steamapps/common/Elite Dangerous" home
              sprintf "%s/.local/share/Steam/steamapps/common/Elite Dangerous" home ]
            |> List.map (fun path -> Some path)
            |> List.append [ fileConfig.GameLocation ]
            |> List.choose id
            |> List.tryFind (fun dir -> Directory.Exists dir)
            |> function
                | None -> Error "Failed to find Elite Dangerous install directory"
                | Some dir -> Ok dir
        let apiUri = Uri(fileConfig.ApiUri)
        let restart = fileConfig.Restart.Enabled, fileConfig.Restart.ShutdownTimeout * 1000L
        let processes =
            fileConfig.Processes
            |> List.map (fun p ->
                let pInfo = ProcessStartInfo()
                pInfo.FileName <- p.FileName
                pInfo.WorkingDirectory <- Path.GetDirectoryName(p.FileName)
                pInfo.Arguments <- p.Arguments |> Option.defaultValue ""
                pInfo.UseShellExecute <- false
                pInfo.RedirectStandardOutput <- true
                pInfo.RedirectStandardError <- true
                pInfo.WindowStyle <- ProcessWindowStyle.Minimized
                pInfo)
        
        parseArgs defaults findCbLaunchDir args
        |> Result.map (fun settings -> { settings with ApiUri = apiUri
                                                       Processes = processes
                                                       Restart = restart
                                                       RemoteLogging = fileConfig.RemoteLogging
                                                       WatchForCrashes = fileConfig.WatchForCrashes })