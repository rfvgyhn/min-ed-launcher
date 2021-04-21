module MinEdLauncher.App

open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Threading
open MinEdLauncher
open MinEdLauncher.Token
open FSharp.Control.Tasks.NonAffine
open System
open System.Diagnostics
open System.Threading.Tasks
open MinEdLauncher.Types
open MinEdLauncher.HttpClientExtensions

type LoginResult =
| Success of Api.Connection
| ActionRequired of string
| Failure of string
let login runningTime httpClient machineId (platform: Platform) lang =
    let authenticate = function
        | Ok authToken -> task {
            Log.debug $"Authenticating via %s{platform.Name}"
            match! Api.authenticate runningTime authToken platform machineId lang httpClient with
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
    | Dev -> task {
        let! result = Permanent "DevAuthToken" |> Ok |> authenticate
        return result, noopDisposable }
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
            let! result = PasswordBased { Username = username; Password = password; Token = token } |> Ok |> authenticate
            return result, noopDisposable
        | Error msg ->
            let! _ = Cobra.discardToken credPath
            return Failure msg, noopDisposable }
    | Steam -> task {
        use steam = new Steam.Steam()
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
    Log.info $"""Elite Dangerous - Minimal Launcher
Platform: %s{platform.Name}
OS: %s{RuntimeInformation.getOsIdent()}
CobraBay Version: %s{cobraVersion}
Launcher Version: %s{launcherVersion}
Products Dir: %s{productsDir}"""
    
let rec launchProduct proton processArgs restart productName product =
    let args = processArgs()
    Log.info $"Launching %s{productName}"
    
    match Product.run proton args product with
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

        use p = p
        p.WaitForExit()
        Log.info $"Shutdown %s{productName}"
        
        if shouldRestart && not (cancelRestart()) then
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

let normalizeManifestPartialPath (path: string) =
    if not (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) then
        path.Replace('\\', '/')
    else
        path

let throttledDownload (semaphore: SemaphoreSlim) (download: 'a -> Task<'b>) input =
    input
    |> Array.map (fun file -> task {
        do! semaphore.WaitAsync()
        try
             return! download(file)
        finally
            semaphore.Release() |> ignore
        })
    |> Task.whenAll

type DownloadProgress = { TotalFiles: int; BytesSoFar: int64; TotalBytes: int64; }
let downloadFiles (httpClient: HttpClient) (throttler: SemaphoreSlim) destDir (progress: IProgress<DownloadProgress>) cancellationToken (files: Types.ProductManifest.File[]) = task {
    let combinedTotalBytes = files |> Seq.sumBy (fun f -> int64 f.Size)
    let combinedBytesSoFar = ref 0L
    let downloadFile (file: Types.ProductManifest.File) = task {
        let path = normalizeManifestPartialPath file.Path
        let dest = Path.Combine(destDir, path)
        
        let dirName = Path.GetDirectoryName(dest);
        if dirName.Length > 0 then
            Directory.CreateDirectory(dirName) |> ignore
        
        use sha1 = SHA1.Create()
        use fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Write, 4096, FileOptions.Asynchronous)
        use cryptoStream = new CryptoStream(fileStream, sha1, CryptoStreamMode.Write) // Calculate hash as file is downloaded
        let relativeProgress = Progress<int>(fun bytesRead ->
            let bytesSoFar = Interlocked.Add(combinedBytesSoFar, int64 bytesRead)
            progress.Report({ TotalFiles = files.Length
                              BytesSoFar = bytesSoFar
                              TotalBytes = combinedTotalBytes }))
        do! httpClient.DownloadAsync(file.Download, cryptoStream, relativeProgress, cancellationToken)
        cryptoStream.Dispose()
        let hash = sha1.Hash |> Hex.toString |> String.toLower
        return dest, file.Hash = hash }
    
    try
        let! result = files |> (throttledDownload throttler downloadFile)
        return Ok result
    with e -> return e.ToString() |> Error }

type UpdateProductPaths = { ProductDir: string; ProductCacheDir: string; CacheHashMap: string; ProductHashMap: string }
let updateProduct (httpClient: HttpClient) (throttler: SemaphoreSlim) cancellationToken paths (manifest: Types.ProductManifest.File[]) = task {
    let manifestMap =
        manifest
        |> Array.map (fun file -> normalizeManifestPartialPath file.Path, file)
        |> Map.ofArray
    let getFileHash file =
        match SHA1.hashFile file |> Result.map Hex.toString with
        | Ok hash -> Some (hash.ToLower())
        | Error e ->
           Log.warn $"Unable to get hash of file '%s{file}' - %s{e.ToString()}"
           None
    let parseHashCache hashMapPath =
        if File.Exists(hashMapPath) then
            FileIO.readAllLines hashMapPath
            |> Task.mapResult (fun (lines: string[]) ->
                lines
                |> Array.choose (fun line ->
                    let parts = line.Split("|", StringSplitOptions.RemoveEmptyEntries)
                    if parts.Length = 2 then
                        Some (parts.[0], parts.[1])
                    else
                        None)
                |> Map.ofArray)
        else Map.empty |> Ok |> Task.fromResult
           
    let getFileHashes cache dir (filePaths: string seq) =
        filePaths
        |> Seq.filter File.Exists
        |> Seq.map (fun file -> file.Replace(dir, "").TrimStart(Path.DirectorySeparatorChar))
        |> Seq.filter manifestMap.ContainsKey
        |> Seq.choose (fun file ->
            cache
            |> Map.tryFind file
            |> Option.orElseWith (fun () -> getFileHash (Path.Combine(dir, file)))
            |> Option.map (fun hash -> (file, hash)))
        |> Map.ofSeq

    let verifyFiles files =
        let invalidFiles = files |> Seq.filter (fun (path, valid) -> not valid) |> Seq.map fst
        if Seq.isEmpty invalidFiles then Ok ()
        else invalidFiles |> String.join Environment.NewLine |> Error
    
    let progress = Progress<DownloadProgress>(fun p ->
        let total = p.TotalBytes |> Int64.toFriendlyByteString
        let percent = float p.BytesSoFar / float p.TotalBytes
        Console.Write($"\rDownloading %d{p.TotalFiles} files (%s{total}) - {percent:P0}"))

    let writeHashCache path hashMap = task {
        let! write =
            hashMap
            |> Map.toSeq
            |> Seq.map (fun (file, hash) -> $"%s{file}|%s{hash}")
            |> FileIO.writeAllLines path
        match write with
        | Ok () -> Log.debug $"Wrote hash cache to '%s{path}'"
        | Error e -> Log.warn $"Unable to write hash cache at '%s{paths.ProductHashMap}' - %s{e}" }
    
    let processFiles productHashMap cacheHashMap =
        paths.ProductCacheDir
        |> FileIO.ensureDirExists
        |> Result.map (fun cacheDir ->
            let cachedHashes = getFileHashes cacheHashMap cacheDir (Directory.EnumerateFiles(cacheDir, "*.*", SearchOption.AllDirectories))
            let validCachedFiles = cachedHashes |> Map.filter (fun file hash -> manifestMap.[file].Hash = hash) |> Map.keys
            let manifestKeys = manifestMap |> Map.keys
            let productHashes =
                manifestKeys
                |> Seq.except validCachedFiles
                |> Seq.map (fun path -> Path.Combine(paths.ProductDir, path))
                |> getFileHashes productHashMap paths.ProductDir
                |> Map.fold (fun acc key value -> Map.add key value acc) cachedHashes
                
            manifestKeys
            |> Set.filter (fun file ->
                productHashes
                |> Map.tryFind file
                |> Option.map (fun hash -> manifestMap.[file].Hash <> hash)
                |> Option.isNone)
            |> Seq.map (fun file -> Map.find file manifestMap)
            |> Seq.toArray, productHashes, cachedHashes)
    
    Log.info "Determining which files need to be updated. This may take a while."
    let! cacheHashes = task {
        match! parseHashCache paths.CacheHashMap with
        | Ok hashes -> return hashes
        | Error e ->
            Log.warn $"Unable to parse hash map at '%s{paths.CacheHashMap}' - %s{e}"
            return Map.empty }
    let! productHashes = task {
        match! parseHashCache paths.ProductHashMap with
        | Ok hashes -> return hashes
        | Error e ->
            Log.warn $"Unable to parse hash map at '%s{paths.ProductHashMap}' - %s{e}"
            return Map.empty }
    
    return!
        processFiles productHashes cacheHashes
        |> Result.bindTask (fun (invalidFiles, productHashes, cacheHashes) -> task {
            do! writeHashCache paths.ProductHashMap productHashes
            do! writeHashCache paths.CacheHashMap cacheHashes
            return Ok invalidFiles })
        |> Task.bindTaskResult (downloadFiles httpClient throttler paths.ProductCacheDir progress cancellationToken)
        |> Task.bindResult verifyFiles }
    
let run settings cancellationToken = task {
    if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && settings.Platform = Steam then
        Steam.fixLcAll()
    
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
            match machineId with
            | Ok machineId ->
                let lang = settings.PreferredLanguage |> Option.defaultValue "en"
                let! loginResult, disposable = login runningTime httpClient machineId settings.Platform lang
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
                        let filterProducts f products =
                            products
                            |> Result.defaultValue []
                            |> List.choose f
                            |> List.toArray
                        let productsRequiringUpdate = products |> filterProducts (fun p -> match p with | RequiresUpdate p -> Some p | _ -> None)
                        
                        let productsToUpdate =
                            let products =
                                if true(*settings.AutoUpdate*) then
                                    productsRequiringUpdate
                                else
                                    productsRequiringUpdate |> promptForProductsToUpdate
                            products
                            |> Array.filter (fun p -> p.Metadata.IsNone)
                            |> Array.iter (fun p -> Log.error $"Unknown product metadata for %s{p.Name}")
                            
                            products |> Array.filter (fun p -> p.Metadata.IsSome)
                        
                        use tmpClient = new HttpClient()
                        tmpClient.Timeout <- TimeSpan.FromMinutes(5.)                        
                        let tmp = products |> filterProducts (fun p -> match p with | Playable p -> Some p | _ -> None)
                        let! asdf = Api.getProductManifest tmpClient (Uri("http://cdn.zaonce.net/elitedangerous/win/manifests/Win64_Release_3_7_7_500+%282021.01.28.254828%29.xml.gz"))
                        //let! fdsa = Api.getProductManifest httpClient (Uri("http://cdn.zaonce.net/elitedangerous/win/manifests/Win64_4_0_0_10_Alpha+%282021.04.09.263090%29.xml.gz"))
                        do! match asdf with
                            | Ok man -> task {
                                let p = tmp.[0]
                                Log.info $"Updating %s{p.Name}"
                                let productsDir = Path.Combine(settings.CbLauncherDir, "Products")
                                let productDir = Path.Combine(productsDir, p.Directory)
                                use throttler = new SemaphoreSlim(4, 4)
                                
                                let productCacheDir = Path.Combine(Environment.cacheDir, $"%s{man.Title}%s{man.Version}")
                                let pathInfo = { ProductDir = productDir
                                                 ProductCacheDir = productCacheDir
                                                 CacheHashMap = Path.Combine(productCacheDir, "hashmap.txt")
                                                 ProductHashMap = Path.Combine(Environment.cacheDir, $"hashmap.%s{Path.GetFileName(productDir)}.txt") }
                                let! result = updateProduct tmpClient throttler cancellationToken pathInfo man.Files
                                printfn ""
                                match result with
                                | Ok () ->
                                    Log.info $"Finished downloading update for %s{p.Name}"
                                    File.Delete(pathInfo.CacheHashMap)
                                    FileIO.mergeDirectories productDir productCacheDir
                                    Log.debug $"Moved downloaded files from '%s{Environment.cacheDir}' to '%s{productDir}'"
                                | Error e -> Log.error $"Unable to download update for %s{p.Name} - %s{e}" }
                            | Error e -> () |> Task.fromResult

                        let! productManifestTasks =
                            productsToUpdate
                            |> Array.map (fun p ->
                                p.Metadata
                                |> Option.map (fun m -> Api.getProductManifest httpClient m.RemotePath)
                                |> Option.defaultValue (Task.FromResult(Error $"No metadata for %s{p.Name}")))
                            |> Task.whenAll
                        
                        let productManifests =
                            productManifestTasks
                            |> Array.zip productsToUpdate
                            |> Array.choose (fun (_, manifest) -> match manifest with Ok m -> Some m | Error _ -> None)
                        let failedManifests = productManifestTasks |> Array.choose (function Ok _ -> None | Error e -> Some e)
                        
                        let playableProducts = products |> filterProducts (fun p -> match p with | Playable p -> Some p | _ -> None)
                        let selectedProduct =
                            if settings.AutoRun then
                                playableProducts
                                |> Array.filter (fun p -> settings.ProductWhitelist.Count = 0
                                                          || p.Filters |> Set.union settings.ProductWhitelist |> Set.count > 0)
                                |> Array.tryHead
                            else if playableProducts.Length > 0 then
                                promptForProductToPlay playableProducts cancellationToken
                            else None
                        
                        match selectedProduct, cancellationToken.IsCancellationRequested with
                        | _, true -> ()
                        | Some product, _ ->
                            let gameLanguage = Cobra.getGameLang settings.CbLauncherDir settings.PreferredLanguage
                            let processArgs() = Product.createArgString settings.DisplayMode gameLanguage connection.Session machineId (runningTime()) settings.WatchForCrashes settings.Platform SHA1.hashFile product
                            
                            match Product.validateForRun settings.CbLauncherDir settings.WatchForCrashes product with
                            | Ok p ->
                                let processes = Process.launchProcesses settings.Processes
                                launchProduct settings.Proton processArgs settings.Restart product.Name p
                                Process.stopProcesses processes
                            | Error msg -> Log.error $"Couldn't start selected product: %s{msg}"
                        | None, _ -> Log.error "No selected project"
                        
                        if not settings.AutoQuit && not cancellationToken.IsCancellationRequested then
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