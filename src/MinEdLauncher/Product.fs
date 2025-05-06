module MinEdLauncher.Product

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open MinEdLauncher.Http
open MinEdLauncher.Rop
open MinEdLauncher.Types
open FsToolkit.ErrorHandling

let generateFileHashStr (hashFile: string -> Result<byte[], 'TError>) (file: string) =
    hashFile file
    |> Result.map (Hex.toString >> String.toLower)

let hashFile = SHA1.hashFile
let createHashAlgorithm() = SHA1.Create() :> HashAlgorithm

let private mapHashPair file hash = (file, hash)
let private getFileRelativeDirectory relativeTo (file: string) = file.Replace(relativeTo, "").TrimStart(Path.DirectorySeparatorChar)

let private generateFileHashes tryGenHash (progress: IProgress<int>) productDir (manifestFiles: string Set) (filePaths: string[]) =
    let files =
        filePaths
        |> Seq.map (getFileRelativeDirectory productDir)
        |> Seq.intersect manifestFiles
        |> Seq.indexed
        |> Seq.toArray
    let alreadyHashed = manifestFiles.Count - files.Length
    let tryGetHash (index, file) =
        let getFileAbsoluteDirectory file = Path.Combine(productDir, file)
        let hash = tryGenHash (getFileAbsoluteDirectory file) |> Option.map (mapHashPair file)
        progress.Report(index + 1 + alreadyHashed)
        hash
        
    files
    |> Array.choose tryGetHash
    |> Map.ofSeq

let getFileHashes tryGenHash fileExists progress (manifestFiles: string Set) cache productDir (filePaths: string seq) =
    let getHashFromCache cache file =
        cache
        |> Map.tryFind (getFileRelativeDirectory productDir file)
        |> Option.map (mapHashPair file)

    let filePaths = filePaths |> Seq.toArray 
    let cachedHashes = filePaths |> Array.choose (getHashFromCache cache)
    let missingHashes = filePaths |> Array.except (cachedHashes |> Array.map fst) |> Array.filter fileExists
    generateFileHashes tryGenHash progress productDir manifestFiles missingHashes
    |> Map.merge cache

let parseHashCacheLines (lines: string seq) =
    lines
    |> Seq.choose (fun line ->
        let parts = line.Split("|", StringSplitOptions.RemoveEmptyEntries)
        if parts.Length = 2 then
            Some (parts.[0], parts.[1])
        else
            None)
    |> Map.ofSeq
    
let parseHashCache filePath =
    if File.Exists(filePath) then
        FileIO.readAllLines filePath
        |> TaskResult.map parseHashCacheLines
    else Map.empty |> Ok |> Task.fromResult

let mapHashMapToLines hashMap =
    hashMap
    |> Map.toSeq
    |> Seq.map (fun (file, hash) -> $"%s{file}|%s{hash}")
    
let writeHashCache append path hashMap =
    let writeAllLines = if append then FileIO.appendAllLines else FileIO.writeAllLines
    mapHashMapToLines hashMap |> writeAllLines path

let normalizeManifestPartialPath (path: string) =
    if not (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) then
        path.Replace('\\', '/')
    else
        path

let mapFileToRequest destDir (file: Types.ProductManifest.File) =
    let path = normalizeManifestPartialPath file.Path
    let targetPath = Path.Combine(destDir, path)
    { RemotePath = file.Download; TargetPath = targetPath; ExpectedHash = file.Hash }

let downloadFiles downloader destDir (files: Types.ProductManifest.File[]) : Task<Result<FileDownloadResponse[], string>> = task {
    let combinedTotalBytes = files |> Seq.sumBy (fun f -> int64 f.Size)
    let combinedBytesSoFar = ref 0L
    let stopWatch = Stopwatch()
    let relativeProgress = Progress<int>(fun bytesRead ->
        let bytesSoFar = Interlocked.Add(combinedBytesSoFar, int64 bytesRead)
        downloader.Progress.Report({ TotalFiles = files.Length
                                     BytesSoFar = bytesSoFar
                                     Elapsed = stopWatch.Elapsed
                                     TotalBytes = combinedTotalBytes })) :> IProgress<int>
        
    let ensureDirectories requests =
        requests
        |> Seq.iter (fun request ->
            let dirName = Path.GetDirectoryName(request.TargetPath);
            if dirName.Length > 0 then
                Directory.CreateDirectory(dirName) |> ignore )
        
    let requests = files |> Array.map (mapFileToRequest destDir)
    
    try
        ensureDirectories requests
        stopWatch.Start()
        let! result = downloader.Download relativeProgress requests
        stopWatch.Stop()
        downloader.Progress.Flush()
        return Ok result
    with e -> return e.ToString() |> Error }

let manifestHashCachePath cacheDir details metadata =
    Path.Combine(cacheDir, $"manifest-hash_{Path.GetFileName(details.Directory)}_{metadata.LocalFile}.txt")

let cacheManifestHash dir (products: Product list) = task {
    let getMetadata p =
            match p with
            | Playable details
            | RequiresUpdate details
            | MaybeRequiresUpdate details
            | RequiresStealthUpdate (details, _)
            | Missing details when details.Metadata.IsSome -> Some (details, details.Metadata.Value)
            | Playable _
            | RequiresUpdate _
            | MaybeRequiresUpdate _
            | RequiresStealthUpdate _
            | Missing _
            | Unknown _ -> None
    let! _ =
        products
        |> List.choose getMetadata
        |> List.map(fun (details, metadata) -> task {
            let path = manifestHashCachePath dir details metadata
            match! FileIO.writeAllText path metadata.Hash with
            | Ok () -> Log.debug $"Wrote manifest hash to %s{path}"
            | Error e -> Log.debug $"Failed to write manifest hash to %s{path} - %s{e.ToString()}"
        })
        |> Task.whenAll
    
    return Ok products
}

// Steam and Epic updates should be handled by their CDNs.
// Sometimes FDev doesn't release updates through them though (e.g. Odyssey alpha)
// so allow users to specify if they want to override that behavior
let filterByUpdateable platform updateOverride (products: Product list) =
        let getDetails p =
            match p with
            | Playable details
            | RequiresUpdate details
            | MaybeRequiresUpdate details
            | RequiresStealthUpdate (details, _)
            | Missing details -> Some details
            | Unknown _ -> None
        match platform with
        | Steam | Epic _ ->
            let skuOverriden d = updateOverride |> Set.contains d.Sku
            let shouldOverride p =
                getDetails p
                |> Option.bind (fun details ->
                    if p.IsRequiresStealthUpdate || (skuOverriden details) then
                        Some details
                    else
                        None
                )
                    
            products |> List.choose shouldOverride
        | Frontier _ | Oculus _ | Dev -> products |> List.choose getDetails

let filterByMissing (products: Product list) =
    products |> List.filter _.IsMissing

let filterByUpdateRequired (products: Product list) =
    products |> List.filter (fun p -> p.IsRequiresStealthUpdate || p.IsRequiresUpdate)

let selectProduct (whitelist: OrdinalIgnoreCaseSet) (products: ProductDetails[]) =
    if whitelist.IsEmpty then
        None
    else
        products
        |> Array.filter (fun p -> p.Filters |> OrdinalIgnoreCaseSet.intersect whitelist |> OrdinalIgnoreCaseSet.any)
        |> Array.tryHead

let createArgString vr (lang: string option) edSession machineId timestamp watchForCrashes platform hashFile (product:ProductDetails) =
    let targetOptions = String.Join(" ", [
        if lang.IsSome then "/language " + lang.Value 
        match platform, product.VInfo.SteamAware with
            | Steam, true -> "/steam"
            | Epic p, _ ->
                let refresh = edSession.PlatformToken.GetRefreshToken() |> Option.defaultValue "[none]"
                let sId = p.SandboxId |> Option.defaultValue "[none]"
                let dId = p.DeploymentId |> Option.defaultValue "[none]"
                $"\"ConfigureEpic %s{refresh} %s{sId} %s{dId}\""
            | _, _ -> ()
        match vr with
            | Vr -> "/vr"
            | _ -> "/novr"
        if not (String.IsNullOrEmpty(product.GameArgs)) then product.GameArgs ])
    let online =
        match product.VInfo.Mode with
        | Offline -> false
        | Online -> true
    let prepareQuotes (input: string) = if watchForCrashes then input.Replace("\"", "\"\"") else input
    let serverToken = if online then $"ServerToken %s{edSession.MachineToken} %s{edSession.Token} %s{product.ServerArgs}" else ""
    let combined = $"\"%s{serverToken}\" %s{targetOptions}" |> prepareQuotes
    let fullExePath = Path.Combine(product.Directory, product.VInfo.Executable)
    let exeHash = fullExePath |> hashFile |> Result.map Hex.toString |> Result.map (fun p -> p.ToUpperInvariant()) |> Result.defaultValue ""
    if watchForCrashes && online then
        let version = product.VInfo.Version.ToString()
        sprintf "/Executable \"%s\" /ExecutableArgs \"%s\" /MachineToken %s /Version %s /AuthToken %s /MachineId %s /Time %s /ExecutableHash \"%s\""
            fullExePath combined edSession.MachineToken version (edSession.Token) machineId (timestamp.ToString()) exeHash
    else
        combined
        
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
    let filters = product.Filter.Split(',', StringSplitOptions.RemoveEmptyEntries) |> OrdinalIgnoreCaseSet.ofSeq
    let directory = Path.Combine(productsDir, product.DirectoryName)
    match readVersionInfo directory with
    | Found v ->
        Playable { Sku = product.Sku
                   Name = product.Name
                   Filters = filters
                   VInfo = v 
                   Directory = directory
                   GameArgs = product.GameArgs
                   ServerArgs = serverArgs
                   SortKey = product.SortKey
                   Metadata = None }
    | NotFound file ->
        Log.debug $"Unable to find product's version info at '%s{file}'"
        Missing { Sku = product.Sku
                  Name = product.Name
                  Filters = filters
                  VInfo = VersionInfo.Empty
                  Directory = directory
                  GameArgs = product.GameArgs
                  ServerArgs = serverArgs
                  SortKey = product.SortKey
                  Metadata = None }
    | Failed msg ->
        Log.error $"Unable to parse product %s{product.Name}: %s{msg}"
        Product.Unknown product.Name
        
type RunnableProduct =
    { Executable: FileInfo
      WorkingDir: DirectoryInfo
      Version: Version
      SteamAware: bool
      Mode: ProductMode
      ServerArgs: string }
let validateForRun launcherDir watchForCrashes (product: ProductDetails) =
    let productFullPath = Path.Combine(product.Directory, product.VInfo.Executable)
    let watchDogFullPath = if product.VInfo.UseWatchDog64 then Path.Combine(launcherDir, "WatchDog64.exe") else Path.Combine(launcherDir, "WatchDog.exe") 
    if not (File.Exists(productFullPath)) then
        Error $"Unable to find product exe at '%s{productFullPath}'"
    elif watchForCrashes && not (File.Exists(watchDogFullPath)) then
        Error $"Unable to find watchdog exe at '%s{watchDogFullPath}'"
    else
        let exePath = if watchForCrashes then watchDogFullPath else productFullPath
        Ok { Executable = FileInfo(exePath)
             WorkingDir = DirectoryInfo(Path.GetDirectoryName(productFullPath))
             Version = product.VInfo.Version
             SteamAware = product.VInfo.SteamAware
             Mode = product.VInfo.Mode
             ServerArgs = product.ServerArgs }
    
let isRunning (product:RunnableProduct) =
    let exeName = Path.GetFileNameWithoutExtension(product.Executable.Name)
    
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        Process.GetProcessesByName(exeName).Length > 0
    else
        // Process.ProcessName seems to be truncated on linux. Not sure if it's always the case or only sometimes
        // so check both truncated name and full name
        Process.GetProcessesByName(exeName[..14]).Length > 0 || Process.GetProcessesByName(exeName).Length > 0

let createProcessInfo proton args product =
    let fileName, arguments =
        match proton with
        | Some details ->
            let protonArgs = details.Args |> Array.map (fun a -> $"\"%s{a}\"") |> String.join " "
            details.EntryPoint,  $"%s{protonArgs} \"%s{product.Executable.FullName}\" %s{args}"
        | None -> product.Executable.FullName, args
    
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- fileName
    startInfo.WorkingDirectory <- product.WorkingDir.FullName
    startInfo.Arguments <- arguments
    startInfo.CreateNoWindow <- true
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo

type RunResult = Ok of Process | AlreadyRunning | DryRun of ProcessStartInfo | Error of exn
let run dryRun proton args (product:RunnableProduct)  =
    let startInfo = createProcessInfo proton args product
    
    Log.debug $"Process: %s{startInfo.FileName} %s{startInfo.Arguments}"
    
    if dryRun then
        DryRun startInfo
    else
        try
            Process.Start(startInfo) |> Ok
        with
        | e -> Error e