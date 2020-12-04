module MinEdLauncher.MachineId
open System
open System.IO
open FSharp.Control.Tasks.NonAffine

module WindowsRegistry =
    open Microsoft.Win32
    
    let private machinePath = @"SOFTWARE\\Microsoft\\Cryptography"
    let private frontierPath = @"SOFTWARE\\Frontier Developments\\Cryptography"
    let private key = "MachineGuid"
    
    let ensureIdsExist() =
        let regKey = Registry.CurrentUser.OpenSubKey(frontierPath)
        if regKey <> null && regKey.GetValue(key) <> null then
            Ok ()
        else
            try
                let regKey = Registry.CurrentUser.CreateSubKey(frontierPath, RegistryKeyPermissionCheck.ReadWriteSubTree)
                regKey.SetValue(key, Guid.NewGuid().ToString())
                Ok ()
            with
            | e -> Error e.Message
    
    let getIds() =
        match ensureIdsExist() with
        | Ok _ ->
            let machineId = Registry.LocalMachine.OpenSubKey(machinePath).GetValue(key).ToString()
            let frontierId = Registry.CurrentUser.OpenSubKey(frontierPath).GetValue(key).ToString()
            
            Ok (machineId, frontierId)
        | Error msg -> Error msg
    
module WineRegistry =
    let private machineFilePath registryPath = Path.Combine(registryPath, "system.reg")
    let private frontierFilePath registryPath = Path.Combine(registryPath, "user.reg")
    let private machineEntry = @"[Software\\Microsoft\\Cryptography]"
    let private frontierEntry = @"[Software\\Frontier Developments\\Cryptography]"
    
    let ensureIdsExist registryPath = task {
        try
            let system = FileInfo(machineFilePath registryPath)
            let user = FileInfo(frontierFilePath registryPath)
            
            if not (system.Exists || user.Exists) then
                return Error $"Unable to find registry file(s) at '%s{user.FullName}' and/or '%s{system.FullName}'"
            else
            
            let keyExists =
                File.ReadLines(user.FullName)
                |> Seq.skipWhile (fun l -> not (l.StartsWith(frontierEntry)))
                |> Seq.length > 0
                
            if not keyExists then
                let entry = String.Join(Environment.NewLine, [
                    ""
                    $"%s{frontierEntry} 1585703162"
                    "#time=1d52cf302f944a0" // TODO: figure out how to generate this timestamp
                    $"\"MachineGuid\"=\"%s{Guid.NewGuid().ToString()}\""
                ])
                use sw = user.AppendText()
                do! sw.WriteLineAsync(entry)
            
            return Ok ()
        with
        | e -> return Error (e.ToString())
    }
    
    let private readEntry file (path:string) =
        let split (token:string) (str:string) = str.Split(token, StringSplitOptions.RemoveEmptyEntries)
        
        // [Software\\Microsoft\\Cryptography] 1561645031
        // #time=1d52cf302f944a0
        // "MachineGuid"="Guid"
        File.ReadLines(file)
        |> Seq.skipWhile (fun l -> not (l.StartsWith(path)))
        |> Seq.skip 2
        |> Seq.take 1
        |> Seq.head
        |> split "\""
        |> Array.last
        
    let getIds registryPath = task {
        match! ensureIdsExist registryPath with
        | Ok _ ->
            let machineId = readEntry (machineFilePath registryPath) machineEntry
            let frontierId = readEntry (frontierFilePath registryPath) frontierEntry
            
            return Ok (machineId, frontierId)
        | Error msg -> return Error msg
    }
    
module Filesystem =
    open System.Linq
    
    let private configPath = "Settings"
    let private machinePath = Path.Combine(configPath, "machineid.txt")
    let private frontierPath = Path.Combine(configPath, "frontierid.txt")

    let ensureIdsExist() = task {
        try
            let files = [ FileInfo(machinePath); FileInfo(frontierPath) ]
            for file in files do
                if not file.Exists || file.Length < 1L then
                    Directory.CreateDirectory file.DirectoryName |> ignore
                    use sw = file.AppendText()
                    do! sw.WriteLineAsync(Guid.NewGuid().ToString())
                    
            let! machineId = FileIO.readAllText machinePath
            let! frontierId = FileIO.readAllText frontierPath
            
            match machineId, frontierId with
            | Ok _, Ok _ -> return Ok ()
            | _, Error msg -> return Error msg
            | Error msg, _ -> return Error msg
        with
        | e -> return Error e.Message
    }
        
    let getIds() = task {
        match! ensureIdsExist() with
        | Ok _ ->
            let! machineId = File.ReadAllLinesAsync(machinePath)
            let! frontierId = File.ReadAllLinesAsync(frontierPath)
            return Ok (machineId.FirstOrDefault(), frontierId.FirstOrDefault())
        | Error msg -> return Error msg
    }
    
let getId (machineId:string) (frontierId:string) =
    machineId.Trim() + frontierId.Trim()
    |> SHA1.hashString
    |> Hex.toStringTrunc 16
    |> fun s -> s.ToLowerInvariant()

let getWindowsId() =
    match WindowsRegistry.getIds() with
    | Ok (machineId, frontierId) -> Ok <| getId machineId frontierId
    | Error msg -> Error msg

let getWineId() = task {
    let registryPath =
        let steamCompat = Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH")
        let winePrefix = Environment.GetEnvironmentVariable("WINEPREFIX")
        if not (String.IsNullOrEmpty(steamCompat)) then
            Path.Combine(steamCompat, "pfx")
        else
            winePrefix
    if String.IsNullOrEmpty(registryPath) then
        return Error "Unable to find wine directory. Make sure either STEAM_COMPAT_DATA_PATH or WINEPREFIX is set"
    else
        match! WineRegistry.getIds registryPath with
        | Ok (machineId, frontierId) -> return Ok <| getId machineId frontierId
        | Error msg -> return Error msg
}
let getFilesystemId() = task {
    match! Filesystem.getIds() with
    | Ok (machineId, frontierId) -> return Ok <| getId machineId frontierId
    | Error msg -> return Error msg
}