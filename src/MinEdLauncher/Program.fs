module MinEdLauncher.Program

open System
open System.IO
open System.Reflection
open System.Threading
open FsConfig
open FSharp.Control.Tasks.NonAffine
open Steam

let getSettings args =
    let path = Environment.configDir
    match FileIO.ensureDirExists path with
    | Error msg -> Error $"Unable to find/create configuration directory at %s{path} - %s{msg}" |> Task.fromResult
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
        |> function
            | Ok c -> task {
                let! settings = Settings.getSettings args AppContext.BaseDirectory c
                return settings }
            | Error msg -> Error msg |> Task.fromResult

[<EntryPoint>]
let main argv =
    async {
        use cts = new CancellationTokenSource()
        Console.CancelKeyPress.AddHandler (fun s e ->
            cts.Cancel()
            e.Cancel <- true)
        try
            do! Async.SwitchToThreadPool ()
            Log.debug $"Args: %A{argv}"
            let! settings = getSettings argv |> Async.AwaitTask
            Log.debug $"Settings: %A{settings}"
            return! match settings with
                    | Ok settings -> App.run settings cts.Token |> Async.AwaitTask
                    | Error msg -> async { Log.error msg; return 1 }
        with
        | e -> Log.error $"Unhandled exception: {e}"; return 1
    } |> Async.RunSynchronously
