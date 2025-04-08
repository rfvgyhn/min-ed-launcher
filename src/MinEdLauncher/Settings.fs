module MinEdLauncher.Settings

open FsToolkit.ErrorHandling
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
      QuitMode = WaitForInput
      WatchForCrashes = true
      ProductWhitelist = OrdinalIgnoreCaseSet.empty
      SkipInstallPrompt = false
      ForceLocal = false
      CompatTool = None
      CbLauncherDir = "."
      PreferredLanguage = None
      ApiUri = Uri("http://localhost:8080")
      Restart = None
      AutoUpdate = true
      CheckForLauncherUpdates = true
      MaxConcurrentDownloads = 4
      ForceUpdate = Set.empty
      Processes = List.empty
      ShutdownProcesses = List.empty
      FilterOverrides = OrdinalIgnoreCaseMap.empty
      AdditionalProducts = List.empty
      DryRun = false
      ShutdownTimeout = TimeSpan.FromSeconds(10)
      CacheDir = ""
      GameStartDelay = TimeSpan.Zero
      ShutdownDelay = TimeSpan.Zero }
    
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
    
let applyFallbackEpidIds settings =
    match settings.Platform with
    | Epic details -> result {
        let! depId =
            details.DeploymentId
            |> Option.map Ok
            |> Option.defaultWith (fun () ->
                Epic.getDeploymentId()
                |> Result.mapError (fun e -> $"Failed to get Epic deployment id: {e}")
            )
        let! sandboxId =
            details.SandboxId
            |> Option.map Ok
            |> Option.defaultWith (fun () ->
                Epic.getDeploymentId()
                |> Result.mapError (fun e -> $"Failed to get Epic sandbox id: {e}")
            )
        return { settings with Platform = Epic { details with DeploymentId = Some depId; SandboxId = Some sandboxId } }
        }
    | _ -> Ok settings
    
let private doesntEndWith (value: string) (str: string) = not (str.EndsWith(value))
type private EpicArg = ExchangeCode of string | Type of string | AppId of string | SandboxId of string | DeploymentId of string
let parseArgs defaults (findCbLaunchDir: Platform -> Result<string,string>) (argv: string[]) =
    let launcherIndex =
        argv
        |> Array.tryFindIndex (fun s -> s <> null && s.EndsWith("EDLaunch.exe", StringComparison.OrdinalIgnoreCase))
        |> Option.defaultValue -1
    
    let compatTool, cbLaunchDir, args =
        if launcherIndex < 0 then
            None, None, argv
        else if launcherIndex = 0 then
            None, Path.GetDirectoryName(argv[launcherIndex]) |> Some, argv[1..]
        else if argv.Length > 1 then
            Some { EntryPoint = argv[0]; Args = argv[1..launcherIndex - 1] }, Path.GetDirectoryName(argv[launcherIndex]) |> Some, argv[launcherIndex + 1..]
        else
            Some { EntryPoint = argv[0]; Args = [||] }, Path.GetDirectoryName(argv[launcherIndex]) |> Some, [||]
            
    let getArg (arg:string) i =
        if i + 1 < args.Length && not (String.IsNullOrEmpty args[i + 1]) && (not (args[i + 1].StartsWith '/') && not (args[i].StartsWith '-')) then // /arg argValue
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
                | ExchangeCode p  -> { details with ExchangeCode = p }
                | Type t          -> { details with Type = t }
                | AppId id        -> { details with AppId = id }
                | SandboxId id    -> { details with SandboxId = Some id }
                | DeploymentId id -> { details with DeploymentId = Some id })
            |> Option.defaultValue details
            |> Epic
            
        match platform with
        | Epic d -> apply arg d
        | _ -> apply arg EpicDetails.Empty
    
    let (|PosDouble|_|) (str: string option) =
        str
        |> Option.bind (fun str ->
            match Double.TryParse(str) with
            | true, v when v >= 0. -> Some v
            | _ -> None)
    
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
            | "-epicsandboxid", Some id       -> { s with Platform = epicArg (SandboxId id) }
            | "-epicdeploymentid", Some id    -> { s with Platform = epicArg (DeploymentId id) }
            | "/oculus", Some nonce           -> { s with Platform = Oculus nonce; ForceLocal = true }
            | "/restart", PosDouble delay     -> { s with Restart = delay * 1000. |> int64 |> Some }
            | "/vr", _                        -> { s with DisplayMode = Vr }
            | "/novr", _                      -> { s with DisplayMode = Pancake }
            | "/autorun", _                   -> { s with AutoRun = true }
            | "/autoquit", Some arg when arg.Equals("waitforexit", StringComparison.OrdinalIgnoreCase)
                                              -> { s with QuitMode = WaitForExit }
            | "/autoquit", _                  -> { s with QuitMode = Immediate }
            | "/forcelocal", _                -> { s with ForceLocal = true }
            | "/dryrun", _                    -> { s with DryRun = true }
            | "/skipinstallprompt", _         -> { s with SkipInstallPrompt = true }
            | arg, _ when arg.StartsWith('/')
                          && arg.Length > 1   -> { s with ProductWhitelist = s.ProductWhitelist.Add (arg.TrimStart('/')) }
            | _ -> s) defaults
    
    cbLaunchDir
    |> Option.map Ok |> Option.defaultWith (fun () -> findCbLaunchDir settings.Platform)
    |> Result.map (fun launchDir -> { settings with CompatTool = compatTool; CbLauncherDir = launchDir })

[<CLIMutable>] type FilterConfig = { Sku: string; Filter: string }
[<CLIMutable>]
type ProcessConfig =
    { FileName: string
      Arguments: string option
      RestartOnRelaunch: bool
      KeepOpen: bool }
[<CLIMutable>]
type Config =
    { ApiUri: string
      WatchForCrashes: bool
      GameLocation: string option
      Language: string option
      [<DefaultValue("true")>]
      AutoUpdate: bool
      [<DefaultValue("true")>]
      CheckForLauncherUpdates: bool
      [<DefaultValue("4")>]
      MaxConcurrentDownloads: int
      ForceUpdate: string list
      Processes: ProcessConfig list
      ShutdownProcesses: ProcessConfig list
      FilterOverrides: FilterConfig list
      AdditionalProducts: AuthorizedProduct list
      [<DefaultValue("10")>]
      ShutdownTimeout: int
      CacheDir: string option
      [<DefaultValue("0")>]
      GameStartDelay: int
      [<DefaultValue("0")>]
      ShutdownDelay: int }
let parseConfig fileName =
    let configRoot = ConfigurationBuilder()
                        .AddJsonFile(fileName, false)
                        .Build()
    let parseKvps section keyName valueName map =
        configRoot.GetSection(section).GetChildren()
            |> Seq.choose (fun section ->
                let key = section.GetValue<string>(keyName)
                let value = section.GetValue<string>(valueName)
                if String.IsNullOrWhiteSpace(key) then
                    None
                else
                    map key value)
            |> Seq.toList
    let parseProcesses section =
        configRoot.GetSection(section).GetChildren()
            |> Seq.choose (fun section ->
                let fileName = section.GetValue<string>("fileName")
                let args = section.GetValue<string>("arguments")
                let restart = section.GetValue<bool>("restartOnRelaunch")
                let keepOpen = section.GetValue<bool>("keepOpen")
                if String.IsNullOrWhiteSpace(fileName) then
                    None
                else
                    Some { FileName = fileName; Arguments = Option.ofObj args; RestartOnRelaunch = restart; KeepOpen = keepOpen })
            |> Seq.toList
    let parseAdditionalProducts() =
        configRoot.GetSection("additionalProducts").GetChildren()
        |> Seq.mapOrFail AuthorizedProduct.fromConfig
    match AppConfig(configRoot).Get<Config>() with
    | Ok config ->
        // FsConfig doesn't support list of records so handle it manually
        let processes = parseProcesses "processes"
        let shutdownProcesses = parseProcesses "shutdownProcesses"
        let filterOverrides =
            parseKvps "filterOverrides" "sku" "filter" (fun key value ->
                if String.IsNullOrWhiteSpace(value) then None
                else Some { Sku = key; Filter = value })
        match parseAdditionalProducts() with
        | Ok additionalProducts -> { config with Processes = processes; ShutdownProcesses = shutdownProcesses; FilterOverrides = filterOverrides; AdditionalProducts = additionalProducts } |> ConfigParseResult.Ok
        | Error msg -> BadValue ("additionalProducts", msg) |> Error
    | Error error -> Error error
   
let private mapProcessConfig p =
    let pInfo = ProcessStartInfo()
    pInfo.FileName <- p.FileName
    pInfo.WorkingDirectory <- Path.GetDirectoryName(p.FileName)
    pInfo.Arguments <- p.Arguments |> Option.defaultValue ""
    pInfo.UseShellExecute <- false
    pInfo.RedirectStandardOutput <- true
    pInfo.RedirectStandardError <- true
    pInfo.WindowStyle <- ProcessWindowStyle.Minimized
    pInfo
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
    let processes = fileConfig.Processes |> List.map (fun p -> {| Info = mapProcessConfig p; RestartOnRelaunch = p.RestartOnRelaunch; KeepOpen = p.KeepOpen |}) 
    let shutdownProcesses = fileConfig.ShutdownProcesses |> List.map mapProcessConfig
    let filterOverrides = fileConfig.FilterOverrides |> Seq.map (fun o -> o.Sku, o.Filter) |> OrdinalIgnoreCaseMap.ofSeq
    let fallbackDirs platform =
        match platform with
        | Epic d -> Epic.potentialInstallPaths d.AppId |> findCbLaunchDir
        | Steam -> Steam.potentialInstallPaths() |> findCbLaunchDir
        | Frontier _-> Cobra.potentialInstallPaths() @ Steam.potentialInstallPaths() |> findCbLaunchDir
        | Dev -> findCbLaunchDir []
        | _ -> Error "Unknown platform. Failed to find Elite Dangerous install directory"
    
    let! settings =
        parseArgs defaults fallbackDirs args
        |> Result.tee (fun s -> Directory.SetCurrentDirectory(s.CbLauncherDir))
        |> Result.bind applyFallbackEpidIds
        |> Result.bindTask applyDeviceAuth
    
    return settings
           |> Result.map (fun settings -> { settings with ApiUri = apiUri
                                                          AutoUpdate = fileConfig.AutoUpdate
                                                          CheckForLauncherUpdates = fileConfig.CheckForLauncherUpdates
                                                          ForceUpdate = fileConfig.ForceUpdate |> Set.ofList
                                                          MaxConcurrentDownloads = fileConfig.MaxConcurrentDownloads
                                                          PreferredLanguage = fileConfig.Language
                                                          Processes = processes
                                                          ShutdownProcesses = shutdownProcesses
                                                          FilterOverrides = filterOverrides
                                                          WatchForCrashes = fileConfig.WatchForCrashes
                                                          AdditionalProducts = fileConfig.AdditionalProducts
                                                          ShutdownTimeout = TimeSpan.FromSeconds(fileConfig.ShutdownTimeout)
                                                          CacheDir = fileConfig.CacheDir |> Option.defaultValue Environment.cacheDir
                                                          GameStartDelay = TimeSpan.FromSeconds(fileConfig.GameStartDelay)
                                                          ShutdownDelay = TimeSpan.FromSeconds(fileConfig.ShutdownDelay) 
                                           })
}