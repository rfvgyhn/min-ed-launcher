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
            | Api.Failed msg -> return Failure msg |> Error }
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
        match! Epic.login launcherVersion details with
        | Ok t ->
            let tokenManager = new RefreshableTokenManager(t, Epic.refreshToken launcherVersion)
            let! result = tokenManager.Get |> Expires |> Ok |> (authenticate (Some tokenManager)) 
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
    |> Option.iter(function
        | Github.Security d ->
            let cves = d.Cves |> String.join ", "
            Log.warn $"Security related launcher update available {currentVersion} -> {d.Details.Version}. Addresses CVE(s) %s{cves}. Download at %s{releasesUrl}"
        | Github.Standard d -> Log.info $"Launcher update available {currentVersion} -> {d.Version}. Download at %s{releasesUrl}"
    )
    
    if release.IsNone then
        Log.debug $"Launcher is latest release {currentVersion}"
}
    
let rec launchProduct dryRun proton processArgs restart productName product =
    let args = processArgs()
    Log.info $"Launching %s{productName}"
    
    match Product.run dryRun proton args product with
    | Product.RunResult.Ok p ->
        use p = p
        p.BeginErrorReadLine()
        p.BeginOutputReadLine()
        p.WaitForExit()
        p.Close()
        Log.info $"Shutdown %s{productName}"
        
        let timeout = restart |> Option.defaultValue 3000L
        if restart.IsSome && not (Console.cancelRestart timeout) then
            launchProduct dryRun proton processArgs restart productName product
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

    let writeHashCache append path hashMap = task { 
        match! Product.writeHashCache append path hashMap with
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
            let write = writeHashCache false
            do! write paths.ProductHashMap productHashes
            do! write paths.CacheHashMap cacheHashes
            return Ok invalidFiles })
        |> TaskResult.bind (downloadFiles downloader paths.ProductCacheDir)
        |> TaskResult.bind (fun files -> task {
            if files.Length > 0 then
                do! files
                    |> Seq.map (fun response ->
                        let trim = paths.ProductCacheDir |> String.ensureEndsWith Path.DirectorySeparatorChar
                        response.FilePath.Replace(trim, ""), response.Hash)
                    |> Map.ofSeq
                    |> writeHashCache true paths.ProductHashMap
                return verifyFiles files
            else
                return Ok 0 }) }
   
type AppError =
    | Version of string
    | ProductsDirectory of string
    | MachineId of string
    | AuthorizedProducts of string
    | Login of LoginError
    | NoSelectedProduct
    | InvalidProductState of string
    
[<RequireQualifiedAccess>]
module AppError =
    let toDisplayString = function
        | Version m -> $"Unable to get version: %s{m}"
        | ProductsDirectory m -> $"Unable to get products directory: %s{m}"
        | MachineId m -> $"Couldn't get machine id: %s{m}"
        | AuthorizedProducts m -> $"Couldn't get available products: %s{m}"
        | Login (ActionRequired m) -> $"Unsupported login action required: %s{m}"
        | Login (Failure m) -> $"Couldn't login: %s{m}"
        | NoSelectedProduct -> "No selected project"
        | InvalidProductState m -> $"Couldn't start selected product: %s{m}"

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
        
    let checkForGameUpdates =
        let getProductDir = Cobra.getProductDir productsDir File.Exists File.ReadAllLines Directory.Exists
        authorizedProducts
        |> List.map (fun p -> Product.mapProduct (getProductDir p.DirectoryName) p)
        |> List.filter (function | Playable _ -> true | _ -> false)
        |> Api.checkForUpdates settings.Platform machineId connection
    let! products = task {
        match! checkForGameUpdates with
        | Ok p -> return p
        | Error e -> Log.warn $"{e}"; return [] }

    Log.info $"Available Products:{Environment.NewLine}\t%s{Console.availableProductsDisplay products}"

    let productsRequiringUpdate = Product.filterByUpdateRequired settings.Platform settings.ForceUpdate products |> List.toArray
    let productsToUpdate =
        let products =
            if settings.AutoUpdate then
                productsRequiringUpdate
            else
                productsRequiringUpdate |> Console.promptForProductsToUpdate
        products
        |> Array.filter (fun p -> p.Metadata.IsNone)
        |> Array.iter (fun p -> Log.error $"Unknown product metadata for %s{p.Name}")
        
        products |> Array.filter (fun p -> p.Metadata.IsSome)

    let! productManifests =
        if productsToUpdate.Length > 0 then
            Log.info "Fetching product manifest(s)"
        
        productsToUpdate
        |> Array.map (fun p ->
            p.Metadata
            |> Option.map (fun m -> Api.getProductManifest httpClient m.RemotePath)
            |> Option.defaultValue (Task.FromResult(Error $"No metadata for %s{p.Name}")))
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
            let productCacheDir = Path.Combine(Environment.cacheDir, $"%s{manifest.Title}%s{manifest.Version}")
            let pathInfo = { ProductDir = productDir
                             ProductCacheDir = productCacheDir
                             CacheHashMap = Path.Combine(productCacheDir, "hashmap.txt")
                             ProductHashMap = Path.Combine(Environment.cacheDir, $"hashmap.%s{Path.GetFileName(productDir)}.txt") }
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
                FileIO.mergeDirectories productDir productCacheDir
                Log.debug $"Moved downloaded files from '%s{Environment.cacheDir}' to '%s{productDir}'"
                Log.info $"Finished updating %s{product.Name}"
                return Some product
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
    let! selectedProduct = 
        if settings.AutoRun then
            playableProducts
            |> Product.selectProduct settings.ProductWhitelist
        else if playableProducts.Length > 0 then
            Console.promptForProductToPlay playableProducts cancellationToken
        else None
        |> Result.requireSome NoSelectedProduct
    
    let! p = Product.validateForRun settings.CbLauncherDir settings.WatchForCrashes selectedProduct |> Result.mapError InvalidProductState
    
    let gameLanguage = Cobra.getGameLang settings.CbLauncherDir settings.PreferredLanguage
    let processArgs() = Product.createArgString settings.DisplayMode gameLanguage connection.Session machineId (getRunningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile selectedProduct
    settings.Processes |> List.iter (fun p -> Log.info $"Starting process %s{p.FileName}")
    let startProcesses, shutdownProcesses =
        if settings.DryRun then
            [], []
        else
            settings.Processes, settings.ShutdownProcesses
    
    if not cancellationToken.IsCancellationRequested then
        let startProcesses = Process.launchProcesses startProcesses
        launchProduct settings.DryRun settings.CompatTool processArgs settings.Restart selectedProduct.Name p
        Process.stopProcesses settings.ShutdownTimeout startProcesses
        settings.ShutdownProcesses |> List.iter (fun p -> Log.info $"Starting process %s{p.FileName}")
        Process.launchProcesses shutdownProcesses |> Process.writeOutput
    return 0
} 