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
                | "/vr", _              -> { s with DisplayMode = Vr; AutoRun = true }
                | "/autorun", _         -> { s with AutoRun = true }
                | "/autoquit", _        -> { s with AutoQuit = true }
                | "/forcelocal", _      -> { s with ForceLocal = true }
                | "/ed", _              -> { s with ProductWhitelist = s.ProductWhitelist.Add "ed" }
                | "/edh", _             -> { s with ProductWhitelist = s.ProductWhitelist.Add "edh" }
                | "/eda", _             -> { s with ProductWhitelist = s.ProductWhitelist.Add "eda" }
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