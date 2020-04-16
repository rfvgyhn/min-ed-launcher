namespace EdLauncher
    module Product =
        open System
        open System.Diagnostics
        open System.IO
        open EdLauncher.Types
        
        let createArgString vr (lang: string option) machineToken sessionToken machineId timestamp watchForCrashes platform hashFile (product:Product) =
            let targetOptions = String.Join(" ", [
                if lang.IsSome then "/language " + lang.Value 
                match platform, product.SteamAware with
                    | Steam _, true -> "/steam"
                    | _, _ -> ()
                match vr with
                    | Vr -> "/vr"
                    | _ -> "/novr"
                if not (String.IsNullOrEmpty(product.GameArgs)) then product.GameArgs ])
            let online =
                match product.Mode with
                | Offline -> false
                | Online -> true
            let serverToken = if online then sprintf "ServerToken %s %s %s" machineToken sessionToken product.ServerArgs else ""
            let combined = sprintf "\"%s\" %s" serverToken targetOptions
            let exe = product.Executable |> Option.defaultValue ""
            let machineId = machineId |> Option.defaultValue ""
            let fullExePath = Path.Combine(product.Directory, exe)
            let exeHash = fullExePath |> hashFile |> Result.map Hex.toString |> Result.map (fun p -> p.ToUpperInvariant()) |> Result.defaultValue ""
            if watchForCrashes && online then
                let version = product.Version |> Option.map (fun v -> v.ToString()) |> Option.defaultValue ""
                sprintf "/Executable \"%s\" /ExecutableArgs %s /MachineToken %s /Version %s /AuthToken %s /MachineId %s /Time %s /ExecutableHash %s"
                    fullExePath combined machineToken version sessionToken machineId (timestamp.ToString()) exeHash
            else
                combined
                
        type RunnableProduct =
            { Executable: FileInfo
              Version: Version
              SteamAware: bool
              Mode: ProductMode
              ServerArgs: string }
        let validateForRun launcherDir watchForCrashes (product:Product) =
            match product.Executable, product.Version with
            | Some exe, Some version ->
                let productFullPath = Path.Combine(product.Directory, exe)
                let watchDogFullPath = if product.UseWatchDog64 then Path.Combine(launcherDir, "WatchDog64.exe") else Path.Combine(launcherDir, "WatchDog.exe") 
                if not (File.Exists(productFullPath)) then
                    Error <| sprintf "Unable to find product exe at '%s'" productFullPath
                elif watchForCrashes && not (File.Exists(watchDogFullPath)) then
                    Error <| sprintf "Unable to find watchdog exe at '%s'" watchDogFullPath
                else
                    let exePath = if watchForCrashes then watchDogFullPath else productFullPath
                    Ok { Executable = FileInfo(exePath)
                         Version = version
                         SteamAware = product.SteamAware
                         Mode = product.Mode
                         ServerArgs = product.ServerArgs }
            | None, _ -> Error "No executable specified"
            | _, None -> Error "No version specified"
            
        let run args (product:RunnableProduct)  =
            let startInfo = ProcessStartInfo()
            //startInfo.FileName <- product.Executable.FullName
            startInfo.FileName <- "python3"
            startInfo.WorkingDirectory <- product.Executable.DirectoryName
            startInfo.Arguments <- sprintf "\"%s\" waitforexitandrun \"%s\" %s" "/home/chris/.local/share/Steam/steamapps/common/Proton 4.2/proton" product.Executable.FullName args
            //startInfo.CreateNoWindow <- true
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            startInfo.EnvironmentVariables.Add("STEAM_COMPAT_DATA_PATH", "/home/chris/.local/share/Steam/steamapps/compatdata/359320")
            
            try
                printfn "StartInfo: %s" startInfo.FileName
                printfn "StartInfo: %s" startInfo.WorkingDirectory
                printfn "StartInfo: %s" startInfo.Arguments
                //Ok ""
                Process.Start(startInfo) |> Ok
            with
            | e -> Error e.Message
