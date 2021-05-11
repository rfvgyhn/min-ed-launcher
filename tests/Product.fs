module MinEdLauncher.Tests.Product

open System
open System.IO
open MinEdLauncher
open MinEdLauncher.Http
open MinEdLauncher.Product
open MinEdLauncher.Token
open MinEdLauncher.Types
open Expecto

    module Expect =
        let notStringContains (subject : string) (substring : string) message =
          if (subject.Contains(substring)) then
            failtestf "%s. Expected subject string '%s' to not contain substring '%s'."
                            message subject substring
        let stringEqual (actual: string) (expected: string) comparisonType message =
            if not (String.Equals(actual, expected, comparisonType)) then
                failtest $"%s{message}. Actual value was %s{actual} but had expected it to be %s{expected}."

    [<Tests>]
    let tests =
        let product =
            { Sku = ""
              Name = ""
              Filters = Set.empty
              Executable = ""
              UseWatchDog64 = false
              SteamAware = false
              Version = System.Version()
              Mode = Offline
              Directory = ""
              GameArgs = ""
              ServerArgs = ""
              SortKey = 0
              Metadata = None }
        let getTimestamp = fun () -> (double)1
        let hashFile = fun str -> Result.Ok Array.empty<byte>
        let token = EdSession.Empty
            
        testList "Product" [
            testList "Argument String" [
                test "Language provided" {
                    let actual = createArgString Vr (Some "theLang") token "" getTimestamp false Dev hashFile product
                    
                    Expect.stringContains actual "/language theLang" ""
                }
                test "No language provided" {
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "/language" ""
                    Expect.notStringContains actual "theLang" ""
                }
                test "Steam platform and steam aware product" {
                    let product = { product with SteamAware = true }
                    let actual = createArgString Vr None token "" getTimestamp false Steam hashFile product
                    
                    Expect.stringContains actual "/steam" ""
                }
                test "Steam platform and non steam aware product" {
                    let product = { product with SteamAware = false }
                    let actual = createArgString Vr None token "" getTimestamp false Steam hashFile product
                    
                    Expect.notStringContains actual "/steam" ""
                }
                test "Non steam platform and steam aware product" {
                    let product = { product with SteamAware = true }
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "/steam" ""
                }
                test "Non steam platform and non steam aware product" {
                    let product = { product with SteamAware = false }
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "/steam" ""
                }
                test "Epic platform contains refresh token" {
                    let token = { EdSession.Empty with PlatformToken = Expires (fun () -> { RefreshableToken.Empty with RefreshToken = "asdf" }) }
                    let actual = createArgString Vr None token "" getTimestamp false (Epic EpicDetails.Empty) hashFile product
                    
                    Expect.stringContains actual "\"EpicToken asdf\"" ""
                }
                test "Non epic platform doesn't contain refresh token" {
                    let token = { EdSession.Empty with PlatformToken = Expires (fun () -> { RefreshableToken.Empty with RefreshToken = "asdf" }) }
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "\"EpicToken" ""
                }
                test "VR mode" {
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.stringContains actual "/vr" ""
                }
                test "Non VR mode" {
                    let actual = createArgString Pancake None token "" getTimestamp false Dev hashFile product
                    
                    Expect.stringContains actual "/novr" ""
                }
                test "ServerToken is used when product is online and not watching for crashes" {
                    let session = { EdSession.Empty with Token = "54321"; MachineToken = "12345" }
                    let serverArgs = "/some arg"
                    let gameArgs = "/gameargs"
                    let product = { product with ServerArgs = serverArgs; Mode = Online; GameArgs = gameArgs }
                    let actual = createArgString Vr None session "" getTimestamp false Dev hashFile product
                    
                    let expected = $"\"ServerToken %s{session.MachineToken} %s{session.Token} %s{serverArgs}\""
                    Expect.stringStarts actual expected ""
                    Expect.stringEnds actual gameArgs ""
                }
                test "Product is online and watching for crashes" {
                    let session = { EdSession.Empty with Token = "456"; MachineToken = "123" }
                    let serverArgs = "/Test"
                    let machineId = "789"
                    let timeStamp = 12345.12345
                    let hashFile = fun _ -> Result.Ok [|228uy; 20uy; 11uy; 154uy;|]
                    let version = System.Version(1, 2, 3)
                    let product = { product with ServerArgs = serverArgs; Mode = Online; Version = version; Directory = Path.Combine("path", "to"); Executable = "theExe.exe" }
                    let actual = createArgString Vr None session machineId timeStamp true Dev hashFile product
                    
                    let expectedExe = sprintf "/Executable \"%s\"" (Path.Combine("path", "to", "theExe.exe"))
                    let expectedExeArgs = sprintf "/ExecutableArgs \"%s\"" <| sprintf "\"\"ServerToken %s %s %s\"\" /vr" session.MachineToken session.Token serverArgs
                    let expectedMachineToken = $"/MachineToken %s{session.MachineToken}"
                    let expectedVersion = $"/Version %s{version.ToString()}"
                    let expectedsessionToken = $"/AuthToken %s{session.Token}"
                    let expectedMachineId = $"/MachineId %s{machineId}"
                    let expectedTime = $"/Time %s{timeStamp.ToString()}"
                    let expectedHash = $"/ExecutableHash \"E4140B9A\""
                    Expect.stringContains actual expectedExe ""
                    Expect.stringContains actual expectedExeArgs ""
                    Expect.stringContains actual expectedMachineToken ""
                    Expect.stringContains actual expectedVersion ""
                    Expect.stringContains actual expectedsessionToken ""
                    Expect.stringContains actual expectedMachineId ""
                    Expect.stringContains actual expectedTime ""
                    Expect.stringContains actual expectedHash ""
                }
            ]
            testList "Run" [
                test "Sets correct process file name when no proton" {
                    let product = { Executable = FileInfo("asdf")
                                    WorkingDir = DirectoryInfo("dir")
                                    Version = System.Version()
                                    SteamAware = false
                                    Mode = Online
                                    ServerArgs = "" }
                    let info = createProcessInfo None "arg1 arg2" product
                    
                    Expect.equal info.FileName product.Executable.FullName ""
                }
                test "Sets correct process file name when using proton" {
                    let proton = { EntryPoint = "asdf"; Args = [| "arg1"; "arg2" |] }
                    let product = { Executable = FileInfo("file")
                                    WorkingDir = DirectoryInfo("dir")
                                    Version = System.Version()
                                    SteamAware = false
                                    Mode = Online
                                    ServerArgs = "" }
                    let info = createProcessInfo (Some proton) "arg3 arg4" product
                    
                    Expect.equal info.FileName proton.EntryPoint ""
                }
                test "Sets correct process args when no proton" {
                    let product = { Executable = FileInfo("asdf")
                                    WorkingDir = DirectoryInfo("dir")
                                    Version = System.Version()
                                    SteamAware = false
                                    Mode = Online
                                    ServerArgs = "" }
                    let args = "arg1 arg2"
                    let info = createProcessInfo None args product
                    
                    Expect.equal info.Arguments args ""
                }
                test "Sets correct process args when using proton" {
                    let proton = { EntryPoint = "asdf"; Args = [| "arg1"; "arg2" |] }
                    let product = { Executable = FileInfo("file")
                                    WorkingDir = DirectoryInfo("dir")
                                    Version = System.Version()
                                    SteamAware = false
                                    Mode = Online
                                    ServerArgs = "" }
                    let info = createProcessInfo (Some proton) "arg3 arg4" product
                    
                    Expect.equal info.Arguments $"\"arg1\" \"arg2\" \"%s{product.Executable.FullName}\" arg3 arg4" ""
                }
                test "Sets correct working dir" {
                    let product = { Executable = FileInfo("asdf")
                                    WorkingDir = DirectoryInfo("dir")
                                    Version = System.Version()
                                    SteamAware = false
                                    Mode = Online
                                    ServerArgs = "" }
                    let info = createProcessInfo None "arg1 arg2" product
                    
                    Expect.equal info.WorkingDirectory product.WorkingDir.FullName ""
                }
            ]
            testList "generateFileHashStr" [
                test "converts to hex representation" {
                    let hashFile = (fun _ -> Result.Ok [| 10uy; 2uy; 15uy; 11uy |])
                    let expected = "0A020F0B"
                    
                    let result = generateFileHashStr hashFile "" |> Result.defaultValue ""
                    
                    Expect.stringEqual result expected StringComparison.OrdinalIgnoreCase ""
                }
                test "converts to all lowercase string" {
                    let hashFile = (fun _ -> Result.Ok [| 10uy; 2uy; 15uy; 11uy |])
                    
                    let result = (generateFileHashStr hashFile "") |> Result.defaultValue ""
                    
                    Expect.all result (fun c -> Char.IsDigit(c) || Char.IsLower(c)) ""
                } ]
            testList "getFileHashes" [
                test "skips files that don't exist" {
                    let tryGenHash = (fun _ -> Some "hash")
                    let fileExists = (fun _ -> false)
                    let baseDir = Path.Combine("the", "directory")
                    let manifestFiles = [ Path.Combine("file", "path") ] |> Set.ofList
                    let cache = Map.empty<string, string> 
                    let filePaths = [ Path.Combine(baseDir, "file", "path") ]
                    
                    let result = getFileHashes tryGenHash fileExists manifestFiles cache baseDir filePaths
                    
                    Expect.isEmpty result ""
                }
                test "tries to get hash of absolute path" {
                    let baseDir = Path.Combine("the", "directory")
                    let absolutePath = Path.Combine(baseDir, "file", "path")
                    let tryGenHash = (fun path -> if path = absolutePath then Some "hash" else None)
                    let fileExists = (fun _ -> true)
                    let manifestFiles = [ Path.Combine("file", "path") ] |> Set.ofList
                    let cache = Map.empty<string, string> 
                    let filePaths = [ absolutePath ]
                    
                    let result = getFileHashes tryGenHash fileExists manifestFiles cache baseDir filePaths
                    
                    Expect.hasLength result filePaths.Length ""
                }
                test "ignores files not in manifest" {
                    let tryGenHash = (fun _ -> Some "hash")
                    let fileExists = (fun _ -> true)
                    let baseDir = Path.Combine("the", "directory")
                    let manifestFile = Path.Combine("manifest", "file")
                    let nonManifestFile = Path.Combine("nonmanifest", "file")
                    let manifestFiles = [ manifestFile ] |> Set.ofList
                    let cache = Map.empty<string, string> 
                    let filePaths = [ manifestFile; nonManifestFile ] |> List.map (fun path -> Path.Combine(baseDir, path))
                    
                    let result = getFileHashes tryGenHash fileExists manifestFiles cache baseDir filePaths
                    
                    Expect.hasLength result 1 ""
                }
                test "uses relative path to check manifest" {
                    let tryGenHash = (fun _ -> Some "hash")
                    let fileExists = (fun _ -> true)
                    let baseDir = Path.Combine("the", "directory")
                    let manifestFile = Path.Combine("manifest", "file")
                    let manifestFiles = [ manifestFile ] |> Set.ofList
                    let cache = Map.empty<string, string> 
                    let filePaths = [ Path.Combine(baseDir, manifestFile) ]
                    
                    let result = getFileHashes tryGenHash fileExists manifestFiles cache baseDir filePaths
                    
                    Expect.hasLength result 1 ""
                }
                test "skips files that weren't able to generate a hash" {
                    let tryGenHash = (fun _ -> None)
                    let fileExists = (fun _ -> true)
                    let baseDir = Path.Combine("the", "directory")
                    let manifestFiles = [ Path.Combine("file", "path") ] |> Set.ofList
                    let cache = Map.empty<string, string> 
                    let filePaths = [ Path.Combine(baseDir, "file", "path") ]
                    
                    let result = getFileHashes tryGenHash fileExists manifestFiles cache baseDir filePaths
                    
                    Expect.isEmpty result ""
                }
                test "merges cached hashes with generated" {
                    let tryGenHash = (fun _ -> Some "hash2")
                    let fileExists = (fun _ -> true)
                    let baseDir = Path.Combine("the", "directory")
                    let cachedFile = Path.Combine("manifest", "file")
                    let nonCachedFile = Path.Combine("manifest", "file2")
                    let manifestFiles = [ cachedFile; nonCachedFile ] |> Set.ofList
                    let cache = [ (cachedFile, "hash") ] |> Map.ofList
                    let filePaths = [ cachedFile; nonCachedFile ] |> List.map (fun path -> Path.Combine(baseDir, path))
                    let expected = cache |> Map.add nonCachedFile "hash2"
                    let result = getFileHashes tryGenHash fileExists manifestFiles cache baseDir filePaths
                    
                    Expect.sequenceEqual result expected ""
                }
                test "skips generating hash if available in cache" {
                    let tryGenHash = (fun _ -> failtest "Shouldn't try to hash file")
                    let fileExists = (fun _ -> false)
                    let baseDir = Path.Combine("the", "directory")
                    let manifestFile = Path.Combine("manifest", "file")
                    let manifestFiles = [ manifestFile ] |> Set.ofList
                    let cache = [ (manifestFile, "hash") ] |> Map.ofList
                    let filePaths = [ Path.Combine(baseDir, manifestFile) ]
                    
                    let result = getFileHashes tryGenHash fileExists manifestFiles cache baseDir filePaths
                    
                    Expect.hasLength result 1 ""
                }
            ]
            testList "parseHashCacheLines" [
                test "skips if line has fewer than two parts" {
                    let lines = seq { "path"; "path|" }
                    
                    let result = parseHashCacheLines lines
                    
                    Expect.isEmpty result ""
                }
                test "skips if lines has more than two parts" {
                    let lines = seq { "path|hash|something" }
                    
                    let result = parseHashCacheLines lines
                    
                    Expect.isEmpty result ""
                }
                test "can parse valid line" {
                    let lines = seq { "path|hash" }
                    let expected = [ ("path", "hash") ] |> Map.ofList
                    
                    let result = parseHashCacheLines lines
                    
                    Expect.equal result expected ""
                } ]
            testList "mapHashMapToLines" [
                test "can parse valid line" {
                    let map = [ ("path", "hash") ] |> Map.ofList
                    let expected = seq { "path|hash" }
                    
                    let result = mapHashMapToLines map
                    
                    Expect.sequenceEqual result expected ""
                } ]
            testList "normalizeManifestPartialPath" [
                test "results in correct path separator" {
                    let path = "a\\windows\\dir"
                    let expected = Path.Combine("a", "windows", "dir")
                    let result = normalizeManifestPartialPath path
                    
                    Expect.equal result expected ""
                } ]
            testList "mapFileToRequest" [
                test "maps correctly" {
                    let hash = "hash"
                    let remotePath = "http://remote.path"
                    let destDir = "dest"
                    let file = ProductManifest.File("a\\windows\\dir", hash, 0, remotePath)
                    let expected = { RemotePath = remotePath; TargetPath = Path.Combine(destDir, "a", "windows", "dir"); ExpectedHash = hash }
                    
                    let result = mapFileToRequest destDir file
                    
                    Expect.equal result expected ""
                } ]
            testList "filterByUpdateRequired" [
                test "only returns products that require update" {
                    let playable = { product with Sku = "Playable" }
                    let needsUpdate = { product with Sku = "NeedsUpdate" }
                    let products = [ Playable playable ; RequiresUpdate needsUpdate ]
                    
                    let result = filterByUpdateRequired Dev Set.empty products
                    
                    Expect.hasLength result 1 ""
                    Expect.allEqual result needsUpdate ""
                }
                test "epic excludes all if no override specified" {
                    let products = [ RequiresUpdate product ]
                    
                    let result = filterByUpdateRequired (Epic EpicDetails.Empty) Set.empty products
                    
                    Expect.isEmpty result ""
                }
                test "epic excludes all except override" {
                    let override1 = { product with Sku = "1" }
                    let override2 = { product with Sku = "2" }
                    let notOverride = { product with Sku = "3" }
                    let products = [ override1; override2; notOverride ] |> List.map RequiresUpdate
                    let force = [ override1; override2 ] |> List.map (fun p -> p.Sku) |> Set.ofList 
                    
                    let result = filterByUpdateRequired (Epic EpicDetails.Empty) force products
                    
                    Expect.hasLength result 2 ""
                    Expect.all result (fun p -> p.Sku <> notOverride.Sku) ""
                }
                test "steam excludes all if no override specified" {
                    let products = [ RequiresUpdate product ]
                    
                    let result = filterByUpdateRequired Steam Set.empty products
                    
                    Expect.isEmpty result ""
                }
                test "steam excludes all except override" {
                    let override1 = { product with Sku = "1" }
                    let override2 = { product with Sku = "2" }
                    let notOverride = { product with Sku = "3" }
                    let products = [ override1; override2; notOverride ] |> List.map RequiresUpdate
                    let force = [ override1; override2 ] |> List.map (fun p -> p.Sku) |> Set.ofList 
                    
                    let result = filterByUpdateRequired Steam force products
                    
                    Expect.hasLength result 2 ""
                    Expect.all result (fun p -> p.Sku <> notOverride.Sku) ""
                }
                test "frontier includes all" {
                    let products = [ product; product ]
                    let platform = Frontier FrontierDetails.Empty
                    
                    let result = filterByUpdateRequired platform Set.empty (products |> List.map RequiresUpdate)
                    
                    Expect.sequenceEqual result products ""
                } ]
        ]
