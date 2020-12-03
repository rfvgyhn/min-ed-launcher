module EdLauncher.App

open System.IO
open EdLauncher
open EdLauncher.Token
open FSharp.Control.Tasks.NonAffine
open Steam
open System
open System.Diagnostics
open EdLauncher.Types

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

let printInfo (platform: Platform) productsDir cobraVersion launcherVersion =
    Log.info $"""Elite: Dangerous - Minimal Launcher
Platform: %s{platform.Name}
OS: %s{RuntimeInformation.getOsIdent()}
CobraBay Version: %s{cobraVersion}
Launcher Version: %s{launcherVersion}
Products Dir: %s{productsDir}"""
    
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
                Console.Write($"Restarting in %i{remaining} seconds. Press any key to quit.")
                
            let left = Console.CursorLeft
            Console.SetCursorPosition(0, Console.CursorTop)
            if Console.KeyAvailable then
                Console.ReadKey() |> ignore
                Console.WriteLine("Shutting down...".PadRight(left))
                true
            else
                Console.WriteLine("Restarting...".PadRight(left))
                false

        Log.info $"Launching %s{productName}"
        use p = p
        p.WaitForExit()
        Log.info "Shutdown %s{productName}"
        
        if shouldRestart && not (cancelRestart()) then
            launchProduct proton processArgs restart productName product
    | Product.RunResult.AlreadyRunning -> Log.info $"%s{productName} is already running"
    | Product.RunResult.Error e -> Log.error $"Couldn't start selected product: %s{e.ToString()}"
    
let run settings = task {
    let appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments")
    let productsDir =
        Cobra.getProductsDir appDataDir FileIO.hasWriteAccess settings.ForceLocal settings.CbLauncherDir
        |> FileIO.ensureDirExists
    let version = Cobra.getVersion settings.CbLauncherDir
    return!
        match productsDir, version with 
        | _, Error msg -> task {
            Log.error $"Unable to get version: %s{msg}"
            return 1 }
        | Error msg, _ -> task { 
            Log.error $"Unable to get products directory: %s{msg}"
            return 1 }
        | Ok productsDir, Ok (cbVersion, launcherVersion) -> task {
            printInfo settings.Platform productsDir cbVersion launcherVersion
            use httpClient = Api.createClient settings.ApiUri launcherVersion (RuntimeInformation.getOsIdent())
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
            
            match machineId with
            | Ok machineId ->
                let! loginResult, disposable = login runningTime httpClient machineId settings.Platform
                use _ = disposable
                match loginResult with
                | Success connection ->
                    Log.info $"Logged in via %s{settings.Platform.Name} as: %s{connection.Session.Name}"
                    Log.debug "Getting authorized products"
                    match! Api.getAuthorizedProducts settings.Platform None connection with
                    | Ok authorizedProducts ->
                        let names = authorizedProducts |> List.map (fun p -> p.Name)
                        Log.debug $"Authorized Products: %s{String.Join(',', names)}"
                        Log.info "Checking for updates"
                        let! products = authorizedProducts
                                        |> List.map (Product.mapProduct productsDir)
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
                            let gameLanguage = Cobra.getGameLang settings.CbLauncherDir                                 
                            let processArgs() = Product.createArgString settings.DisplayMode gameLanguage connection.Session machineId (runningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile product
                            
                            match Product.validateForRun settings.CbLauncherDir settings.WatchForCrashes product with
                            | Ok p ->
                                let processes = Process.launchProcesses settings.Processes
                                launchProduct settings.Proton processArgs settings.Restart product.Name p
                                Process.stopProcesses processes
                            | Error msg -> Log.error $"Couldn't start selected product: %s{msg}"
                        | None, true -> Log.error "No selected project"
                        | _, _ -> ()
                        
                        if not settings.AutoQuit then
                            printfn "Press any key to quit..."
                            Console.ReadKey() |> ignore
                        
                    | Error msg ->
                        Log.error $"Couldn't get available products: %s{msg}"
                | ActionRequired msg ->
                    Log.error $"Unsupported login action required: %s{msg}"
                | Failure msg ->
                    Log.error $"Couldn't login: %s{msg}"
            | Error msg ->
                Log.error $"Couldn't get machine id: %s{msg}"
            return 0 }
}    