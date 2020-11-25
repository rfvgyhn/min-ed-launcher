namespace EdLauncher

module Result =
    let defaultValue value = function
        | Ok v -> v
        | Error _ -> value
    let defaultWith (defThunk: 'T -> 'U) = function
        | Ok v -> v
        | Error e -> defThunk e

module Seq =
    let chooseResult r = r |> Seq.choose (fun r -> match r with | Error _ -> None | Ok v -> Some v)
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
        | :? JsonException -> Error "Invalid json document"
        | e -> Error $"Couldn't open file at {path} - {e}"
    let parseStream (stream:Stream) =
        try
            Ok <| JsonDocument.Parse(stream)
        with
        | :? JsonException -> Error "Invalid json document"
    let rootElement (doc:JsonDocument) = Ok doc.RootElement
    let parseProp (prop:string) (element:JsonElement) =
        match element.TryGetProperty(prop) with
        | true, prop -> Ok prop
        | false, _ -> Error <| sprintf "Unable to find '%s' in json document" prop
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
        | false, _ -> Error <| sprintf "Unable to parse '%s' as int64" (prop.ToString())
    let asInt (prop:JsonElement) =
        match prop.TryGetInt32() with
        | true, value -> Ok value
        | false, _ -> Error <| sprintf "Unable to parse '%s' as int" (prop.ToString())
    let toInt (prop:JsonElement) =
        let str = prop.ToString()
        match Int32.TryParse(str) with
        | true, value -> Ok value
        | false, _ -> Error <| sprintf "Unable to convert string to int '%s'" str
    let asDateTime (prop:JsonElement) =
        let str = prop.ToString()
        match DateTime.TryParse(str) with
        | true, value -> Ok value
        | false, _ -> Error <| $"Unable to parse string as DateTime '%s{str}'"
    let asUri (prop:JsonElement) =
        match Uri.TryCreate(prop.ToString(), UriKind.Absolute) with
        | true, value -> Ok value
        | false, _ -> Error <| sprintf "Unable to parse '%s' as Uri" (prop.ToString())
    let toString (prop:JsonElement) =
        Ok <| prop.ToString()
    let asVersion (prop:JsonElement) =
        match Version.TryParse(prop.ToString()) with
        | true, value -> Ok value
        | false, _ -> Error <| sprintf "Unable to parse '%s' as Version" (prop.ToString())
    let asBool (prop:JsonElement) =
        match bool.TryParse(prop.ToString()) with
        | true, value -> Ok value
        | false, _ -> Error <| sprintf "Unable to parse '%s' as boolean" (prop.ToString())

module Xml =
    open System.Xml
    let getValue xpath (file: string) =
        let doc = XmlDocument()
        doc.Load file
        doc.SelectNodes xpath
            |> Seq.cast<XmlNode>
            |> Seq.map (fun node -> node.InnerText)
            |> Seq.tryExactlyOne

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
    
module SHA1 =
    open System.Text
    open System.Security.Cryptography
    open System.IO
    
    let hashString (str:string) =
        let bytes = Encoding.ASCII.GetBytes(str)
        use crypto = new SHA1CryptoServiceProvider()
        crypto.ComputeHash(bytes)
        
    let hashFile (filePath:string) =
        try
            use file = File.OpenRead(filePath)
            use crypto = new SHA1CryptoServiceProvider()
            crypto.ComputeHash(file) |> Ok
        with
        | e -> Error e

module Hex =
    open System
    
    let toString bytes = BitConverter.ToString(bytes).Replace("-","")
    let toStringTrunc length bytes = BitConverter.ToString(bytes).Replace("-","").Substring(0, length)

module Task =
    open System.Threading.Tasks
    open System.Collections.Generic
    let fromResult r = Task.FromResult(r)
    let whenAll (tasks: IEnumerable<Task<'t>>) = Task.WhenAll(tasks)

module FileIO =
    open System
    open System.IO
    open FSharp.Control.Tasks.NonAffine
    
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

module Regex =
    open System.Text.RegularExpressions
    
    let replace pattern (replacement: string) input =
        Regex.Replace(input, pattern, replacement)

module Environment =
    open System
    open System.Runtime.InteropServices
    
    let expandEnvVars name =
        if String.IsNullOrEmpty(name) then
            name
        else
            // Platform checks needed until corefx supports platform specific vars
            // https://github.com/dotnet/corefx/issues/28890
            let str =
                if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
                    name
                    |> Regex.replace @"\$(\w+)" "%$1%"
                    |> Regex.replace "^~" "%HOME%"
                else
                    name
            
            Environment.ExpandEnvironmentVariables(str)
    let configDir =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        else
            let xdgConfig = expandEnvVars("$XDG_CONFIG_HOME")
            if String.IsNullOrEmpty(xdgConfig) then
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            else
                xdgConfig    
                
            