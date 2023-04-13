module MinEdLauncher.Process

open System.Diagnostics

let launchProcesses (processes:ProcessStartInfo list) =
    processes
    |> List.choose (fun p ->
        try
            Process.Start(p) |> Some
        with
        | e ->
            Log.exn e $"Unable to start process %s{p.FileName}"
            None)

let stopProcesses (processes: Process list) =
    processes
    |> List.iter (fun p ->
        use p = p
        if p.HasExited then
            Log.debug $"Process %i{p.Id} already exited"            
        else
            Log.debug $"Stopping process %s{p.ProcessName}"
            match Interop.termProcess p with
            | Ok () ->
                p.StandardOutput.ReadToEnd() |> ignore
                p.StandardError.ReadToEnd() |> ignore
                Log.info $"Stopped process %s{p.ProcessName}"
            | Error msg -> Log.warn msg)
    
let writeOutput (processes: Process list) =
    processes
    |> List.iter(fun p ->
        use p = p
        p.EnableRaisingEvents <- true
        p.OutputDataReceived.Add(fun a -> if a.Data <> null then printfn $"  %s{a.Data}")
        p.ErrorDataReceived.Add(fun a -> if a.Data <> null then printfn $"  %s{a.Data}")
        p.BeginErrorReadLine()
        p.BeginOutputReadLine()
        p.WaitForExit()
    )