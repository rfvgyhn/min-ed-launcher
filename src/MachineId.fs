namespace EdLauncher

module MachineId =
    open System
    open System.Security.Cryptography
    open System.Text

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
                let machineId = Registry.CurrentUser.OpenSubKey(machinePath).GetValue(key).ToString()
                let frontierId = Registry.CurrentUser.OpenSubKey(frontierPath).GetValue(key).ToString()
                
                Ok (machineId, frontierId)
            | Error msg -> Error msg
        
    module WineRegistry =
        open System.IO
        
        let private machineFilePath registryPath = Path.Combine(registryPath, "system.reg")
        let private frontierFilePath registryPath = Path.Combine(registryPath, "user.reg")
        let private machineEntry = @"[Software\\Microsoft\\Cryptography]"
        let private frontierEntry = @"[Software\\Frontier Developments\\Cryptography]"
        
        let ensureIdsExist registryPath = async {
            try
                let system = FileInfo(machineFilePath registryPath)
                let user = FileInfo(frontierFilePath registryPath)
                
                if not (system.Exists || user.Exists) then
                    return Error <| sprintf "Unable to find registry file(s) at '%s' and/or '%s'" user.FullName system.FullName
                else
                
                let keyExists =
                    File.ReadLines(user.FullName)
                    |> Seq.skipWhile (fun l -> not (l.StartsWith(frontierEntry)))
                    |> Seq.take 1
                    |> Seq.length > 0
                    
                if not keyExists then
                    let entry = String.Join(Environment.NewLine, [
                        ""
                        sprintf"%s 1585703162" frontierEntry
                        sprintf "#time=%s" ""
                        sprintf "\"MachineGuid\"=\"%s\"" (Guid.NewGuid().ToString())
                    ])
                    use sw = user.AppendText()
                    do! sw.WriteLineAsync(entry) |> Async.AwaitTask
                
                return Ok ()
            with
            | e -> return Error e.Message
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
            
        let getIds registryPath = async {
            match! ensureIdsExist registryPath with
            | Ok _ ->
                let machineId = readEntry (machineFilePath registryPath) machineEntry
                let frontierId = readEntry (frontierFilePath registryPath) frontierEntry
                
                return Ok (machineId, frontierId)
            | Error msg -> return Error msg
        }
        
    module Filesystem =
        open System.IO
        open System.Linq
        
        let private configPath = "Settings"
        let private machinePath = Path.Combine(configPath, "machineid.txt")
        let private frontierPath = Path.Combine(configPath, "frontierid.txt")

        let ensureIdsExist() = async {
            try
                let files = [ FileInfo(machinePath); FileInfo(frontierPath) ]
                for file in files do
                    if not file.Exists || file.Length < 1L then
                        Directory.CreateDirectory file.DirectoryName |> ignore
                        use sw = file.AppendText()
                        do! sw.WriteLineAsync(Guid.NewGuid().ToString()) |> Async.AwaitTask
                        
                let! machineId = FileIO.readAllText machinePath
                let! frontierId = FileIO.readAllText frontierPath
                
                match machineId, frontierId with
                | Ok _, Ok _ -> return Ok ()
                | _, Error msg -> return Error msg
                | Error msg, _ -> return Error msg
            with
            | e -> return Error e.Message
        }
            
        let getIds() = async {
            match! ensureIdsExist() with
            | Ok _ ->
                let! machineId = File.ReadAllLinesAsync(machinePath) |> Async.AwaitTask
                let! frontierId = File.ReadAllLinesAsync(frontierPath) |> Async.AwaitTask
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
    
    let getWineId registryPath = async {
        match! WineRegistry.getIds registryPath with
        | Ok (machineId, frontierId) -> return Ok <| getId machineId frontierId
        | Error msg -> return Error msg
    }
    let getFilesystemId() = async {
        match! Filesystem.getIds() with
        | Ok (machineId, frontierId) -> return Ok <| getId machineId frontierId
        | Error msg -> return Error msg
    }