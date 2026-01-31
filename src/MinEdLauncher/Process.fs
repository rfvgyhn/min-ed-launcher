module MinEdLauncher.Process

open System
open System.ComponentModel
open System.Diagnostics
open MinEdLauncher.Types

let launchProcesses printOutput (processes:LauncherProcess list) =
    processes
    |> List.choose (fun l ->
        try
            let p = Process.Start(l.StartInfo)
            p.BeginErrorReadLine()
            p.BeginOutputReadLine()
            
            if printOutput then
                p.OutputDataReceived.Add(fun a -> if a.Data <> null then printfn $"  %s{a.Data}")
                p.ErrorDataReceived.Add(fun a -> if a.Data <> null then printfn $"  %s{a.Data}")
                
            (p, l) |> Some
        with
        | :? Win32Exception as e ->
            Log.exn e $"""Unable to start process %s{l.Name}
    HRESULT: 0x{e.ErrorCode:X}
    Win32 Error Code: {e.NativeErrorCode}
    """
            None
        | e ->
            Log.exn e $"Unable to start process %s{l.Name}"
            None)

let launchProcessesDelayed (delay: TimeSpan) (processes: LauncherProcess list) = task {
    if delay > TimeSpan.Zero then
        do! Threading.Tasks.Task.Delay(delay)
    return launchProcesses false processes
}

let stopProcesses (timeout: TimeSpan) (processes: (Process * LauncherProcess) list) =
    processes
    |> List.iter (fun (p, l) ->
        use p = p
        if p.HasExited then
            Log.debug $"Process %i{p.Id} already exited"            
        else
            let name = match l with Host _ -> p.ProcessName | Flatpak _ -> l.Name
            Log.debug $"Stopping process %s{name}"
            match l.ShutdownCommand with
            | Some startInfo ->
                try
                    use p = Process.Start(startInfo)
                    if not (p.WaitForExit(timeout)) then
                        Log.warn $"Process did not exit within %i{int timeout.TotalSeconds} seconds"
                    else
                        Log.info $"Stopped process %s{name}"
                with e -> Log.exn e $"Failed to stop process %s{name}"
            | None ->
                match Interop.termProcess timeout p with
                | Ok () -> Log.info $"Stopped process %s{name}"
                | Error msg -> Log.warn msg)
    
let waitForExit (processes: Process list) =
    processes
    |> List.iter(fun p ->
        use p = p
        p.WaitForExit()
    )
    
/// <summary>
/// Override $LD_LIBRARY_PATH with $MEL_LIBRARY_PATH if it's set
/// </summary>
/// <remarks>
/// This allows things like flatpaks to run properly within Steam's runtime environment. Users can set
/// $MEL_LD_LIBRARY_PATH to $LD_LIBRARY_PATH and then clear $LD_LIBRARY_PATH when launching so that only the game
/// process get Steam's runtime environment's $LD_LIBRARY_PATH. Mostly useful for Steam Deck users
/// </remarks>
let overrideLdLibraryPath (startInfo: ProcessStartInfo) =
    System.Environment.GetEnvironmentVariable("MEL_LD_LIBRARY_PATH")
    |> Option.ofObj
    |> Option.iter(fun path ->
        Log.debug $"Setting $LD_LIBRARY_PATH to $MEL_LD_LIBRARY_PATH: {path}"
        startInfo.EnvironmentVariables["LD_LIBRARY_PATH"] <- path
    )
    startInfo