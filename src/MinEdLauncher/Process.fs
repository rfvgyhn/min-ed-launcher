module MinEdLauncher.Process

open System.ComponentModel
open System.Diagnostics

let launchProcesses printOutput (processes:ProcessStartInfo list) =
    processes
    |> List.choose (fun p ->
        try
            let p = Process.Start(p)
            p.BeginErrorReadLine()
            p.BeginOutputReadLine()
            
            if printOutput then
                p.OutputDataReceived.Add(fun a -> if a.Data <> null then printfn $"  %s{a.Data}")
                p.ErrorDataReceived.Add(fun a -> if a.Data <> null then printfn $"  %s{a.Data}")
                
            p |> Some
        with
        | :? Win32Exception as e ->
            Log.exn e $"""Unable to start process %s{p.FileName}
    HRESULT: 0x{e.ErrorCode:X}
    Win32 Error Code: {e.NativeErrorCode}
    """
            None
        | e ->
            Log.exn e $"Unable to start process %s{p.FileName}"
            None)

let stopProcesses timeout (processes: Process list) =
    processes
    |> List.iter (fun p ->
        use p = p
        if p.HasExited then
            Log.debug $"Process %i{p.Id} already exited"            
        else
            Log.debug $"Stopping process %s{p.ProcessName}"
            match Interop.termProcess timeout p with
            | Ok () ->
                Log.info $"Stopped process %s{p.ProcessName}"
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