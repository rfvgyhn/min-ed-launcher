namespace MinEdLauncher

open System.IO

module Task =
    open System.Threading.Tasks
    open System.Collections.Generic
    let fromResult r = Task.FromResult(r)
    let whenAll (tasks: IEnumerable<Task<'t>>) = Task.WhenAll(tasks)
    
    let bindTaskResult (f: 'T -> Task<Result<'U, 'TError>>) (result: Task<Result<'T, 'TError>>) = task { 
        match! result with
        | Ok v -> return! f v
        | Error m -> return Error m 
    }
    let bindResult (f: 'T -> Result<'U, 'TError>) (result: Task<Result<'T, 'TError>>) = task { 
        match! result with
        | Ok v -> return f v
        | Error m -> return Error m 
    }
    let mapResult f (result: Task<Result<'T, 'TError>>) = task { 
        match! result with
        | Ok v -> return Ok (f v)
        | Error m -> return Error m
    }

module Result =
    open System.Threading.Tasks
    
    let defaultValue value = function
        | Ok v -> v
        | Error _ -> value
    let defaultWith (defThunk: 'T -> 'U) = function
        | Ok v -> v
        | Error e -> defThunk e
    let bindTask f = function
        | Ok v -> f v
        | Error v -> Error v |> Task.fromResult
    let mapTask f (result: Result<Task<'T>, 'TError>) =
        match result with
        | Ok v -> task {
            let! result = v
            return f result }
        | Error v -> Error v |> Task.fromResult
        

module Seq =
    open System.Linq
    
    let chooseResult r = r |> Seq.choose (fun r -> match r with | Error _ -> None | Ok v -> Some v)
    let chooseResultDoErr action r =
        r |> Seq.choose (function
                          | Error e ->
                              action e
                              None
                          | Ok v -> Some v)
    let intersect (itemsToInclude: seq<'T>) (source: seq<'T>) = source.Intersect(itemsToInclude)
    let mapOrFail mapping source =
        let rec loop acc source =
            match (source |> Seq.isEmpty) with
            | true -> Ok (List.rev acc)
            | false ->
                mapping (source |> Seq.head) |> Result.bind (fun item -> loop (item::acc) (source |> Seq.tail))
        loop [] source
    
module List =
    open System.Threading.Tasks
    
    let mapTasksSequential (mapping: 'T -> Task<'U>) list = task {
        let! result =
            match list with
            | [] -> [] |> Task.fromResult
            | head :: tail -> task {
                let firstTask = task {
                    let! result = mapping head
                    return ([], result) }

                let! tasks, lastTask =
                    List.fold (fun prevTask arg -> task {
                        let! accum, prev = prevTask
                        let accum = prev :: accum

                        let! result = mapping arg

                        return (accum, result) }) firstTask tail
                return (lastTask :: tasks) }
        return (result |> Seq.toList) }
    let chooseTasksSequential (chooser: 'T -> Task<'U option>) list = task {
        let! items = list |> mapTasksSequential chooser
        return items |> List.choose id
    }
    
module Map =
    // https://stackoverflow.com/a/50925864/182821
    let keys<'k, 'v when 'k : comparison> (map : Map<'k, 'v>) =
        Map.fold (fun s k _ -> Set.add k s) Set.empty map
    // https://stackoverflow.com/a/3974842/182821
    let merge map1 map2 =
        Map.fold (fun acc key value -> Map.add key value acc) map1 map2
    
module Rop =
    let (>>=) switchFunction twoTrackInput = Result.bind twoTrackInput switchFunction

module Union =
    open Microsoft.FSharp.Reflection
    let getCaseName (e:'a) = (FSharpValue.GetUnionFields(e, typeof<'a>) |> fst).Name

module Json =
    open System
    open System.Collections.Generic
    open System.IO
    open System.Text.Json
    
    let parseFile path =
        try
            use file = File.OpenRead(path)
            JsonDocument.Parse(file) |> Ok
        with
        | :? JsonException as e -> Error $"Couldn't parse json - {e.Message}"
        | e -> Error $"Couldn't open file at {path} - {e}"
    let parseStream (stream:Stream) =
        try
            Ok <| JsonDocument.Parse(stream)
        with
        | :? JsonException as e ->
            let content =
                try
                    if stream.CanSeek then
                        stream.Seek(0L, SeekOrigin.Begin) |> ignore
                        use reader = new StreamReader(stream)
                        $"{Environment.NewLine}{reader.ReadToEnd()}"
                    else ""
                with _ -> ""
            Error $"Couldn't parse json - {e.Message}{content}"
    let rootElement (doc:JsonDocument) = Ok doc.RootElement
    let parseProp (prop:string) (element:JsonElement) =
        match element.TryGetProperty(prop) with
        | true, prop -> Ok prop
        | false, _ -> Error $"Unable to find '%s{prop}' in json document"
    let parseEitherProp (prop1:string) (prop2:string) (element:JsonElement) =
        match element.TryGetProperty(prop1) with
        | true, prop -> Ok prop
        | false, _ ->
            match element.TryGetProperty(prop2) with
            | true, prop -> Ok prop
            | false, _ -> Error $"Unable to find '%s{prop1}' or '%s{prop2}' in json document"
    let mapArray f (element:JsonElement) =
        try
            Ok <| element.EnumerateArray()
        with
        | :? InvalidOperationException -> Error "Element is not an array"
        |> Result.map (fun array -> seq { for p in array do yield f p })
    let arrayTryFind f (element:JsonElement) =
        try
            element.EnumerateArray() :> IEnumerable<JsonElement> |> Ok
        with
        | :? InvalidOperationException -> Error "Element is not an array"
        |> Result.map (Seq.tryFind f)
    let asEnumerable (prop:JsonElement) = Ok <| seq { for p in prop.EnumerateArray() do yield p }
    let asInt64 (prop:JsonElement) =
        match prop.TryGetInt64() with
        | true, value -> Ok value
        | false, _ -> Error $"Unable to parse '%s{prop.ToString()}' as int64"
    let asInt (prop:JsonElement) =
        match prop.TryGetInt32() with
        | true, value -> Ok value
        | false, _ -> Error $"Unable to parse '%s{prop.ToString()}' as int"
    let toInt (prop:JsonElement) =
        let str = prop.ToString()
        match Int32.TryParse(str) with
        | true, value -> Ok value
        | false, _ -> Error $"Unable to convert string to int '%s{str}'"
    let toInt64 (prop:JsonElement) =
        let str = prop.ToString()
        match Int64.TryParse(str) with
        | true, value -> Ok value
        | false, _ -> Error $"Unable to convert string to long '%s{str}'"
    let asDateTime (prop:JsonElement) =
        let str = prop.ToString()
        match DateTime.TryParse(str) with
        | true, value -> Ok value
        | false, _ -> Error $"Unable to parse string as DateTime '%s{str}'"
    let asUri (prop:JsonElement) =
        match Uri.TryCreate(prop.ToString(), UriKind.Absolute) with
        | true, value -> Ok value
        | false, _ -> Error $"Unable to parse '%s{prop.ToString()}' as Uri"
    let toString (prop:JsonElement) =
        Ok <| prop.ToString()
    let asVersion (prop:JsonElement) =
        match Version.TryParse(prop.ToString()) with
        | true, value -> Ok value
        | false, _ -> Error  $"Unable to parse '%s{prop.ToString()}' as Version"
    let asBool (prop:JsonElement) =
        match bool.TryParse(prop.ToString()) with
        | true, value -> Ok value
        | false, _ -> Error $"Unable to parse '%s{prop.ToString()}' as boolean"

module RuntimeInformation =
    open System.Runtime.InteropServices
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

module Uri =
    open System
    open System.Web
        
    let addQueryParams parameters (uri:Uri) =
        let builder = UriBuilder(uri)
        let query = HttpUtility.ParseQueryString(builder.Query)
        parameters |> List.iter (fun (key, value) -> query.Add(key, value.ToString())) 
        builder.Query <- query.ToString()
        builder.Uri
        
    let addQueryParam kvp uri = addQueryParams [ kvp ] uri
    
module String =
    open System
    open System.Collections.Generic
    let join (separator: string) (values: IEnumerable<'T>) = String.Join(separator, values)
    let toLower (str: string) = str.ToLower()
    let ensureEndsWith (value: char) (str: string) = if str.EndsWith(value) then str else $"%s{str}%c{value}"
    
module Int64 =
    open System
    
    let toFriendlyByteString (n: int64) =
        let suf = [| "B"; "KB"; "MB"; "GB"; "TB"; "PB"; "EB" |] //Longs run out around EB
        if n = 0L then
            "0" + suf.[0]
        else
            let bytes = float (Math.Abs(n))
            let place = Convert.ToInt32(Math.Floor(Math.Log(bytes, float 1024)))
            let num = Math.Round(bytes / Math.Pow(float 1024, float place), 1)
            (Math.Sign(n) * int num).ToString() + suf.[place];
    
module StreamExtensions =
    open System
    open System.Threading
    
    type Stream with
        member source.CopyToAsync(destination: Stream, bufferSize: int, ?progress: IProgress<int>, ?cancellationToken: CancellationToken) = task {
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            if source = null then
                raise (ArgumentNullException(nameof source))
            if not source.CanRead then
                raise (ArgumentException("Source stream must be readable", nameof source))
            if destination = null then
                raise (ArgumentNullException(nameof(destination)));
            if not destination.CanWrite then
                raise (ArgumentException("Destination stream must be writable", nameof destination))
            if bufferSize < 0 then
                raise (ArgumentOutOfRangeException(nameof bufferSize))
            
            let buffer = Array.zeroCreate bufferSize
            
            // Tasks don't support tail call optimization so use a while loop instead of recursion
            // https://github.com/crowded/ply/issues/14
            let mutable write = true
            while write do
                let! bytesRead = source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                if bytesRead > 0 then
                    do! destination.WriteAsync(buffer, 0, bytesRead, cancellationToken)
                    progress |> Option.iter (fun p -> p.Report(bytesRead))
                else
                    write <- false }

module HttpClientExtensions =
    open StreamExtensions
    open System
    open System.Net.Http
    open System.Threading
    
    type HttpClient with
        member client.DownloadAsync(requestUri: string, destination: Stream, ?bufferSize: int, ?progress: IProgress<int>, ?cancellationToken: CancellationToken) = task {
            let cancellationToken = defaultArg cancellationToken CancellationToken.None
            let bufferSize = defaultArg bufferSize 8192
            
            use! response = client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead)
            use! download = response.Content.ReadAsStreamAsync()

            match progress with
            | Some progress -> do! download.CopyToAsync(destination, bufferSize, progress, cancellationToken)
            | None -> do! download.CopyToAsync(destination, cancellationToken) }

module SHA1 =
    open System.Text
    open System.Security.Cryptography
    open System.IO
    
    let hashString (str:string) =
        let bytes = Encoding.ASCII.GetBytes(str)
        use crypto = SHA1.Create()
        crypto.ComputeHash(bytes)
        
    let hashFile (filePath:string) =
        try
            use file = File.OpenRead(filePath)
            use crypto = SHA1.Create()
            crypto.ComputeHash(file) |> Ok
        with
        | e -> Error e

module Hex =
    open System
    open System.Text
    
    let toString bytes = BitConverter.ToString(bytes).Replace("-","")
    let toStringTrunc length bytes = BitConverter.ToString(bytes).Replace("-","").Substring(0, length)
    let private iso88591GetString (bytes: byte[]) = Encoding.GetEncoding("ISO-8859-1").GetString(bytes) 
    let parseIso88591String (str: string) =
        str
        |> Seq.chunkBySize 2
        |> Seq.map (fun chars -> Convert.ToByte(String(chars), 16))
        |> Seq.toArray
        |> iso88591GetString

module FileIO =
    open System
    open System.IO
    
    let hasWriteAccess directory =
        try
            let temp = Path.Combine(directory, "deleteme.txt")
            File.WriteAllText(temp, "")
            File.Delete(temp)
            true
        with
        | :? UnauthorizedAccessException -> false

    let deleteFileIfTooBig maxSize path =
        let file = FileInfo(path)
        if file.Exists && file.Length > maxSize then
            File.Delete(path)
            true
        else
            false

    let openRead path =
        try
            Ok <| File.OpenRead(path)
        with
        | e -> Error e.Message
    
    let readAllText path = task {
        try
            let! result = File.ReadAllTextAsync(path) 
            return Ok result
        with
        | e -> return Error e.Message
    }
    
    let writeAllText path text = task {
        try
            let! result = File.WriteAllTextAsync(path, text) 
            return Ok result
        with
        | e -> return Error e.Message
    }
    
    let writeAllLines path lines = task {
        try
            let! result = File.WriteAllLinesAsync(path, lines) 
            return Ok result
        with
        | e -> return Error e.Message
    }
    
    let appendAllLines path lines = task {
        try
            let! result = File.AppendAllLinesAsync(path, lines) 
            return Ok result
        with
        | e -> return Error e.Message
    }
    
    let readAllLines path = task {
        try
            let! result = File.ReadAllLinesAsync(path) 
            return Ok result
        with e -> return Error e.Message
    }

    let ensureFileExists path = task {
        try
            do! File.WriteAllTextAsync(path, "")
            return Ok ()
        with
        | e -> return Error e.Message
    }

    let ensureDirExists directory =
        try
            Directory.CreateDirectory directory |> ignore
            Ok directory
        with
        | :? UnauthorizedAccessException -> Error "Insufficient permissions"
        | :? ArgumentNullException
        | :? ArgumentException -> Error "Path is empty or contains invalid characters"
        | :? PathTooLongException -> Error "Exceeds maximum length"
        | :? DirectoryNotFoundException -> Error "The specified path is invalid (for example, it is on an unmapped drive)."
        | :? IOException -> Error "The directory specified by path is a file or the network name is not known."
        | :? NotSupportedException -> Error @"Contains a colon character (:) that is not part of a drive label (""C:\"")."
        
    // https://stackoverflow.com/a/2553245/182821
    let mergeDirectories (target: string) (source: string) =
        let sourcePath = source.TrimEnd(Path.DirectorySeparatorChar, ' ')
        let targetPath = target.TrimEnd(Path.DirectorySeparatorChar, ' ')
        Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
        |> Seq.groupBy (fun s -> Path.GetDirectoryName(s))
        |> Seq.iter (fun (folder, files) ->
            let targetFolder = folder.Replace(sourcePath, targetPath)
            Directory.CreateDirectory(targetFolder) |> ignore
            files
            |> Seq.iter (fun file ->
                let targetFile = Path.Combine(targetFolder, Path.GetFileName(file))
                if File.Exists(targetFile) then File.Delete(targetFile)
                File.Move(file, targetFile)))
        Directory.Delete(source, true);

module Console =
    open System
    let readPassword () =
        let rec readMask pw =
            let k = Console.ReadKey(true)
            match k.Key with
            | ConsoleKey.Enter -> pw
            | ConsoleKey.Backspace ->
                match pw with
                | [] -> readMask []
                | _::t ->
                    Console.Write "\b \b"
                    readMask t
            | _ ->
                Console.Write "*"
                readMask (k.KeyChar::pw)
        let password = readMask [] |> Seq.rev |> String.Concat
        Console.WriteLine ()
        password
    let consumeAvailableKeys () =
        while Console.KeyAvailable do
            Console.ReadKey() |> ignore

module Regex =
    open System.Text.RegularExpressions
    
    let replace pattern (replacement: string) input = Regex.Replace(input, pattern, replacement)
    let split pattern input = Regex.Split(input, pattern)

module Environment =
    open System
    open System.Runtime.InteropServices
    
    [<Literal>]
    let private AppFolderName = "min-ed-launcher"
    let private home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
    let private xdgDir var fallback =
        let xdgPath = Environment.GetEnvironmentVariable($"XDG_%s{var}")
        if String.IsNullOrEmpty(xdgPath) then
            Path.Combine(fallback, AppFolderName)
        else
            Path.Combine(xdgPath, AppFolderName)
    let private localAppData subDir =
        let appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        Path.Combine(appData, AppFolderName, subDir)
    
    let configDir =
        let specialFolder =
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                Environment.SpecialFolder.LocalApplicationData
            else
                Environment.SpecialFolder.ApplicationData
        
        let path = Environment.GetFolderPath(specialFolder)
        Path.Combine(path, AppFolderName)
        
    let cacheDir =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            localAppData "cache"
        else
            xdgDir "CACHE_HOME" (Path.Combine(home, ".cache"))
                
    let logDir =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            localAppData ""
        else
            xdgDir "STATE_HOME" (Path.Combine(home, ".local", "state"))
        