module MinEdLauncher.App

open System.IO
open System.Runtime.InteropServices
open System.Threading
open MinEdLauncher
open MinEdLauncher.Http
open MinEdLauncher.Token
open System
open System.Diagnostics
open System.Threading.Tasks
open MinEdLauncher.Types
open FsToolkit.ErrorHandling

type LoginError =
| ActionRequired of string
| CouldntConfirmOwnership of Platform
| Failure of string
let login launcherVersion runningTime httpClient machineId (platform: Platform) lang =
    let authenticate disposable = function
        | Ok authToken -> task {
            Log.debug $"Authenticating via %s{platform.Name}"
            match! Api.authenticate runningTime authToken platform machineId lang httpClient with
            | Api.Authorized connection ->
                Log.debug "Successfully authenticated"
                let connection = disposable |> Option.map connection.WithResource |> Option.defaultValue connection
                return Ok connection
            | Api.RegistrationRequired uri -> return ActionRequired <| $"Registration is required at %A{uri}" |> Error
            | Api.LinkAvailable uri -> return ActionRequired <| $"Link available at %A{uri}" |> Error
            | Api.Denied msg -> return Failure msg |> Error
            | Api.Failed msg -> return Failure msg |> Error
            | Api.CouldntConfirmOwnership -> return CouldntConfirmOwnership platform |> Error
            }
        | Error msg -> Failure msg |> Error |> Task.fromResult
        
    match platform with
    | Oculus _ -> Failure "Oculus not supported" |> Error |> Task.fromResult
    | Dev -> task {
        let! result = Permanent "DevAuthToken" |> Ok |> (authenticate None)
        return result }
    | Frontier details -> task {
        let credPath = Settings.frontierCredPath details.Profile
        let loginRequest: Api.LoginRequest =
            { RunningTime = runningTime
              HttpClient = httpClient
              Details = details
              MachineId = machineId
              Lang = lang
              SaveCredentials = Cobra.saveCredentials credPath
              GetTwoFactor = Console.promptTwoFactorCode
              GetUserPass = Console.promptUserPass }
        let! token = Api.login loginRequest
        match token with
        | Ok (username, password, token) ->
            let! result = PasswordBased { Username = username; Password = password; Token = token } |> Ok |> (authenticate None)
            return result
        | Error msg ->
            let! _ = Cobra.discardToken credPath
            return Failure msg |> Error }
    | Steam -> task {
        use steam = new Steam.Steam()
        let! result = steam.Login() |> Result.map (fun steamUser -> Permanent steamUser.SessionToken) |> (authenticate None)
        return result }
    | Epic details -> task {
        match! Epic.loginWithCode launcherVersion details.ExchangeCode with
        | Ok t ->
            let loginViaLegendary() = Legendary.getAccessToken() |> Result.bindTask (Epic.loginWithExistingToken launcherVersion)
            let tokenManager = new RefreshableTokenManager(t, Epic.refreshToken launcherVersion, loginViaLegendary)
            let! result = {| Get = tokenManager.Get; Renew = tokenManager.Renew |} |> Expires |> Ok |> (authenticate (Some tokenManager)) 
            return result
        | Error msg -> return Failure msg |> Error }

let printInfo (platform: Platform) productsDir cobraVersion =
    Log.info $"""Elite Runtime
    Platform: %s{platform.Name}
    CobraBay Version: %s{cobraVersion}
    Products Dir: %s{productsDir}"""
    
let private checkForLauncherUpdates httpClient cancellationToken currentVersion = task {
    let releasesUrl = "https://github.com/rfvgyhn/min-ed-launcher/releases"
    let! release = Github.getUpdatedLauncher currentVersion httpClient cancellationToken
    release
    |> Result.teeError(fun e ->
        Log.warn "Failed to check for launcher updates"
        Log.debug $"Failed to check for launcher updates. {e}"
    )
    |> Result.iter(fun release ->
        release
        |> Option.iter(function
            | Github.Security d ->
                let cves = d.Cves |> String.join ", "
                Log.warn $"Security related launcher update available {currentVersion} -> {d.Details.Version}. Addresses CVE(s) %s{cves}. Download at %s{releasesUrl}"
            | Github.Standard d -> Log.info $"Launcher update available {currentVersion} -> {d.Version}. Download at %s{releasesUrl}"
        )
        
        if release.IsNone then
            Log.debug $"Launcher is latest release {currentVersion}"
    )
}
    
let launchProduct dryRun proton processArgs productName waitForExit product =
    let args = processArgs()
    Log.info $"Launching %s{productName}"
    
    match Product.run dryRun proton args product with
    | Product.RunResult.Ok p ->
        p.BeginErrorReadLine()
        p.BeginOutputReadLine()
        if waitForExit then
            use p = p
            p.WaitForExit()
            p.Close()
            Log.info $"Shutdown %s{productName}"
    | Product.DryRun p ->
        Console.WriteLine("\tDry run")
        Console.WriteLine($"\t{p.FileName} {p.Arguments}")
    | Product.RunResult.AlreadyRunning -> Log.info $"%s{productName} is already running"
    | Product.RunResult.Error e -> Log.error $"Couldn't start selected product: %s{e.ToString()}"

let throttledAction (semaphore: SemaphoreSlim) (action: 'a -> Task<'b>) input =
    input
    |> Array.map (fun item -> task {
        do! semaphore.WaitAsync()
        try
             return! action(item)
        finally
            semaphore.Release() |> ignore
        })
    |> Task.whenAll

type UpdateProductPaths = { ProductDir: string; ProductCacheDir: string; CacheHashMap: string; ProductHashMap: string }
let updateProduct downloader (hashProgress: ISampledProgress<int>) (cacheProgress: int -> IProgress<int>) paths (manifest: Types.ProductManifest.File[]) = task {
    let manifestMap =
        manifest
        |> Array.map (fun file -> Product.normalizeManifestPartialPath file.Path, file)
        |> Map.ofArray
    
    let tryGenHash file =
        Product.generateFileHashStr Product.hashFile file
        |> Result.teeError (fun e -> Log.warn $"Unable to get hash of file '%s{file}' - %s{e.ToString()}")
        |> Result.toOption

    let verifyFiles (files: Http.FileDownloadResponse[]) =
        let invalidFiles = files |> Seq.filter (fun file -> file.Integrity = Http.Invalid) |> Seq.map (fun file -> file.FilePath)
        if Seq.isEmpty invalidFiles then Ok files.Length
        else invalidFiles |> String.join Environment.NewLine |> Error

    let writeHashCache path hashMap = task { 
        match! Product.writeHashCache false path hashMap with
        | Ok () -> Log.debug $"Wrote hash cache to '%s{path}'"
        | Error e -> Log.warn $"Unable to write hash cache at '%s{path}' - %s{e}" }
    
    let checkDownloadCache getFileHashes (createProgress: int -> IProgress<int>) cacheHashMap cacheDir =
        let downloadedFiles = Directory.EnumerateFiles(cacheDir, "*.*", SearchOption.AllDirectories) |> Seq.toList
        let m = downloadedFiles
                |> List.map (fun f -> f.Substring(cacheDir.Length + 1))
                |> List.filter (fun f -> f <> "hashmap.txt") |> Set.ofList
        let totalFiles = m.Count
        let p = createProgress totalFiles
        let hashes = getFileHashes p m cacheHashMap cacheDir (downloadedFiles :> string seq)
        if p :? ISampledProgress<int> then (p :?> ISampledProgress<int>).Flush()
        hashes
    
    let checkProductFiles getFileHashes (progress: IProgress<int>) productHashMap validCachedHashes =
        let manifestKeys = manifestMap |> Map.keys
        let productHashMap = productHashMap |> Map.filter (fun path _ -> File.Exists(Path.Combine(paths.ProductDir, path)))
        let productHashes =
            manifestKeys
            |> Seq.except (validCachedHashes |> Map.keys)
            |> Seq.map (fun path -> Path.Combine(paths.ProductDir, path))
            |> getFileHashes progress manifestKeys productHashMap paths.ProductDir
            |> Map.merge validCachedHashes
        if progress :? ISampledProgress<int> then (progress :?> ISampledProgress<int>).Flush()
        let invalidFiles =
            manifestKeys
            |> Set.filter (fun file ->
                productHashes
                |> Map.tryFind file
                |> Option.filter (fun hash -> manifestMap.[file].Hash = hash)
                |> Option.isNone)
            |> Seq.map (fun file -> Map.find file manifestMap)
            |> Seq.toArray
        invalidFiles, productHashes, validCachedHashes
        
    let downloadFiles downloader cacheDir (files: Types.ProductManifest.File[]) =
        if files.Length > 0 then
            Log.info $"Downloading %d{files.Length} files"
            Product.downloadFiles downloader cacheDir files
        else
            Log.info "All files already up to date"
            [||] |> Ok |> Task.fromResult
            
    let ensureDiskSpace dir (files: Types.ProductManifest.File[]) =
        let totalSize = files |> Array.sumBy(fun f -> int64 f.Size)
        if FileIO.hasEnoughDiskSpace totalSize dir then
            Ok files
        else
            Error $"Not enough disk space available (%i{totalSize / (1024L * 1024L)}MB) at %s{dir}"

    let! cacheHashes =
        Product.parseHashCache paths.CacheHashMap
        |> TaskResult.teeError (fun e -> Log.warn $"Unable to parse hash map at '%s{paths.CacheHashMap}' - %s{e}")
        |> TaskResult.defaultValue Map.empty
        
    let! productHashes =
        Product.parseHashCache paths.ProductHashMap
        |> TaskResult.teeError (fun e -> Log.warn $"Unable to parse hash map at '%s{paths.ProductHashMap}' - %s{e}")
        |> TaskResult.defaultValue Map.empty
    let getFileHashes = Product.getFileHashes tryGenHash File.Exists
    
    return!
        paths.ProductCacheDir
        |> FileIO.ensureDirExists
        |> Result.tee (fun _ -> Log.info "Determining which files need to be updated")
        |> Result.tee (fun _ -> Log.info "Checking download cache")
        |> Result.map (checkDownloadCache getFileHashes cacheProgress cacheHashes)
        |> Result.tee (fun _ -> Log.info "Checking existing files")
        |> Result.map (checkProductFiles getFileHashes hashProgress productHashes)
        |> Result.bindTask (fun (invalidFiles, productHashes, cacheHashes) -> task {
            do! writeHashCache paths.ProductHashMap productHashes
            do! writeHashCache paths.CacheHashMap cacheHashes
            return Ok invalidFiles })
        |> TaskResult.bind (fun files -> ensureDiskSpace paths.ProductCacheDir files |> Task.fromResult)
        |> TaskResult.bind (downloadFiles downloader paths.ProductCacheDir)
        |> TaskResult.bind (fun files -> task {
            if files.Length > 0 then
                do! files
                    |> Seq.map (fun response ->
                        let trim = paths.ProductCacheDir |> String.ensureEndsWith Path.DirectorySeparatorChar
                        response.FilePath.Replace(trim, ""), response.Hash)
                    |> Map.ofSeq
                    |> Map.merge productHashes
                    |> writeHashCache paths.ProductHashMap
                return verifyFiles files
            else
                return Ok 0 }) }
   
type AppError =
    | Version of string
    | ProductsDirectory of string
    | CacheDirectory of string
    | MachineId of string
    | AuthorizedProducts of string
    | Login of LoginError
    | NoSelectedProduct
    | InvalidProductState of string
    | InvalidSession of string
    
[<RequireQualifiedAccess>]
module AppError =
    let toDisplayString = function
        | Version m -> $"Unable to get version: %s{m}"
        | ProductsDirectory m -> $"Unable to get products directory: %s{m}"
        | CacheDirectory m -> $"Unable to get cache directory: %s{m}"
        | MachineId m -> $"Couldn't get machine id: %s{m}"
        | AuthorizedProducts m -> $"Couldn't get available products: %s{m}"
        | Login (ActionRequired m) -> $"Unsupported login action required: %s{m}"
        | Login (Failure m) -> $"Couldn't login: %s{m}"
        | Login (CouldntConfirmOwnership platform) ->
            let possibleFixes =
                [
                    $"Ensure you've linked your {platform.Name} account to your Frontier account. https://user.frontierstore.net/user/info"
                    if platform = Steam then
                        "Restart Steam"
                        "Log out and log back in to Steam"
                        "Restart your computer"
                    "Wait a minute or two and retry"
                    "Wait longer"
                ]
                |> List.map (fun s -> "    " + s)
                |> String.join Environment.NewLine
            $"Frontier was unable to verify that you own the game. This happens intermittently. Possible fixes include:{Environment.NewLine}{possibleFixes}"
        | NoSelectedProduct -> "No selected project"
        | InvalidProductState m -> $"Couldn't start selected product: %s{m}"
        | InvalidSession m -> $"Invalid session: %s{m}"

let private createGetRunningTime httpClient = task {
    let localTime = DateTime.UtcNow
    Log.debug("Getting remote time")
    let! remoteTime = task {
        match! Api.getTime localTime httpClient with
        | Ok timestamp -> return timestamp
        | Error (localTimestamp, msg) ->
            Log.warn $"Couldn't get remote time: %s{msg}. Using local system time instead"
            return localTimestamp
    }
    return fun () ->
        let runningTime = DateTime.UtcNow.Subtract(localTime);
        (double remoteTime + runningTime.TotalSeconds)
}

let private renewEpicTokenIfNeeded platform token =
    match platform, token with
    | Epic _, Expires t -> t.Renew()
    | _ -> Ok () |> Task.fromResult
    |> TaskResult.mapError InvalidSession

let rec private launchLoop initialLaunch settings playableProducts (session: EdSession) persistentRunning relaunchRunning cancellationToken processArgs = taskResult {      
    let! selectedProduct = 
        if settings.AutoRun && initialLaunch then
            playableProducts
            |> Product.selectProduct settings.ProductWhitelist
            |> Option.map Console.ProductSelection.Product
        else if playableProducts.Length > 0 then
            Console.promptForProductToPlay playableProducts cancellationToken
        else None
        |> Result.requireSome NoSelectedProduct
    let didLoop = not initialLaunch
    
    match selectedProduct with
    | Console.ProductSelection.Exit ->
        return (persistentRunning |> Option.defaultValue []) @ (relaunchRunning |> Option.defaultValue []), didLoop
    | Console.ProductSelection.Product selectedProduct ->
        let! p = Product.validateForRun settings.CbLauncherDir settings.WatchForCrashes selectedProduct |> Result.mapError InvalidProductState
        let pArgs() = processArgs selectedProduct
        let logStart (ps: ProcessStartInfo list) = ps |> List.iter (fun p -> Log.info $"Starting process %s{p.FileName}")        
        let persistentStartInfos, relaunchStartInfos =
            let relaunchProcesses = settings.Processes |> List.filter _.RestartOnRelaunch |> List.map _.Info
            match persistentRunning with
            | Some _ ->
                relaunchProcesses |> logStart
                [], relaunchProcesses
            | None ->
                settings.Processes |> List.map _.Info |> logStart
                
                if settings.DryRun then
                    [], []
                else
                    settings.Processes |> List.filter (fun p -> not p.RestartOnRelaunch) |> List.map _.Info, relaunchProcesses
        
        if not initialLaunch then
            do! renewEpicTokenIfNeeded settings.Platform session.PlatformToken
        
        let persistentProcesses = persistentRunning |> Option.defaultWith (fun () -> Process.launchProcesses false persistentStartInfos)
        let mutable relaunchProcesses = Process.launchProcesses false relaunchStartInfos
        
        if initialLaunch && settings.GameStartDelay > TimeSpan.Zero then
            Log.info $"Delaying game launch for %.2f{settings.GameStartDelay.TotalSeconds} seconds"
            do! Task.Delay settings.GameStartDelay
        
        let waitForEdExit =
            settings.QuitMode = WaitForExit
            || settings.QuitMode = WaitForInput
            || settings.Restart.IsSome
            || not settings.Processes.IsEmpty
            || not settings.ShutdownProcesses.IsEmpty
        
        launchProduct settings.DryRun settings.CompatTool pArgs selectedProduct.Name waitForEdExit p
        
        if not waitForEdExit then
            // Wait for ED Process to start before exiting
            let maxTries = 30
            let mutable tries = 0
            while tries < maxTries do
                if not (Product.isRunning p) then
                    do! Task.Delay(TimeSpan.FromSeconds(1))
                    tries <- tries + 1
                else
                    tries <- maxTries
            return [], didLoop
        else
            let timeout = settings.Restart |> Option.defaultValue 3000L
            while settings.Restart.IsSome && not (Console.cancelRestart timeout) do
                Process.stopProcesses settings.ShutdownTimeout relaunchProcesses
                logStart relaunchStartInfos
                relaunchProcesses <- Process.launchProcesses false relaunchStartInfos
                
                do! renewEpicTokenIfNeeded settings.Platform session.PlatformToken
                
                launchProduct settings.DryRun settings.CompatTool pArgs selectedProduct.Name true p
            
            if settings.QuitMode = WaitForInput then
                return! launchLoop false settings playableProducts session (Some persistentProcesses) (Some relaunchProcesses) cancellationToken processArgs
            else
                return persistentProcesses @ relaunchProcesses, didLoop
}

let run settings launcherVersion cancellationToken = taskResult {
    if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && settings.Platform = Steam then
        Steam.fixLcAll()
    
    let appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments")
    let! productsDir =
        Cobra.getDefaultProductsDir appDataDir FileIO.hasWriteAccess settings.ForceLocal settings.CbLauncherDir
        |> FileIO.ensureDirExists
        |> Result.mapError ProductsDirectory
    let! cbVersion = Cobra.getVersion settings.CbLauncherDir |> Result.mapError Version

    printInfo settings.Platform productsDir cbVersion
    use httpClient = Api.createClient settings.ApiUri launcherVersion
    
    Log.debug("Getting machine id")
    let! machineId =
        MachineId.getId()
        |> TaskResult.mapError MachineId

    let lang = settings.PreferredLanguage |> Option.defaultValue "en"
    
    Log.info("Logging in")
    let! getRunningTime = createGetRunningTime httpClient
    use! connection = login launcherVersion getRunningTime httpClient machineId settings.Platform lang |> TaskResult.mapError Login
    Log.info $"Logged in via %s{settings.Platform.Name} as: %s{connection.Session.Name}"
    
    Log.debug "Getting authorized products"    
    let applyFixes = AuthorizedProduct.fixDirectoryName productsDir settings.Platform Directory.Exists File.Exists
                     >> AuthorizedProduct.fixFilters settings.FilterOverrides
    
    let! authorizedProducts =
        Api.getAuthorizedProducts settings.Platform None connection
        |> TaskResult.mapError AuthorizedProducts
        |> TaskResult.map (fun p ->
            if settings.AdditionalProducts.Length > 0 then
                Log.debug $"Appending %i{settings.AdditionalProducts.Length} product(s) from settings file"
            p |> List.append settings.AdditionalProducts
              |> List.map applyFixes)
    
    let names = authorizedProducts |> List.map (fun p -> p.Name) |> String.join ","
    Log.debug $"Authorized Products: %s{names}"
    Log.info "Checking for updates"
    
    if settings.CheckForLauncherUpdates then
        do! checkForLauncherUpdates httpClient cancellationToken (Version.Parse(launcherVersion.Split([|'+'; '-'|])[0]))
    
    let! cacheDir = settings.CacheDir |> FileIO.ensureDirExists |> Result.mapError CacheDirectory    
    let! products =
        let getProductDir = Cobra.getProductDir productsDir File.Exists File.ReadAllLines Directory.Exists
        authorizedProducts
        |> List.map (fun p -> Product.mapProduct (getProductDir p.DirectoryName) p)
        |> List.filter (fun p -> p.IsPlayable || p.IsMissing)
        |> Api.checkForUpdates settings.Platform machineId connection cacheDir
        |> TaskResult.bind (Product.cacheManifestHash cacheDir)
        |> TaskResult.teeError Log.warn
        |> TaskResult.defaultValue List.empty
        |> Task.bind (Api.checkForStealthUpdate connection.HttpClient)

    Log.info $"Available Products:{Environment.NewLine}\t%s{Console.availableProductsDisplay products}"

    let manifestMap =
        products
        |> List.choose (function RequiresStealthUpdate (d, m) -> Some (d.Sku, m) | _ -> None)
        |> List.tee (fun (sku, _) -> Log.debug $"{sku} has a stealth update")
        |> Map.ofList

    let missingToInstall =
        let missing =
            products
            |> Product.filterByMissing
            |> Product.filterByUpdateable settings.Platform settings.ForceUpdate
            |> List.toArray
        if settings.AutoRun then
            let preferredProduct =
                products
                |> Product.filterByUpdateable settings.Platform settings.ForceUpdate
                |> List.toArray
                |> Product.selectProduct settings.ProductWhitelist
            Product.selectProduct settings.ProductWhitelist missing
            |> Option.filter(fun missing -> preferredProduct |> Option.exists(fun preferred -> missing = preferred))
            |> Option.map(fun p -> [| p |])
            |> Option.defaultWith(fun () -> [||])
        else if not settings.SkipInstallPrompt then
            missing |> Console.promptForProductsToUpdate "install"
        else
            [||]
    let productsRequiringUpdate =
        products
        |> Product.filterByUpdateRequired
        |> Product.filterByUpdateable settings.Platform settings.ForceUpdate
        |> List.toArray
    let productsToUpdate =
        let products =
            if settings.AutoUpdate then
                productsRequiringUpdate
            else
                productsRequiringUpdate |> Console.promptForProductsToUpdate "update"
            |> Array.append missingToInstall
        products
        |> Array.filter (_.Metadata.IsNone)
        |> Array.iter (fun p -> Log.error $"Unknown product metadata for %s{p.Name}")
        
        products |> Array.filter (_.Metadata.IsSome)

    let! productManifests =
        if productsToUpdate.Length > 0 then
            Log.info "Fetching product manifest(s)"
        
        productsToUpdate
        |> Array.map (fun p ->
            manifestMap
            |> Map.tryFind p.Sku
            |> Option.flatten
            |> Option.map (Result.Ok >> Task.fromResult)
            |> Option.defaultWith (fun () ->
                p.Metadata
                |> Option.map (fun m -> Api.getProductManifest httpClient m.RemotePath)
                |> Option.defaultValue (Task.FromResult(Error $"No metadata for %s{p.Name}"))
            )
        )
        |> Task.whenAll
    let productsToUpdate, failedManifests =
        productManifests
        |> Array.zip productsToUpdate
        |> Array.fold (fun (success, failed) (product, manifest) ->
            match manifest with
            | Ok m -> ((product, m) :: success, failed)
            | Error e -> (success, (product.Name, e) :: failed)) ([], [])
        
    if not failedManifests.IsEmpty then
        let separator = $"{Environment.NewLine}\t"
        let messages = failedManifests |> List.map (fun (name, error) -> $"%s{name} - %s{error}") |> String.join separator
        Log.error $"Unable to update the following products. Failed to get their manifests:%s{separator}%s{messages}"
    
    let! updated =
        let productsDir = Path.Combine(settings.CbLauncherDir, "Products")
        
        productsToUpdate
        |> List.chooseTasksSequential (fun (product, manifest) -> task {
            Log.info $"Updating %s{product.Name}"
            let productDir = Path.Combine(productsDir, product.Directory)
            let productCacheDir = Path.Combine(cacheDir, $"%s{manifest.Title}%s{manifest.Version}")
            let pathInfo = { ProductDir = productDir
                             ProductCacheDir = productCacheDir
                             CacheHashMap = Path.Combine(productCacheDir, "hashmap.txt")
                             ProductHashMap = Path.Combine(cacheDir, $"hashmap.%s{Path.GetFileName(productDir)}.txt") }
            let barLength = 30
            let downloadProgress = Console.Progress(Console.productDownloadIndicator barLength)              
            let totalFiles = manifest.Files.Length
            let digits = Int.digitCount totalFiles
            let hashProgress = Console.Progress(Console.productHashIndicator barLength digits totalFiles)
            let cacheProgress totalFiles : IProgress<int> =
                Console.Progress(Console.productHashIndicator barLength (Int.digitCount totalFiles) totalFiles)
            use semaphore = new SemaphoreSlim(settings.MaxConcurrentDownloads, settings.MaxConcurrentDownloads)
            let throttled progress = throttledAction semaphore (downloadFile httpClient Product.createHashAlgorithm cancellationToken progress)
            let downloader = { Download = throttled; Progress = downloadProgress }
            Console.CursorVisible <- false
            match! updateProduct downloader hashProgress cacheProgress pathInfo manifest.Files with
            | Ok 0 -> return Some product
            | Ok _ ->
                Console.CursorVisible <- true
                File.Delete(pathInfo.CacheHashMap)
                Log.info $"Moving downloaded files from '%s{cacheDir}' to '%s{productDir}'"
                FileIO.mergeDirectories productDir productCacheDir                
                Log.info $"Finished updating %s{product.Name}"
                
                return
                    if product.VInfo = VersionInfo.Empty then
                        match Product.readVersionInfo productDir with
                        | Product.VersionInfoStatus.Found v -> Some { product with VInfo = v }
                        | _ -> Some product
                    else
                        Some product
            | Error e ->
                Log.error $"Unable to download update for %s{product.Name} - %s{e}"
                return None
            })
    
    let playableProducts =
        products
        |> List.choose (fun p -> match p with | Playable p -> Some p | _ -> None)
        |> List.append updated
        |> List.sortBy (fun p -> p.SortKey)
        |> List.toArray
    
    let gameLanguage = Cobra.getGameLang settings.CbLauncherDir settings.PreferredLanguage
    let processArgs = Product.createArgString settings.DisplayMode gameLanguage connection.Session machineId (getRunningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile
    
    let! runningProcesses, didLoop = launchLoop true settings playableProducts connection.Session None None cancellationToken processArgs
    
    if settings.ShutdownDelay > TimeSpan.Zero then
        Log.info $"Delaying shutdown for %.2f{settings.ShutdownDelay.TotalSeconds} seconds"
        do! Task.Delay settings.ShutdownDelay
        
    let processesToStop =
        runningProcesses
        |> List.filter (fun p ->
           settings.Processes
           |> List.tryFind (fun s -> s.Info.FileName = p.StartInfo.FileName && s.KeepOpen = false)
           |> Option.isSome) 
        
    Process.stopProcesses settings.ShutdownTimeout processesToStop
    settings.ShutdownProcesses |> List.iter (fun p -> Log.info $"Starting process %s{p.FileName}")
    Process.launchProcesses true settings.ShutdownProcesses |> Process.waitForExit
    
    return didLoop
} 