namespace EdLauncher

module Program =
    open System
    open System.Diagnostics
    open System.IO
    open System.Net.Http
    open System.Reflection
    open System.Resources
    open System.Runtime.InteropServices
    open FileIO
    open FsConfig
    open Steam
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

    let getEventLogPaths httpClient = async {
        let! result = EventLog.LocalFile.create "test.txt" // logs/Client.log
        let file =
            match result with
            | Ok file -> Some file
            | Error msg ->
                Log.error msg
                None
        let remote =
            let url = "http://localhost:8080" // https://api.zaonce.net/1.1/
            match httpClient, Uri.TryCreate(url, UriKind.Absolute) with
            | None, _ ->
                Log.info "Remote logging disabled via configuration"
                None
            | Some httpClient, (true, uri) ->
                Some <| EventLog.RemoteLog (httpClient, { Uri = uri
                                                          MachineToken = ""
                                                          AuthToken = ""
                                                          MachineId = ""
                                                          RunningTime = fun () -> 1L })
            | Some _, (false, _) ->
                Log.errorf "EventLog.RemotePath - Invalid URI %s. Disabling" url
                None
        return file, remote
    }
    let writeEventLog httpClient entry = async {
        let! file, remote = getEventLogPaths httpClient
        let! result = EventLog.write file remote entry
        result |> Array.iter (fun e -> match e with
                                       | Error e -> Log.warn e
                                       | _ -> ())
    }
    let getAppSettingsPath cbLauncherVersion =
#if WINDOWS
        let appSettingsPath = "" // TODO: get app data path
#else
        let steamCompat = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH")
        let appSettingsPath =
            if not (String.IsNullOrEmpty(steamCompat)) then
                Path.Combine(steamCompat, "pfx", "drive_c", "users", "steamuser", "Local Settings", "Application Data", "Frontier_Developments")
            else
                // TODO: search common locations for steam compat data paths
                "/mnt/games/Steam/Linux/steamapps/compatdata/359320/pfx/drive_c/users/steamuser/Local Settings/Application Data/Frontier_Developments"
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
        |> Option.map (fun v -> if v = null then Error ("Username is null " + xpath) else Ok v)
        |> Option.defaultValue (sprintf "Couldn't get user name from '%s'" appSettingsPath |> Error)

    let mapPlatformToAuthDetails sessionToken machineToken = function
        | Steam -> Api.Steam (sessionToken, machineToken)
        | Epic _ -> Api.Epic (sessionToken, machineToken)
        | Frontier -> Api.Frontier (sessionToken, machineToken)
        | Oculus _ -> raise (NotImplementedException())
        | Dev -> raise (NotImplementedException())
    type LoginResult =
    | Success of User
    | ActionRequired of string
    | Failure of string
    let login appSettingsPath serverRequest machineId platform = async {
        match platform, (getUserEmail appSettingsPath) with
        | _, Error msg -> return Failure msg
        | Dev, Ok userEmail -> return Success { Name = "Dev User"; MachineToken = "DevToken"; EmailAddress = userEmail; SessionToken = "AuthToken" }
        | Oculus _, _ -> return Failure "Oculus not supported"
        | Frontier, _ -> return Failure "Frontier not supported"
        | Steam, Ok userEmail ->
            use steam = new Steam()
            return! match steam.Login() with
                    | Ok steamUser -> async {
                        let authDetails = Api.Steam (steamUser.SessionToken, machineId)
                        // TODO: event log RequestingSteamAuthentication no params
                        Log.debug "Authenticating via Steam"
                        match! Api.authenticate authDetails serverRequest with
                        | Api.Authorized (authToken, machineToken, name) ->
                            Log.debug "Successfully authenticated"
                            // TODO: event log SteamAuthenticated no params
                            return Success { Name = name
                                             EmailAddress = userEmail
                                             SessionToken = authToken
                                             MachineToken = machineToken }
                        | Api.RegistrationRequired uri -> return ActionRequired <| sprintf "Registration is required at %A" uri
                        | Api.LinkAvailable uri -> return ActionRequired <| sprintf "Link available at %A" uri
                        | Api.Denied msg -> return Failure msg
                        | Api.Failed msg -> return Failure msg
                        }
                    | Error msg -> async { return Failure msg }
            
    }

    let getLogPath = function
        | Dev -> Ok "logs"
        | Oculus _ -> Error "Oculus not supported"
        | Frontier -> Error "Frontier not supported"
        | Steam -> Ok "logs"
            
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
            
    let createZaonceClient baseUri clientName clientVersion osIdent =
        let userAgent = sprintf "%s/%s/%s" clientName clientVersion osIdent
        let httpClient = new HttpClient()
        httpClient.BaseAddress <- baseUri
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
        httpClient.DefaultRequestHeaders.ConnectionClose <- Nullable<bool>(false)
        httpClient
        
    let printInfo platform productsDir cobraVersion launcherVersion remoteTime =
        Log.info "Elite: Dangerous Launcher"
        Log.infof "Platform: %A" platform
        Log.infof "OS: %s" (getOsIdent())
        Log.infof "CobraBay Version: %s" cobraVersion
        Log.infof "Launcher Version: %A" launcherVersion
        Log.infof "Launcher Name: %A" (System.Reflection.AssemblyName.GetAssemblyName("/mnt/games/Steam/Linux/steamapps/common/Elite Dangerous/EDLaunch.exe").Name)
        Log.infof "Remote Time: %i" remoteTime
        Log.infof "Products Dir: %s" productsDir

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
        match Product.run proton processArgs product with
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
        
    let private run settings = async {
        let appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments")
        let productsDir =
            getProductsDir appDataDir hasWriteAccess settings.ForceLocal settings.CbLauncherDir
            |> ensureDirExists
        let version = getVersion settings.CbLauncherDir
        return!
            match productsDir, version with 
            | _, Error msg -> async {
                Log.errorf "Unable to get version: %s" msg
                return 1 }
            | Error msg, _ -> async { 
                Log.errorf "Unable to get products directory: %s" msg
                return 1 }
            | Ok productsDir, Ok (cbVersion, launcherVersion) -> async {
                let cbLauncherName = System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(settings.CbLauncherDir, "EDLaunch.exe")).Name
                use httpClient = createZaonceClient settings.ApiUri cbLauncherName cbVersion (getOsIdent())
                let serverRequest = Server.request httpClient 
                let localTime = DateTime.UtcNow
                let getRemoteTime runningTime = async { 
                    match! Api.getTime localTime (serverRequest runningTime) with
                    | Ok timestamp -> return timestamp
                    | Error (localTimestamp, msg) ->
                        Log.warnf "Couldn't get remote time: %s. Using local system time instead" msg
                        return localTimestamp
                    }
                let! remoteTime = getRemoteTime (fun () -> (double)1)
                let runningTime = fun () ->
                        let runningTime = DateTime.UtcNow.Subtract(localTime);
                        ((double)remoteTime + runningTime.TotalSeconds)
                let serverRequest = serverRequest runningTime
                let! machineId =
#if WINDOWS
                    MachineId.getWindowsId()
#else
                    MachineId.getWineId()
#endif
                let remoteLogHttpClient = if settings.RemoteLogging then Some httpClient else None
                let logEvents = writeEventLog remoteLogHttpClient
                do! logEvents [ EventLog.LogStarted; EventLog.ClientVersion ("app", "path", DateTime.Now) ] // TODO: Check if .Now is correct
                // TODO: Check if launcher version is compatible with current ED version
                
                printInfo settings.Platform productsDir cbVersion launcherVersion remoteTime
                
                match machineId, (getAppSettingsPath cbVersion) with
                | Ok machineId, Ok appSettingsPath ->
                    match! login appSettingsPath serverRequest machineId settings.Platform with
                    | Success user ->
                        // TODO: event log Authenticated user.Name
                        Log.infof "Logged in via %A as: %s (%s)" settings.Platform user.Name user.EmailAddress
                        let authDetails = mapPlatformToAuthDetails user.SessionToken machineId settings.Platform
                        match! Api.getAuthorizedProducts authDetails None serverRequest with
                        | Ok authorizedProducts ->
                            do! logEvents [ EventLog.AvailableProjects (user.EmailAddress, authorizedProducts |> List.map (fun p -> p.Sku)) ]
                            let! products = authorizedProducts
                                            |> List.map (mapProduct productsDir)
                                            |> Api.checkForUpdates user.SessionToken user.MachineToken machineId serverRequest
                            let availableProducts =
                                products
                                |> Result.defaultValue []
                                |> List.map (fun p -> match p with
                                                      | Playable p -> Some (p.Name, "Up to date")
                                                      | RequiresUpdate p -> Some (p.Name, "Requires Update")
                                                      | Missing _ -> None
                                                      | Product.Unknown _ -> None)
                                |> List.choose id
                            Log.infof "Available Products:%s\t%s" Environment.NewLine (String.Join(Environment.NewLine + "\t", availableProducts))
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
                                let processArgs = Product.createArgString settings.DisplayMode gameLanguage user.MachineToken user.SessionToken machineId (runningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile product
                                
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
                    | Ok settings -> run settings
                    | Error msg -> async { Log.error msg; return 1 }
        } |> Async.RunSynchronously
        
        
