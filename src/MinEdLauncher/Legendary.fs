module Legendary

open System
open System.IO
open System.Runtime.InteropServices
open MinEdLauncher
open FsToolkit.ErrorHandling
open Rop

let parseAccessToken (timeProvider: TimeProvider) json = result {
    let! root = json |> Json.rootElement
    do! root
        |> Json.parseProp "expires_at"
        >>= Json.asDateTime
        >>= (fun expires -> expires > timeProvider.GetUtcNow()
                            |> Result.requireTrue "Epic access token is expired. Re-authenticate with Legendary/Heroic")
    
    return! root |> Json.parseProp "access_token" >>= Json.asString
}

let getAccessToken() =
    let potentialPaths =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            [
                Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH")
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "legendary")
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "heroic", "legendaryConfig", "legendary")
            ]
        else if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
            [
                Environment.GetEnvironmentVariable("LEGENDARY_CONFIG_PATH")
                Environment.configDirFor "legendary"
                Path.Combine(Environment.configDirFor "heroic", "legendaryConfig", "legendary")
            ]
        else
            []
        |> List.filter (fun p -> not (String.IsNullOrWhiteSpace(p)))
        
    potentialPaths
    |> List.map (fun p -> FileInfo(Path.Combine(p, "user.json")))
    |> List.filter _.Exists
    |> List.sortByDescending _.LastWriteTime // Assume newest file has latest access token
    |> List.tryHead
    |> Option.map _.FullName
    |> Result.requireSome "Couldn't find Legendary auth file"
    |> Result.teeError (fun _ ->
        let paths = if potentialPaths.Length = 0 then "None" else String.Join($"{Environment.NewLine}    ", potentialPaths)
        Log.debug $"Legendary locations checked:{Environment.NewLine}{paths}"
    )
    >>= Json.parseFile
    >>= (parseAccessToken TimeProvider.System)
