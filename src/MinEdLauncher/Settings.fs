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
      ProductWhitelist = OrdinalIgnoreCaseSet.empty
      ForceLocal = false
      Proton = None
      CbLauncherDir = "."
      PreferredLanguage = None
      ApiUri = Uri("http://localhost:8080")
      Restart = None
      AutoUpdate = true
      MaxConcurrentDownloads = 4
      ForceUpdate = Set.empty
      Processes = List.empty
      FilterOverrides = OrdinalIgnoreCaseMap.empty }
    
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
    
let private doesntEndWith (value: string) (str: string) = not (str.EndsWith(value))
type private EpicArg = ExchangeCode of string | Type of string | AppId of string
let parseArgs defaults (findCbLaunchDir: Platform -> Result<string,string>) (argv: string[]) =
    let proton, cbLaunchDir, args =
        let isOldProton (args: string[]) = // Proton < 5.13 doesn't run via steam linux runtime
            args.Length > 2 && args.[0] <> null
            && [ Path.Combine("steamapps", "common", "Proton"); Path.Combine("Steam", "compatibilitytools.d", "Proton") ]
               |> List.exists (fun p -> args.[0].Contains(p))
        let isNewProton (args: string[]) = // Proton >= 5.13 runs via steam linux runtime
            args.Length > 2 && args.[0] <> null
            && args.[0].Contains("SteamLinuxRuntime")
        let isReaper() = // Steam Client v? introduced a reaper process as entrypoint
            argv.Length > 2 && argv.[0] <> null
            && argv.[0].EndsWith("/reaper")
        
        // TODO: figure out how to get elite to run with reaper instead of ignoring it
        let argv =
            if isReaper() then
                let index = (argv |> Array.findIndex (fun arg -> arg = "--")) + 1
                argv.[index..]
            else
                argv
        if isNewProton argv then
            let runtimeArgs = argv.[1..] |> Array.takeWhile (doesntEndWith "proton")
            let protonArgs = argv.[1..] |> Array.skipWhile (doesntEndWith "proton") |> Array.takeWhile (doesntEndWith "EDLaunch.exe")
            let args = seq { runtimeArgs; [| "python3" |]; protonArgs } |> Array.concat
            let launcherIndex = args.Length
            Some { EntryPoint = argv.[0]; Args = args }, Path.GetDirectoryName(argv.[launcherIndex]) |> Some, argv.[launcherIndex + 1..]
        else if isOldProton argv then 
            Some { EntryPoint = "python3"; Args = argv.[..1] }, Path.GetDirectoryName(argv.[2]) |> Some, argv.[3..]
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
            | "/oculus", Some nonce           -> { s with Platform = Oculus nonce; ForceLocal = true }
            | "/restart", PosDouble delay     -> { s with Restart = delay * 1000. |> int64 |> Some }
            | "/vr", _                        -> { s with DisplayMode = Vr; AutoRun = true }
            | "/novr", _                      -> { s with DisplayMode = Pancake }
            | "/autorun", _                   -> { s with AutoRun = true }
            | "/autoquit", _                  -> { s with AutoQuit = true }
            | "/forcelocal", _                -> { s with ForceLocal = true }
            | arg, _ when arg.StartsWith('/')
                          && arg.Length > 1   -> { s with ProductWhitelist = s.ProductWhitelist.Add (arg.TrimStart('/')) }
            | _ -> s) defaults
    
    match cbLaunchDir
          |> Option.map Ok |> Option.defaultWith (fun () -> findCbLaunchDir settings.Platform)
          |> Result.map (fun launchDir -> { settings with Proton = proton; CbLauncherDir = launchDir }) with
    | Ok settings ->
        applyDeviceAuth settings
    | Result.Error msg -> Result.Error msg |> Task.fromResult
    

[<CLIMutable>] type ProcessConfig = { FileName: string; Arguments: string option }
[<CLIMutable>] type FilterConfig = { Sku: string; Filter: string }
[<CLIMutable>]
type Config =
    { ApiUri: string
      WatchForCrashes: bool
      GameLocation: string option
      Language: string option
      [<DefaultValue("true")>]
      AutoUpdate: bool
      [<DefaultValue("4")>]
      MaxConcurrentDownloads: int
      ForceUpdate: string list
      Processes: ProcessConfig list
      FilterOverrides: FilterConfig list }
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
    match AppConfig(configRoot).Get<Config>() with
    | Ok config ->
        // FsConfig doesn't support list of records so handle it manually
        let processes =
            parseKvps "processes" "fileName" "arguments" (fun key value ->
                Some { FileName = key; Arguments = if value = null then None else Some value })
        let filterOverrides =
            parseKvps "filterOverrides" "sku" "filter" (fun key value ->
                if String.IsNullOrWhiteSpace(value) then None
                else Some { Sku = key; Filter = value })
        { config with Processes = processes; FilterOverrides = filterOverrides } |> ConfigParseResult.Ok
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
    let filterOverrides = fileConfig.FilterOverrides |> Seq.map (fun o -> o.Sku, o.Filter) |> OrdinalIgnoreCaseMap.ofSeq
    let fallbackDirs platform =
        match platform with
        | Epic d -> Epic.potentialInstallPaths d.AppId |> findCbLaunchDir
        | Steam -> Steam.potentialInstallPaths() |> findCbLaunchDir
        | Frontier _-> Cobra.potentialInstallPaths() @ Steam.potentialInstallPaths() |> findCbLaunchDir
        | _ -> Error "Unknown platform. Failed to find Elite Dangerous install directory"
    
    let! settings = parseArgs defaults fallbackDirs args
    return settings
           |> Result.map (fun settings -> { settings with ApiUri = apiUri
                                                          AutoUpdate = fileConfig.AutoUpdate
                                                          ForceUpdate = fileConfig.ForceUpdate |> Set.ofList
                                                          MaxConcurrentDownloads = fileConfig.MaxConcurrentDownloads
                                                          PreferredLanguage = fileConfig.Language
                                                          Processes = processes
                                                          FilterOverrides = filterOverrides
                                                          WatchForCrashes = fileConfig.WatchForCrashes }) }