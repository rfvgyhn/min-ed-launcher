namespace EdLauncher

module Program =
    open System.IO
    open System.Reflection
    open FsConfig
    open Steam
    open Rop

    let getSettings args =
        let path = Path.Combine(Environment.configDir, "elite-dangerous-launcher")
        match FileIO.ensureDirExists path with
        | Error msg -> sprintf "Unable to find/create configuration directory at %s - %s" path msg |> Error
        | Ok settingsDir ->
            let settingsPath = Path.Combine(settingsDir, "settings.json")
            if not (File.Exists(settingsPath)) then
                use settings = typeof<Steam>.GetTypeInfo().Assembly.GetManifestResourceStream("EdLauncher.settings.json")
                use file = File.OpenWrite(settingsPath)
                settings.CopyTo(file)
            |> ignore
                
            Settings.parseConfig settingsPath
            |> Result.mapError (fun e ->
                match e with
                | BadValue (key, value) -> sprintf "Bad Value: %s - %s" key value
                | ConfigParseError.NotFound key -> sprintf "Key not found: %s" key
                | NotSupported key -> sprintf "Key not supported: %s" key)
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
        
        
