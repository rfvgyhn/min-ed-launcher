namespace EdLauncher

module Steam =
    open System
    open System.IO
    open System.Runtime.InteropServices
    open Types

    [<Literal>]
    let SteamLib =
    #if WINDOWS
        "steam_api64.dll"
    #else
        "libsteam_api.so"
    #endif

    [<DllImport(SteamLib)>]
    extern bool SteamAPI_Init()

    [<DllImport(SteamLib)>]
    extern void SteamAPI_Shutdown()

    [<DllImport(SteamLib)>]
    extern IntPtr SteamClient();

    [<DllImport(SteamLib)>]
    extern int SteamAPI_GetHSteamPipe()

    [<DllImport(SteamLib)>]
    extern int SteamAPI_ISteamClient_ConnectToGlobalUser(IntPtr instance, int pipe)

    [<DllImport(SteamLib)>]
    extern IntPtr SteamAPI_ISteamClient_GetISteamUser(IntPtr instance, int user, int pipe, string version)

    [<DllImport(SteamLib)>]
    extern uint64 SteamAPI_ISteamUser_GetSteamID(IntPtr instance)

    [<DllImport(SteamLib)>]
    extern uint32 SteamAPI_ISteamUser_GetAuthSessionTicket(IntPtr instance, byte[] buffer, uint32 size, uint32& count)

    [<DllImport(SteamLib)>]
    extern void SteamAPI_ISteamUser_CancelAuthTicket(IntPtr instance, uint32 hTicket)

    type SteamUser =
        { UserId: UInt64
          SessionToken: string }

    type Steam(log : ILog) =
        let mutable disposed = false
        let mutable initialized = false
        let mutable userHandle = IntPtr.Zero
        let mutable sessionToken = 0u
        
        let cleanup disposing =
            if not disposed then
                log.Debug "Disposing Steam resources"
                disposed <- true
                
                if userHandle <> IntPtr.Zero then
                    log.Debug "Cancelling auth ticket"
                    SteamAPI_ISteamUser_CancelAuthTicket(userHandle, sessionToken)
                    
                if initialized then
                    log.Debug "closing steam"
                    SteamAPI_Shutdown()
                    
        let getCurrentUserHandle() =
            let client = SteamClient()
            if client <> IntPtr.Zero then
                log.Debug "Got steam client"
                let pipe = SteamAPI_GetHSteamPipe()
                if pipe <> 0 then
                    log.Debug "Got steam pipe"
                    let globalUser = SteamAPI_ISteamClient_ConnectToGlobalUser(client, pipe)
                    if globalUser <> 0 then
                        log.Debug "Got steam global user"
                        userHandle <- SteamAPI_ISteamClient_GetISteamUser(client, globalUser, pipe, "SteamUser019")
                        if userHandle <> IntPtr.Zero then
                            log.Debug "Got steam user"
                            Some userHandle
                        else None
                    else None
                else None
            else None
                    
        let bytesToHex (bytes: byte[]) count =
            bytes
            |> Array.take count
            |> Hex.toString
            
        let init() =
            if not <| File.Exists(SteamLib) then
                Error <| sprintf "Unable to find steam library '%s'" SteamLib
            elif SteamAPI_Init() then
                initialized <- true
                Ok ()
            else
                Error "Unable to initialize Steam"

        member this.Login() =
            match init() with
            | Error m -> Error m
            | Ok _ ->
                let errorResult = Error "Unable to get current steam user. Make sure your Steam client is running."
                match getCurrentUserHandle() with
                | None -> errorResult
                | Some handle ->
                    let userId = SteamAPI_ISteamUser_GetSteamID handle
                    
                    if userId <> 0UL then
                        let rawToken = Array.zeroCreate<byte> 1024
                        let mutable count = 0u
                        log.Debug("Requesting steam auth ticket")
                        sessionToken <- SteamAPI_ISteamUser_GetAuthSessionTicket(handle, rawToken, 1024u, &count)

                        if sessionToken <> 0u then
                            log.Debug("Got steam auth ticket")
                            Ok { UserId = userId; SessionToken = bytesToHex rawToken ((int)count) }
                        else errorResult
                    else errorResult
        
        interface IDisposable with
            member this.Dispose() =
                cleanup true
                GC.SuppressFinalize(this)
                
        override this.Finalize() =
            cleanup false

        
