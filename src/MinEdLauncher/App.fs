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
        let promptTwoFactorCode email =
            printf $"Enter verification code that was sent to %s{email}: "
            Console.ReadLine()
        let promptUserPass() =
            printfn "Enter Frontier credentials"
            printf "Username (Email): "
            let username = Console.ReadLine()
            printf "Password: "
            let password = Console.readPassword() |> Cobra.encrypt |> Result.defaultValue ""
            username, password
        let credPath = Settings.frontierCredPath details.Profile
        let! token = Api.login runningTime httpClient details machineId lang (Cobra.saveCredentials credPath) promptTwoFactorCode promptUserPass
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
    
let rec launchProduct proton processArgs restart productName product =
    let args = processArgs()
    Log.info $"Launching %s{productName}"
    
    match Product.run proton args product with
    | Product.RunResult.Ok p ->
        let timeout = restart |> Option.defaultValue 3000L
        let cancelRestart() =
            let interval = 250
            let stopwatch = Stopwatch()
            Console.consumeAvailableKeys()
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

        use p = p
        p.WaitForExit()
        Log.info $"Shutdown %s{productName}"
        
        if restart.IsSome && not (cancelRestart()) then
            launchProduct proton processArgs restart productName product
    | Product.RunResult.AlreadyRunning -> Log.info $"%s{productName} is already running"
    | Product.RunResult.Error e -> Log.error $"Couldn't start selected product: %s{e.ToString()}"
  
let promptForProductToPlay (products: ProductDetails array) (cancellationToken:CancellationToken) =
    printfn $"Select a product to launch (default=1):"
    products
    |> Array.indexed
    |> Array.iter (fun (i, product) -> printfn $"%i{i + 1}) %s{product.Name}")
        
    let rec readInput() =
        printf "Product: "
        let userInput = Console.ReadKey(true)
        printfn ""
        let couldParse, index =
            if userInput.Key = ConsoleKey.Enter then
                true, 1
            else
                Int32.TryParse(userInput.KeyChar.ToString())
        if cancellationToken.IsCancellationRequested then
            None
        else if couldParse && index > 0 && index < products.Length then
            let product = products.[index - 1]
            let filters = String.Join(", ", product.Filters)
            Log.debug $"User selected %s{product.Name} - %s{product.Sku} - %s{filters}"
            products.[index - 1] |> Some
        else
            printfn "Invalid selection"
            readInput()
    readInput()
    
let promptForProductsToUpdate (products: ProductDetails array) =
    if products.Length > 0 then
        printfn $"Select product(s) to update (eg: \"1\", \"1 2 3\") (default=None):"
        products
        |> Array.indexed
        |> Array.iter (fun (i, product) -> printfn $"%i{i + 1}) %s{product.Name}")
            
        let rec readInput() =
            let userInput = Console.ReadLine()
            
            if String.IsNullOrWhiteSpace(userInput) then
                [||]
            else
                let selection =
                    userInput
                    |> Regex.split @"\D+"
                    |> Array.choose (fun d ->
                        if String.IsNullOrEmpty(d) then
                            None
                        else
                            match Int32.Parse(d) with
                            | n when n > 0 && n < products.Length -> Some n
                            | _ -> None)
                    |> Array.map (fun i -> products.[i - 1])
                if selection.Length > 0 then
                    selection
                else
                    printfn "Invalid selection"
                    readInput()
        readInput()
    else
        [||]

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
let updateProduct downloader hashProgress paths (manifest: Types.ProductManifest.File[]) = task {
    let manifestMap =
        manifest
        |> Array.map (fun file -> Product.normalizeManifestPartialPath file.Path, file)
        |> Map.ofArray
    
    let tryGenHash file =
        match Product.generateFileHashStr Product.hashFile file with
        | Ok hash -> Some hash
        | Error e ->
           Log.warn $"Unable to get hash of file '%s{file}' - %s{e.ToString()}"
           None

    let verifyFiles (files: Http.FileDownloadResponse[]) =
        let invalidFiles = files |> Seq.filter (fun file -> file.Integrity = Http.Invalid) |> Seq.map (fun file -> file.FilePath)
        if Seq.isEmpty invalidFiles then Ok files.Length
        else invalidFiles |> String.join Environment.NewLine |> Error

    let writeHashCache append path hashMap = task { 
        match! Product.writeHashCache append path hashMap with
        | Ok () -> Log.debug $"Wrote hash cache to '%s{path}'"
        | Error e -> Log.warn $"Unable to write hash cache at '%s{path}' - %s{e}" }
    
    let getFileHashes = Product.getFileHashes hashProgress tryGenHash File.Exists (manifestMap |> Map.keys)
    let processFiles productHashMap cacheHashMap =
        paths.ProductCacheDir
        |> FileIO.ensureDirExists
        |> Result.map (fun cacheDir ->
            Log.info "Determining which files need to be updated..."
            let validCachedHashes =
                getFileHashes cacheHashMap cacheDir (Directory.EnumerateFiles(cacheDir, "*.*", SearchOption.AllDirectories))
                |> Map.filter (fun file hash -> manifestMap.[file].Hash = hash)
            let manifestKeys = manifestMap |> Map.keys
            let productHashMap = productHashMap |> Map.filter (fun path _ -> File.Exists(Path.Combine(paths.ProductDir, path)))
            let productHashes =
                manifestKeys
                |> Seq.except (validCachedHashes |> Map.keys)
                |> Seq.map (fun path -> Path.Combine(paths.ProductDir, path))
                |> getFileHashes productHashMap paths.ProductDir
                |> Map.merge validCachedHashes 
            let invalidFiles =
                manifestKeys
                |> Set.filter (fun file ->
                    productHashes
                    |> Map.tryFind file
                    |> Option.filter (fun hash -> manifestMap.[file].Hash = hash)
                    |> Option.isNone)
                |> Seq.map (fun file -> Map.find file manifestMap)
                |> Seq.toArray
            invalidFiles, productHashes, validCachedHashes)
    
    let downloadFiles downloader cacheDir (files: Types.ProductManifest.File[]) =
        if files.Length > 0 then
            Log.info $"Downloading %d{files.Length} files"
            Product.downloadFiles downloader cacheDir files
        else
            Log.info "All files already up to date"
            [||] |> Ok |> Task.fromResult

    let! cacheHashes = task {
        match! Product.parseHashCache paths.CacheHashMap with
        | Ok hashes -> return hashes
        | Error e ->
            Log.warn $"Unable to parse hash map at '%s{paths.CacheHashMap}' - %s{e}"
            return Map.empty }
    let! productHashes = task {
        match! Product.parseHashCache paths.ProductHashMap with
        | Ok hashes -> return hashes
        | Error e ->
            Log.warn $"Unable to parse hash map at '%s{paths.ProductHashMap}' - %s{e}"
            return Map.empty }
    
    return!
        processFiles productHashes cacheHashes
        |> Result.bindTask (fun (invalidFiles, productHashes, cacheHashes) -> task {
            let write = writeHashCache false
            do! write paths.ProductHashMap productHashes
            do! write paths.CacheHashMap cacheHashes
            return Ok invalidFiles })
        |> Task.bindTaskResult (downloadFiles downloader paths.ProductCacheDir)
        |> Task.bindTaskResult (fun files -> task {
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
#if WINDOWS
        MachineId.getWindowsId() |> Task.fromResult
#else
        MachineId.getWineId()
#endif
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
    
    let names = authorizedProducts |> List.map (fun p -> p.Name)
    Log.debug $"Authorized Products: %s{String.Join(',', names)}"
    Log.info "Checking for updates"
    let checkForUpdates =
        let getProductDir = Cobra.getProductDir productsDir File.Exists File.ReadAllLines Directory.Exists
        authorizedProducts
        |> List.map (fun p -> Product.mapProduct (getProductDir p.DirectoryName) p)
        |> List.filter (function | Playable _ -> true | _ -> false)
        |> Api.checkForUpdates settings.Platform machineId connection
    let! products = task {
        match! checkForUpdates with
        | Ok p -> return p
        | Error e -> Log.warn $"{e}"; return [] }

    let availableProductsDisplay =
        let max (f: ProductDetails -> string) =
            if products.Length = 0 then
                0
            else
                products
                |> List.map (function | Playable p | RequiresUpdate p -> (f(p)).Length | _ -> 0)
                |> List.max
        let maxName = max (fun p -> p.Name)
        let maxSku = max (fun p -> p.Sku)
        let map msg (p: ProductDetails) = $"{p.Name.PadRight(maxName)} {p.Sku.PadRight(maxSku)} %s{msg}" 
        let availableProducts =
            products
            |> List.choose (function | Playable p -> map "Up to Date" p |> Some
                                     | RequiresUpdate p -> map "Requires Update" p |> Some
                                     | Missing _ | Product.Unknown _ -> None)
        match availableProducts with
        | [] -> "None"
        | p -> String.Join(Environment.NewLine + "\t", p)
    Log.info $"Available Products:{Environment.NewLine}\t%s{availableProductsDisplay}"

    let productsRequiringUpdate = Product.filterByUpdateRequired settings.Platform settings.ForceUpdate products |> List.toArray
    let productsToUpdate =
        let products =
            if settings.AutoUpdate then
                productsRequiringUpdate
            else
                productsRequiringUpdate |> promptForProductsToUpdate
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
            let mutable lastProgress = 0.
            let barLength = 30
            let downloadProgress = Progress<DownloadProgress>(fun p ->
                let completed = p.BytesSoFar = p.TotalBytes
                if p.Elapsed.TotalMilliseconds - lastProgress >= 200. || completed then
                    lastProgress <- p.Elapsed.TotalMilliseconds
                    let total = p.TotalBytes |> Int64.toFriendlyByteString
                    let speed =  (p.BytesSoFar / (int64 p.Elapsed.TotalMilliseconds) * 1000L |> Int64.toFriendlyByteString).PadLeft(6)
                    let percent = float p.BytesSoFar / float p.TotalBytes
                    let blocks = int (float barLength * percent)
                    let bar = String.replicate blocks "#" + String.replicate (barLength - blocks) "-"
                    Console.Write($"\r\tDownloading %s{total} %s{speed}/s [%s{bar}] {percent:P0}")
                    if completed then Console.WriteLine()) :> IProgress<DownloadProgress>                    
            let totalFiles = manifest.Files.Length
            let digits = Math.Floor(Math.Log10(if totalFiles = 0 then 1 else totalFiles) + 1.) |> int
            let hashProgress = Progress<int>(fun p ->
                let percent = float p / float totalFiles
                let blocks = int (float barLength * percent)
                let bar = String.replicate blocks "#" + String.replicate (barLength - blocks) "-"
                let file = p.ToString().PadLeft(digits)
                Console.Write($"\r\tChecking file %s{file} of %i{totalFiles} [%s{bar}] {percent:P0}")
                if p = totalFiles then Console.WriteLine())
            use semaphore = new SemaphoreSlim(settings.MaxConcurrentDownloads, settings.MaxConcurrentDownloads)
            let throttled progress = throttledAction semaphore (downloadFile httpClient Product.createHashAlgorithm cancellationToken progress)
            let downloader = { Download = throttled; Progress = downloadProgress }
            Console.CursorVisible <- false
            match! updateProduct downloader hashProgress pathInfo manifest.Files with
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
            promptForProductToPlay playableProducts cancellationToken
        else None
        |> Result.requireSome NoSelectedProduct
    
    let! p = Product.validateForRun settings.CbLauncherDir settings.WatchForCrashes selectedProduct |> Result.mapError InvalidProductState
    
    let gameLanguage = Cobra.getGameLang settings.CbLauncherDir settings.PreferredLanguage
    let processArgs() = Product.createArgString settings.DisplayMode gameLanguage connection.Session machineId (getRunningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile selectedProduct
    let processes = Process.launchProcesses settings.Processes
    
    if not cancellationToken.IsCancellationRequested then
        launchProduct settings.CompatTool processArgs settings.Restart selectedProduct.Name p
        Process.stopProcesses processes
    return 0
} 