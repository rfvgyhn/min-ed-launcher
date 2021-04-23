module MinEdLauncher.Product

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open FSharp.Control.Tasks.NonAffine
open MinEdLauncher.Http
open MinEdLauncher.Rop
open MinEdLauncher.Types

let generateFileHashStr (hashFile: string -> Result<byte[], 'TError>) (file: string) =
    hashFile file
    |> Result.map (Hex.toString >> String.toLower)

let hashFile = SHA1.hashFile
let createHashAlgorithm() = SHA1.Create() :> HashAlgorithm

let private mapHashPair file hash = (file, hash)
let private getFileRelativeDirectory relativeTo (file: string) = file.Replace(relativeTo, "").TrimStart(Path.DirectorySeparatorChar)

let private generateFileHashes tryGenHash productDir (manifestFiles: string Set) (filePaths: string seq) =
    let tryGetHash file =
        let getFileAbsoluteDirectory file = Path.Combine(productDir, file)
        tryGenHash (getFileAbsoluteDirectory file) |> Option.map (mapHashPair file)
        
    filePaths
    |> Seq.map (getFileRelativeDirectory productDir)
    |> Seq.intersect manifestFiles
    |> Seq.choose tryGetHash
    |> Map.ofSeq

let getFileHashes tryGenHash fileExists (manifestFiles: string Set) cache productDir (filePaths: string seq) =
    let getHashFromCache cache file =
        cache
        |> Map.tryFind (getFileRelativeDirectory productDir file)
        |> Option.map (mapHashPair file)

    let cachedHashes = filePaths |> Seq.choose (getHashFromCache cache)
    let missingHashes = filePaths |> Seq.except (cachedHashes |> Seq.map fst) |> Seq.filter fileExists
    generateFileHashes tryGenHash productDir manifestFiles missingHashes

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
        |> Task.mapResult parseHashCacheLines
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
    
    let relativeProgress = Progress<int>(fun bytesRead ->
        let bytesSoFar = Interlocked.Add(combinedBytesSoFar, int64 bytesRead)
        downloader.Progress.Report({ TotalFiles = files.Length
                                     BytesSoFar = bytesSoFar
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
        let! result = downloader.Download relativeProgress requests
        return Ok result
    with e -> return e.ToString() |> Error }

let createArgString vr (lang: string option) edSession machineId timestamp watchForCrashes platform hashFile (product:ProductDetails) =
    let targetOptions = String.Join(" ", [
        if lang.IsSome then "/language " + lang.Value 
        match platform, product.SteamAware with
            | Steam, true -> "/steam"
            | Epic _, _ ->
                let refresh = edSession.PlatformToken.GetRefreshToken() |> Option.defaultValue ""
                $"\"EpicToken {refresh}\""
            | _, _ -> ()
        match vr with
            | Vr -> "/vr"
            | _ -> "/novr"
        if not (String.IsNullOrEmpty(product.GameArgs)) then product.GameArgs ])
    let online =
        match product.Mode with
        | Offline -> false
        | Online -> true
    let prepareQuotes (input: string) = if watchForCrashes then input.Replace("\"", "\"\"") else input
    let serverToken = if online then $"ServerToken %s{edSession.MachineToken} %s{edSession.Token} %s{product.ServerArgs}" else ""
    let combined = $"\"%s{serverToken}\" %s{targetOptions}" |> prepareQuotes
    let fullExePath = Path.Combine(product.Directory, product.Executable)
    let exeHash = fullExePath |> hashFile |> Result.map Hex.toString |> Result.map (fun p -> p.ToUpperInvariant()) |> Result.defaultValue ""
    if watchForCrashes && online then
        let version = product.Version.ToString()
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
                   ServerArgs = serverArgs
                   Metadata = None }
    | NotFound file ->
        Log.info $"Disabling '%s{product.Name}'. Unable to find product at '%s{file}'"
        Missing { Sku = product.Sku
                  Name = product.Name
                  Filters = filters
                  Directory = directory }
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
    let productFullPath = Path.Combine(product.Directory, product.Executable)
    let watchDogFullPath = if product.UseWatchDog64 then Path.Combine(launcherDir, "WatchDog64.exe") else Path.Combine(launcherDir, "WatchDog.exe") 
    if not (File.Exists(productFullPath)) then
        Error $"Unable to find product exe at '%s{productFullPath}'"
    elif watchForCrashes && not (File.Exists(watchDogFullPath)) then
        Error $"Unable to find watchdog exe at '%s{watchDogFullPath}'"
    else
        let exePath = if watchForCrashes then watchDogFullPath else productFullPath
        Ok { Executable = FileInfo(exePath)
             WorkingDir = DirectoryInfo(Path.GetDirectoryName(productFullPath))
             Version = product.Version
             SteamAware = product.SteamAware
             Mode = product.Mode
             ServerArgs = product.ServerArgs }
    
let isRunning (product:RunnableProduct) =
    let exeName = product.Executable.Name
    
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        Process.GetProcessesByName(exeName).Length > 0 // TODO: check that this is true when running on Windows
    else
        Process.GetProcesses() // When running via wine, process name can be truncated and the main module is wine so check all module names
        |> Array.exists (fun p ->
            p.Modules
            |> Seq.cast<ProcessModule>
            |> Seq.exists (fun m -> m.ModuleName = exeName))

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

type RunResult = Ok of Process | AlreadyRunning | Error of exn
let run proton args (product:RunnableProduct)  =
    if isRunning product then
        AlreadyRunning
    else
        let startInfo = createProcessInfo proton args product
        
        Log.debug $"Process: %s{startInfo.FileName} %s{startInfo.Arguments}"
        
        try
            Process.Start(startInfo) |> Ok
        with
        | e -> Error e