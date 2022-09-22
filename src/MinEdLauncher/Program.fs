module MinEdLauncher.Program

open System
open System.Collections
open System.IO
open System.Reflection
open System.Threading
open FsConfig
open FsToolkit.ErrorHandling
open Steam

let assembly = typeof<Steam>.GetTypeInfo().Assembly
let getSettings args =
    let path = Environment.configDir
    match FileIO.ensureDirExists path with
    | Error msg -> Error $"Unable to find/create configuration directory at %s{path} - %s{msg}" |> Task.fromResult
    | Ok settingsDir ->
        let settingsPath = Path.Combine(settingsDir, "settings.json")
        Log.debug $"Reading settings from '%s{settingsPath}'"
        if not (File.Exists(settingsPath)) then
            use settings = assembly.GetManifestResourceStream("MinEdLauncher.settings.json")
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

let logRuntimeInfo version args =
    Log.info $"Elite Dangerous: Minimal Launcher - v{version}"
    let envVars = Environment.GetEnvironmentVariables()
                  |> Seq.cast<DictionaryEntry>
                  |> Seq.filter (fun e -> e.Key = "WINEPREFIX" || e.Key = "STEAM_COMPAT_DATA_PATH")
                  |> Seq.map (fun e -> $"{e.Key}={e.Value}")
                  |> String.join Environment.NewLine
    Log.debug $"""
    Args: %A{args}
    OS: %s{RuntimeInformation.getOsIdent()}
    Env: %s{envVars}
    """

[<EntryPoint>]
let main argv =
    async {
        use cts = new CancellationTokenSource()

        try
            do! Async.SwitchToThreadPool ()
            let version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion
            let args =
                if Console.IsInputRedirected then
                    let stdin = Console.ReadLine().Split(' ') |> Array.filter (fun s -> String.IsNullOrEmpty(s) |> not)
                    Array.append stdin argv
                else argv
            
            logRuntimeInfo version args
            
            return!
                getSettings args
                |> TaskResult.bind (fun settings ->
                    taskResult {
                        Log.debug $"Settings: %A{settings}"
                        Directory.SetCurrentDirectory(settings.CbLauncherDir)
                        let! runResult = App.run settings version cts.Token |> TaskResult.mapError App.AppError.toDisplayString

                        if not settings.AutoQuit && not cts.Token.IsCancellationRequested then
                            printfn "Press any key to quit..."
                            Console.ReadKey() |> ignore
                            
                        return runResult
                    })
                |> TaskResult.teeError Log.error
                |> TaskResult.defaultValue 1
                |> Async.AwaitTask
        with
        | e -> Log.error $"Unhandled exception: {e}"; return 1
    } |> Async.RunSynchronously
