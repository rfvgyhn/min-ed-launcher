namespace EdLauncher

module Program =
    open System
    open System.Diagnostics
    open System.IO
    open System.Reflection
    open System.Resources
    open System.Runtime.InteropServices
    open FSharp.Control.Tasks.NonAffine
    open FileIO
    open FsConfig
    open Steam
    open EdLauncher.Token
    open Types
    open Settings
    open Rop
        
    type OS = Linux | Windows | OSX | FreeBSD | Unknown
    let getOs() =
        let platToOs plat =
            if   plat = OSPlatform.Linux   then Linux
            elif plat = OSPlatform.Windows then Windows
            elif plat = OSPlatform.OSX     then OSX
            elif plat = OSPlatform.FreeBSD then FreeBSD
            else Unknown        
        [ OSPlatform.Linux; OSPlatform.Windows; OSPlatform.OSX; OSPlatform.FreeBSD ]
        |> List.pick (fun p -> if RuntimeInformation.IsOSPlatform(p) then Some p else None)
        |> platToOs
        
    let getOsIdent() =
        let osToStr = function
            | Linux -> "Linux"
            | Windows -> "Win"
            | OSX -> "Mac"
            | FreeBSD -> "FreeBSD"
            | Unknown -> "Unknown"
        let arch =
            match RuntimeInformation.ProcessArchitecture with
            | Architecture.Arm -> "Arm"
            | Architecture.Arm64 -> "Arm64"
            | Architecture.X64 -> "64"
            | Architecture.X86 -> "32"
            | unknownArch -> unknownArch.ToString()
        let os = getOs() |> osToStr
        os + arch

    let getAppSettingsPath cbLauncherVersion =
#if WINDOWS
        let appSettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments")
#else
        let steamCompat = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH")
        let appSettingsPath =
            if not (String.IsNullOrEmpty(steamCompat)) then
                Path.Combine(steamCompat, "pfx", "drive_c", "users", "steamuser", "Local Settings", "Application Data", "Frontier_Developments")
            else
                let home = Environment.expandEnvVars("~")
                let user = Environment.expandEnvVars("$USER")
                [ $"{home}/.local/share/Steam/steamapps/compatdata/359320/pfx/drive_c/users/steamuser"
                  $"{home}/.steam/steam/steamapps/compatdata/359320/pfx/drive_c/users/steamuser"
                  $"{home}/Games/elite-dangerous/drive_c/users/{user}" // lutris
                  $"{home}/.wine/drive_c/users/{user}" ]
                |> List.map (fun path -> $"%s{path}/Local Settings/Application Data/Frontier_Developments")
                |> List.tryFind Directory.Exists
                |> Option.defaultValue "."
#endif
        let path =
            Directory.EnumerateDirectories(appSettingsPath, "EDLaunch.exe*")
            |> Seq.tryHead
            |> Option.map (fun dir -> Path.Combine(dir, cbLauncherVersion, "user.config"))
        match path with
        | Some path ->
            if File.Exists(path) then
                Ok path
            else
                sprintf "Couldn't find user.config in '%s'" path |> Error
        | None -> sprintf "Couldn't find user.config in '%s'" appSettingsPath |> Error
        
    let getUserEmail appSettingsPath =
        let xpath = "//*[@name='UserName']/value"
        appSettingsPath
        |> Xml.getValue xpath
        |> Option.map (fun v -> if v = null then "" else v)

    type LoginResult =
    | Success of Api.Connection
    | ActionRequired of string
    | Failure of string
    let login runningTime httpClient machineId (platform: Platform) =
        let authenticate = function
            | Ok authToken -> task {
                Log.debug $"Authenticating via %s{platform.Name}"
                match! Api.authenticate runningTime authToken platform machineId httpClient with
                | Api.Authorized connection ->
                    Log.debug "Successfully authenticated"
                    return Success connection
                | Api.RegistrationRequired uri -> return ActionRequired <| $"Registration is required at %A{uri}"
                | Api.LinkAvailable uri -> return ActionRequired <| $"Link available at %A{uri}"
                | Api.Denied msg -> return Failure msg
                | Api.Failed msg -> return Failure msg }
            | Error msg -> Failure msg |> Task.fromResult
            
        let noopDisposable = { new IDisposable with member _.Dispose() = () }
        
        match platform with
        | Oculus _ -> (Failure "Oculus not supported", noopDisposable) |> Task.fromResult
        | Frontier-> (Failure "Frontier not supported", noopDisposable) |> Task.fromResult
        | Dev -> task {
            let! result = Permanent "DevAuthToken" |> Ok |> authenticate
            return result, noopDisposable }
        | Steam -> task {
            use steam = new Steam()
            let! result = steam.Login() |> Result.map (fun steamUser -> Permanent steamUser.SessionToken) |> authenticate
            return result, noopDisposable }
        | Epic details -> task {
            match! Epic.login details with
            | Ok t ->
                let tokenManager = new RefreshableTokenManager(t, Epic.refreshToken)
                let! result = tokenManager.Get |> Expires |> Ok |> authenticate 
                return result, (tokenManager :> IDisposable)
            | Error msg -> return Failure msg, noopDisposable }

    let getLogPath = function
        | Dev -> Ok "logs"
        | Oculus _ -> Error "Oculus not supported"
        | Frontier -> Error "Frontier not supported"
        | Steam -> Ok "logs"
        | Epic _ -> Ok "logs"
            
    let getProductsDir fallbackPath hasWriteAccess (forceLocal:ForceLocal) launcherDir =
        let productsPath = "Products"
        let localPath = Path.Combine(launcherDir, productsPath)
        if forceLocal then localPath
        elif hasWriteAccess launcherDir then localPath
        else Path.Combine(fallbackPath, productsPath)
        
    let getVersion cbLauncherDir =
        let cobraPath = Path.Combine(cbLauncherDir, "CBViewModel.dll")
        
        if not (File.Exists cobraPath) then
            Error <| sprintf "Unable to find CBViewModel.dll in directory %s" cbLauncherDir
        else
            let cobraVersion =
                let version = FileVersionInfo.GetVersionInfo(cobraPath)
                if String.IsNullOrEmpty(version.FileVersion) then version.ProductVersion else version.FileVersion
            let launcherVersion = typeof<Steam>.Assembly.GetName().Version
            
            Ok (cobraVersion, launcherVersion)
        
    let printInfo (platform: Platform) productsDir cobraVersion launcherVersion =
        Log.info $"""Elite: Dangerous Launcher
    Platform: %s{platform.Name}
    OS: %s{getOsIdent()}
    CobraBay Version: %s{cobraVersion}
    Launcher Version: %A{launcherVersion}
    Products Dir: %s{productsDir}"""

    type VersionInfoStatus = Found of VersionInfo | NotFound of string | Failed of string
    let readVersionInfo path = 
        let file = Path.Combine(path, "VersionInfo.txt")
        let mode offline = if offline then Offline else Online
        if not (File.Exists(file)) then NotFound file
        else
            let json = (FileIO.openRead file) >>= Json.parseStream >>= Json.rootElement
            let version = json >>= Json.parseProp "Version" >>= Json.asVersion
            let exe = json >>= Json.parseProp "executable" >>= Json.toString
            let name = json >>= Json.parseProp "name" >>= Json.toString
            let wd64 = json >>= Json.parseProp "useWatchDog64" >>= Json.asBool |> Result.defaultValue false
            let steamAware = json >>= Json.parseProp "steamaware" >>= Json.asBool |> Result.defaultValue true
            let offline = json >>= Json.parseProp "offline" >>= Json.asBool |> Result.defaultValue false
            match version, exe, name with
            | Ok version, Ok exe, Ok name ->
                { Name = name
                  Executable = exe
                  UseWatchDog64 = wd64
                  SteamAware = steamAware
                  Version = version
                  Mode = mode offline } |> Found
            | _ -> VersionInfoStatus.Failed "Unexpected VersionInfo json document"
    
    let mapProduct productsDir (product:AuthorizedProduct) =
        let serverArgs = String.Join(" ", [
                if product.TestApi then "/Test"
                if not (String.IsNullOrEmpty(product.ServerArgs)) then product.ServerArgs
            ])
        let filters = product.Filter.Split(',', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray
        let directory = Path.Combine(productsDir, product.Directory)
        match readVersionInfo (Path.Combine(productsDir, product.Directory)) with
        | Found v ->
            Playable { Sku = product.Sku
                       Name = product.Name
                       Filters = filters
                       Executable = v.Executable
                       UseWatchDog64 = v.UseWatchDog64
                       SteamAware = v.SteamAware
                       Version = v.Version
                       Mode = v.Mode
                       Directory = directory
                       GameArgs = product.GameArgs
                       ServerArgs = serverArgs }
        | NotFound file ->
            Log.infof "Disabling '%s'. Unable to find product at '%s'" product.Name file
            Missing { Sku = product.Sku
                      Name = product.Name
                      Filters = filters
                      Directory = directory }
        | Failed msg ->
            Log.errorf "Unable to parse product %s: %s" product.Name msg
            Product.Unknown product.Name
            
    let getGameLang cbLauncherDir =
        let asm = Assembly.LoadFrom(Path.Combine(cbLauncherDir, "LocalResources.dll"))
        let resManager = ResourceManager("LocalResources.Properties.Resources", asm)
        try
            resManager.GetString("GameLanguage") |> Some
        with
        | e -> None
        
    let launchProcesses (processes:ProcessStartInfo list) =
        processes
        |> List.choose (fun p ->
            try
                Log.infof "Starting process %s" p.FileName
                Process.Start(p) |> Some
            with
            | e ->
                Log.exnf e "Unable to start pre-launch process %s" p.FileName
                None)
    
    let stopProcesses (processes: Process list) =
        processes
        |> List.iter (fun p ->
            Log.debugf "Stopping process %s" p.ProcessName
            match Interop.termProcess p with
            | Ok () ->
                p.StandardOutput.ReadToEnd() |> ignore
                p.StandardError.ReadToEnd() |> ignore
                Log.infof "Stopped process %s" p.ProcessName
            | Error msg -> Log.warn msg)
        
    let rec launchProduct proton processArgs restart productName product =
        match Product.run proton (processArgs()) product with
        | Product.RunResult.Ok p ->
            let shouldRestart = restart |> fst
            let timeout = restart |> snd
            let cancelRestart() =
                let interval = 250
                let stopwatch = Stopwatch()
                stopwatch.Start()
                while Console.KeyAvailable = false && stopwatch.ElapsedMilliseconds < timeout do
                    System.Threading.Thread.Sleep interval
                    let remaining = timeout / 1000L - stopwatch.ElapsedMilliseconds / 1000L
                    Console.SetCursorPosition(0, Console.CursorTop)
                    Console.Write(sprintf "Restarting in %i seconds. Press any key to quit." remaining)
                    
                let left = Console.CursorLeft
                Console.SetCursorPosition(0, Console.CursorTop)
                if Console.KeyAvailable then
                    Console.ReadKey() |> ignore
                    Console.WriteLine("Shutting down...".PadRight(left))
                    true
                else
                    Console.WriteLine("Restarting...".PadRight(left))
                    false

            Log.infof "Launching %s" productName
            use p = p
            p.WaitForExit()
            Log.infof "Shutdown %s" productName
            
            if shouldRestart && not (cancelRestart()) then
                launchProduct proton processArgs restart productName product
        | Product.RunResult.AlreadyRunning -> Log.infof "%s is already running" productName
        | Product.RunResult.Error e -> Log.errorf "Couldn't start selected product: %s" (e.ToString())
        
    let private run settings = task {
        let appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments")
        let productsDir =
            getProductsDir appDataDir hasWriteAccess settings.ForceLocal settings.CbLauncherDir
            |> ensureDirExists
        let version = getVersion settings.CbLauncherDir
        return!
            match productsDir, version with 
            | _, Error msg -> task {
                Log.errorf "Unable to get version: %s" msg
                return 1 }
            | Error msg, _ -> task { 
                Log.errorf "Unable to get products directory: %s" msg
                return 1 }
            | Ok productsDir, Ok (cbVersion, launcherVersion) -> task {
                printInfo settings.Platform productsDir cbVersion launcherVersion
                use httpClient = Api.createClient settings.ApiUri cbVersion (getOsIdent())
                let localTime = DateTime.UtcNow
                let! remoteTime = task {
                    match! Api.getTime localTime httpClient with
                    | Ok timestamp -> return timestamp
                    | Error (localTimestamp, msg) ->
                        Log.warn $"Couldn't get remote time: %s{msg}. Using local system time instead"
                        return localTimestamp
                }
                let runningTime = fun () ->
                        let runningTime = DateTime.UtcNow.Subtract(localTime);
                        ((double)remoteTime + runningTime.TotalSeconds)
                let! machineId =
#if WINDOWS
                    MachineId.getWindowsId() |> Task.fromResult
#else
                    MachineId.getWineId()
#endif
                // TODO: Check if launcher version is compatible with current ED version
                
                match machineId, (getAppSettingsPath cbVersion) with
                | Ok machineId, Ok appSettingsPath ->
                    let userEmail = getUserEmail appSettingsPath
                    let! loginResult, disposable = login runningTime httpClient machineId settings.Platform
                    use _ = disposable
                    match loginResult with
                    | Success connection ->
                        let emailDisplay = userEmail |> Option.map (fun e -> $"({e})") |> Option.defaultValue ""
                        Log.info $"Logged in via %s{settings.Platform.Name} as: %s{connection.Session.Name} %s{emailDisplay}"
                        Log.debug "Getting authorized products"
                        match! Api.getAuthorizedProducts settings.Platform None connection with
                        | Ok authorizedProducts ->
                            let names = authorizedProducts |> List.map (fun p -> p.Name)
                            Log.debug $"Authorized Products: %s{String.Join(',', names)}"
                            Log.info "Checking for updates"
                            let! products = authorizedProducts
                                            |> List.map (mapProduct productsDir)
                                            |> List.filter (function | Playable _ -> true | _ -> false)
                                            |> Api.checkForUpdates settings.Platform machineId connection
                            let availableProducts =
                                products
                                |> Result.defaultWith (fun e -> Log.warn $"{e}"; [])
                                |> List.map (fun p -> match p with
                                                      | Playable p -> Some (p.Name, "Up to date")
                                                      | RequiresUpdate p -> Some (p.Name, "Requires Update")
                                                      | Missing _ -> None
                                                      | Product.Unknown _ -> None)
                                |> List.choose id
                            let availableProductsDisplay =
                                match availableProducts with
                                | [] -> "None"
                                | p -> String.Join(Environment.NewLine + "\t", p)
                            Log.info $"Available Products:{Environment.NewLine}\t%s{availableProductsDisplay}" 
                            let selectedProduct =
                               products
                               |> Result.defaultValue []
                               |> List.choose (fun p -> match p with | Playable p -> Some p | _ -> None)
                               |> List.filter (fun p -> settings.ProductWhitelist.Count = 0
                                                        || p.Filters |> Set.union settings.ProductWhitelist |> Set.count > 0)
                               |> List.tryHead
                            
                            match selectedProduct, true with
                            | Some product, true ->
                                let gameLanguage = getGameLang settings.CbLauncherDir                                 
                                let processArgs() = Product.createArgString settings.DisplayMode gameLanguage connection.Session machineId (runningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile product
                                
                                match Product.validateForRun settings.CbLauncherDir settings.WatchForCrashes product with
                                | Ok p ->
                                    let processes = launchProcesses settings.Processes
                                    launchProduct settings.Proton processArgs settings.Restart product.Name p
                                    stopProcesses processes
                                | Error msg -> Log.errorf "Couldn't start selected product: %s" msg
                            | None, true -> Log.error "No selected project"
                            | _, _ -> ()
                            
                            if not settings.AutoQuit then
                                printfn "Press any key to quit..."
                                Console.ReadKey() |> ignore
                            
                        | Error msg ->
                            Log.errorf "Couldn't get available products: %s" msg
                    | ActionRequired msg ->
                        Log.errorf "Unsupported login action required: %s" msg
                    | Failure msg ->
                        Log.errorf "Couldn't login: %s" msg
                | Error msg, _ ->
                    Log.errorf "Couldn't get machine id: %s" msg
                | _, Error msg ->
                    Log.error msg
                return 0 }
    }    

    [<EntryPoint>]
    let main argv =
        async {
            try
                do! Async.SwitchToThreadPool ()
                Log.debugf "Args: %A" argv
                let settings =
                    let path = Path.Combine(Environment.configDir, "elite-dangerous-launcher")
                    match ensureDirExists path with
                    | Error msg -> sprintf "Unable to find/create configuration directory at %s - %s" path msg |> Error
                    | Ok settingsDir ->
                        let settingsPath = Path.Combine(settingsDir, "settings.json")
                        if not (File.Exists(settingsPath)) then
                            use settings = typeof<Steam>.GetTypeInfo().Assembly.GetManifestResourceStream("EdLauncher.settings.json")
                            use file = File.OpenWrite(settingsPath)
                            settings.CopyTo(file)
                        |> ignore
                            
                        parseConfig settingsPath
                        |> Result.mapError (fun e ->
                            match e with
                            | BadValue (key, value) -> sprintf "Bad Value: %s - %s" key value
                            | ConfigParseError.NotFound key -> sprintf "Key not found: %s" key
                            | NotSupported key -> sprintf "Key not supported: %s" key)
                        >>= getSettings argv
                Log.debugf "Settings: %A" settings
                return! match settings with
                        | Ok settings -> run settings |> Async.AwaitTask
                        | Error msg -> async { Log.error msg; return 1 }
            with
            | e -> Log.error $"Unhandled exception: {e}"; return 1
        } |> Async.RunSynchronously
        
        
