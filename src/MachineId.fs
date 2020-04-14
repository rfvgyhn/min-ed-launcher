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
        let bytes = Encoding.ASCII.GetBytes(machineId.Trim() + frontierId.Trim())
        use crypto = new SHA1CryptoServiceProvider()
        let hash = crypto.ComputeHash(bytes)
        BitConverter.ToString(hash)
                    .Replace("-","")
                    .Substring(0, 16)
                    .ToLowerInvariant()
    
    let getWindowsId() =
        match WindowsRegistry.getIds() with
        | Ok (machineId, frontierId) -> Ok <| getId machineId frontierId
        | Error msg -> Error msg
        
    let getFilesystemId() = async {
        match! Filesystem.getIds() with
        | Ok (machineId, frontierId) -> return Ok <| getId machineId frontierId
        | Error msg -> return Error msg
    }