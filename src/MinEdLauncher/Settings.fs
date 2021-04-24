module MinEdLauncher.Settings

open Types
open System
open System.IO
open System.Diagnostics
open FsConfig
open FSharp.Control.Tasks.NonAffine
open Microsoft.Extensions.Configuration

let defaults =
    { Platform = Dev
      DisplayMode = Pancake
      AutoRun = false
      AutoQuit = false
      WatchForCrashes = true
      ProductWhitelist = Set.empty
      ForceLocal = false
      Proton = None
      CbLauncherDir = "."
      PreferredLanguage = None
      ApiUri = Uri("http://localhost:8080")
      Restart = false, 0L
      AutoUpdate = true
      Processes = List.empty }
    
[<RequireQualifiedAccess>]
type FrontierCredResult = Found of string * string * string option | NotFound of string | UnexpectedFormat of string | Error of string
let readFrontierAuth (profileName: string) = task {
    let path = Path.Combine(Environment.configDir, $".frontier-%s{profileName.ToLower()}.cred")
    if File.Exists(path) then
        let! lines = FileIO.readAllLines(path)
        return match lines with
               | Ok lines ->
                    if lines.Length = 2 then
                        (lines.[0], lines.[1], None) |> FrontierCredResult.Found
                    else if lines.Length = 3 then
                        (lines.[0], lines.[1], Some lines.[2]) |> FrontierCredResult.Found
                    else
                       FrontierCredResult.UnexpectedFormat path
               | Error msg -> FrontierCredResult.Error msg
    else
        return FrontierCredResult.NotFound path
}

let frontierCredPath (profileName: string) = Path.Combine(Environment.configDir, $".frontier-%s{profileName.ToLower()}.cred")

let applyDeviceAuth settings  =
    match settings.Platform with
    | Frontier details -> task {
        let path = frontierCredPath details.Profile
        match! Cobra.readCredentials path with
        | Cobra.CredResult.Found (user, pass, token) ->
            return Ok { settings with Platform = Frontier { details with Credentials = Some { Username = user; Password = pass }; AuthToken = token } }
        | Cobra.CredResult.NotFound _ ->
            return Ok settings
        | Cobra.CredResult.UnexpectedFormat path -> return Error $"Unable to parse credentials at '%s{path}'. Unexpected format"
        | Cobra.CredResult.Failure msg -> return Error msg }
    | _ -> Ok settings |> Task.fromResult
    
type private EpicArg = ExchangeCode of string | Type of string | AppId of string
let parseArgs defaults (findCbLaunchDir: Platform -> Result<string,string>) (argv: string[]) =
    let proton, cbLaunchDir, args =
        let isOldProton() = // Proton < 5.13 doesn't run via steam linux runtime
            argv.Length > 2 && argv.[0] <> null
            && [ Path.Combine("steamapps", "common", "Proton"); Path.Combine("Steam", "compatibilitytools.d", "Proton") ]
               |> List.exists (fun p -> argv.[0].Contains(p))
        let isNewProton() = // Proton >= 5.13 runs via steam linux runtime
            argv.Length > 2 && argv.[0] <> null
            && argv.[0].Contains("SteamLinuxRuntime")
        if isNewProton() then
            let doesntEndWith (value: string) (str: string) = not (str.EndsWith(value))
            let runtimeArgs = argv.[1..] |> Array.takeWhile (doesntEndWith "proton")
            let protonArgs = argv.[1..] |> Array.skipWhile (doesntEndWith "proton") |> Array.takeWhile (doesntEndWith "EDLaunch.exe")
            let args = seq { runtimeArgs; [| "python3" |]; protonArgs } |> Array.concat
            let launcherIndex = args.Length
            Some { EntryPoint = argv.[0]; Args = args }, Path.GetDirectoryName(argv.[launcherIndex]) |> Some, argv.[launcherIndex..]
        else if isOldProton() then 
            Some { EntryPoint = "python3"; Args = argv.[..1] }, Path.GetDirectoryName(argv.[2]) |> Some, argv.[2..]
        else if argv.Length > 0 && argv.[0] <> null && argv.[0].EndsWith("EDLaunch.exe", StringComparison.OrdinalIgnoreCase) then
            None, Some (Path.GetDirectoryName(argv.[0])), argv.[1..]
        else
            None, None, argv
            
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
                | AppId id       -> { details with AppId = id })
            |> Option.defaultValue details
            |> Epic
            
        match platform with
        | Epic d -> apply arg d
        | _ -> apply arg EpicDetails.Empty
    
    let settings =
        args
        |> Array.mapi (fun index value -> index, value)
        |> Array.filter (fun (_, arg) -> not (String.IsNullOrEmpty(arg)))
        |> Array.fold (fun s (i, arg) ->
            let epicArg arg = applyEpicArg s.Platform (Some arg)
            match getArg arg i with
            | "/steamid", _                   -> { s with Platform = Steam; ForceLocal = true }
            | "/steam", _                     -> { s with Platform = Steam; ForceLocal = true }
            | "/epic", _                      -> { s with Platform = applyEpicArg s.Platform None; ForceLocal = true }
            | "/frontier", Some profileName   -> { s with Platform = Frontier { Profile = profileName; Credentials = None; AuthToken = None } }
            | "-auth_password", Some password -> { s with Platform = epicArg (ExchangeCode password) }
            | "-auth_type", Some t            -> { s with Platform = epicArg (Type t) }
            | "-epicapp", Some id             -> { s with Platform = epicArg (AppId id) }
            | "/oculus", Some nonce           -> { s with Platform = Oculus nonce; ForceLocal = true }
            | "/vr", _                        -> { s with DisplayMode = Vr; AutoRun = true }
            | "/autorun", _                   -> { s with AutoRun = true }
            | "/autoquit", _                  -> { s with AutoQuit = true }
            | "/forcelocal", _                -> { s with ForceLocal = true }
            | "/ed", _                        -> { s with ProductWhitelist = s.ProductWhitelist.Add "ed" }
            | "/edh", _                       -> { s with ProductWhitelist = s.ProductWhitelist.Add "edh" }
            | "/eda", _                       -> { s with ProductWhitelist = s.ProductWhitelist.Add "eda" }
            | _ -> s) defaults
    
    match cbLaunchDir
          |> Option.map Ok |> Option.defaultWith (fun () -> findCbLaunchDir settings.Platform)
          |> Result.map (fun launchDir -> { settings with Proton = proton; CbLauncherDir = launchDir }) with
    | Ok settings ->
        applyDeviceAuth settings
    | Result.Error msg -> Result.Error msg |> Task.fromResult
    
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
      WatchForCrashes: bool
      GameLocation: string option
      Language: string option
      Restart: RestartConfig
      AutoUpdate: bool option
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
    
let getSettings args appDir fileConfig = task {
    let findCbLaunchDir paths =
        appDir :: paths
        |> List.map Some
        |> List.append [ fileConfig.GameLocation ]
        |> List.choose id
        |> List.tryFind (fun path -> File.Exists(Path.Combine(path, "EDLaunch.exe")))
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
    
    let fallbackDirs platform =
        match platform with
        | Epic d -> Epic.potentialInstallPaths d.AppId |> findCbLaunchDir
        | Steam -> Steam.potentialInstallPaths() |> findCbLaunchDir
        | Frontier _-> Cobra.potentialInstallPaths() @ Steam.potentialInstallPaths() |> findCbLaunchDir
        | _ -> Error "Unknown platform. Failed to find Elite Dangerous install directory"
    
    let! settings = parseArgs defaults fallbackDirs args
    return settings
           |> Result.map (fun settings -> { settings with ApiUri = apiUri
                                                          AutoUpdate = fileConfig.AutoUpdate |> Option.defaultValue true
                                                          PreferredLanguage = fileConfig.Language
                                                          Processes = processes
                                                          Restart = restart
                                                          WatchForCrashes = fileConfig.WatchForCrashes }) }