module MinEdLauncher.Program

open System
open System.Collections
open System.IO
open System.Net.Http
open System.Reflection
open System.Text.RegularExpressions
open System.Threading
open FsConfig
open FsToolkit.ErrorHandling

let getSettings (assembly: Assembly) args =
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
        use cts = new CancellationTokenSource()

        try
            let assembly = Assembly.GetExecutingAssembly()
            let version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion
            let args =
                if Console.IsInputRedirected then
                    let input = Console.ReadLine()
                    if input <> null then
                        Log.debug $"STDIN: {input}"
                        let stdin =
                            Regex.Matches(input, @"[^\s""']+|""([^""]*)""|'([^']*)'", RegexOptions.Multiline)
                            |> Seq.filter (fun m -> not <| String.IsNullOrEmpty(m.Value))
                            |> Seq.map (fun m -> m.Value.Trim('\'', '"'))
                            |> Seq.toArray
                        Array.append stdin argv
                    else
                        argv
                else argv
            
            logRuntimeInfo version args
            
            let run =
                getSettings assembly args
                |> TaskResult.bind (fun settings ->
                    taskResult {
                        Log.debug $"Settings: %A{settings}"
                        Directory.SetCurrentDirectory(settings.CbLauncherDir)
                        let! runResult = App.run settings version cts.Token |> TaskResult.mapError App.AppError.toDisplayString

                        if not settings.AutoQuit && not cts.Token.IsCancellationRequested then
                            Console.waitForQuit()
                            
                        return runResult
                    })
                |> TaskResult.teeError (fun msg ->
                    Log.error msg
                    Console.waitForQuit())
                |> TaskResult.defaultValue 1
            run.GetAwaiter().GetResult()
            
        with
        | :? HttpRequestException as e ->
            let at =
                e.StackTrace.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                |> Seq.map (fun line -> line.TrimStart())
                |> Seq.filter (fun line -> line.StartsWith("at MinEdLauncher"))
                |> Seq.tryHead
                |> Option.defaultValue ""
            Log.error $"Network request failed. Are you connected to the internet? - {e.Message} {at}"
            Console.waitForQuit()
            1
        | e ->
            Log.error $"Unhandled exception: {e}"
            Console.waitForQuit()
            1
