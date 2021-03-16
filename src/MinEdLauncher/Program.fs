module MinEdLauncher.Program

open System.IO
open System.Reflection
open FsConfig
open Steam
open Rop

let getSettings args =
    let path = Environment.configDir
    match FileIO.ensureDirExists path with
    | Error msg -> Error $"Unable to find/create configuration directory at %s{path} - %s{msg}"  
    | Ok settingsDir ->
        let settingsPath = Path.Combine(settingsDir, "settings.json")
        Log.debug $"Reading settings from '%s{settingsPath}'"
        if not (File.Exists(settingsPath)) then
            use settings = typeof<Steam>.GetTypeInfo().Assembly.GetManifestResourceStream("MinEdLauncher.settings.json")
            use file = File.OpenWrite(settingsPath)
            settings.CopyTo(file)
        |> ignore
            
        Settings.parseConfig settingsPath
        |> Result.mapError (fun e ->
            match e with
            | BadValue (key, value) -> $"Bad Value: %s{key} - %s{value}"
            | ConfigParseError.NotFound key -> $"Key not found: %s{key}"
            | NotSupported key -> $"Key not supported: %s{key}")
        >>= Settings.getSettings args

[<EntryPoint>]
let main argv =
    async {
        try
            do! Async.SwitchToThreadPool ()
            Log.debug $"Args: %A{argv}"
            let settings = getSettings argv
            Log.debug $"Settings: %A{settings}"
            return! match settings with
                    | Ok settings -> App.run settings |> Async.AwaitTask
                    | Error msg -> async { Log.error msg; return 1 }
        with
        | e -> Log.error $"Unhandled exception: {e}"; return 1
    } |> Async.RunSynchronously
