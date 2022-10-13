module MinEdLauncher.Process

open System.Diagnostics

let launchProcesses (processes:ProcessStartInfo list) =
    processes
    |> List.choose (fun p ->
        try
            Process.Start(p) |> Some
        with
        | e ->
            Log.exn e $"Unable to start pre-launch process %s{p.FileName}"
            None)

let stopProcesses (processes: Process list) =
    processes
    |> List.iter (fun p ->
        Log.debug $"Stopping process %s{p.ProcessName}"
        match Interop.termProcess p with
        | Ok () ->
            p.StandardOutput.ReadToEnd() |> ignore
            p.StandardError.ReadToEnd() |> ignore
            Log.info $"Stopped process %s{p.ProcessName}"
        | Error msg -> Log.warn msg)