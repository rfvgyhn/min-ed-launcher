module MinEdLauncher.Steam

open System
open System.Globalization
open System.Runtime.InteropServices

let potentialInstallPaths() =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        let winProgFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        [ $"%s{winProgFiles}\\Steam\\steamapps\\common\\Elite Dangerous" ]
    elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
        let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        [ $"%s{home}/.steam/steam/steamapps/common/Elite Dangerous"
          $"%s{home}/.local/share/Steam/steamapps/common/Elite Dangerous" ]
    else []
    
// Steam sets LC_ALL=C to help some games, but we need the real value
// in order to pass the correct language value to the elite dangerous
// process. Steam sets HOST_LC_ALL if LC_ALL is set to allow us to use
// the real value.
let fixLcAll() =
    let hostLcAll = Environment.GetEnvironmentVariable("HOST_LC_ALL")
    if not (String.IsNullOrEmpty(hostLcAll)) then
        Environment.SetEnvironmentVariable("LC_ALL", hostLcAll)
        Log.debug $"Overwrote LC_ALL with HOST_LC_ALL '{hostLcAll}'"
        Threading.Thread.CurrentThread.CurrentUICulture <- CultureInfo.GetCultureInfo(hostLcAll)
    else
        Environment.SetEnvironmentVariable("LC_ALL", null)
        Log.debug "Unset LC_ALL. Using $LANG to determine correct UI culture"
        let lang = Environment.GetEnvironmentVariable("LANG").Split('.')[0]
        Threading.Thread.CurrentThread.CurrentUICulture <- CultureInfo.GetCultureInfo(lang)

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

type Steam() =
    let mutable disposed = false
    let mutable initialized = false
    let mutable userHandle = IntPtr.Zero
    let mutable sessionToken = 0u
    
    let cleanup disposing =
        if not disposed then
            Log.debug "Disposing Steam resources"
            disposed <- true
            
            if userHandle <> IntPtr.Zero then
                Log.debug "Cancelling auth ticket"
                SteamAPI_ISteamUser_CancelAuthTicket(userHandle, sessionToken)
                
            if initialized then
                Log.debug "closing steam"
                SteamAPI_Shutdown()
                
    let getCurrentUserHandle() =
        let client = SteamClient()
        if client <> IntPtr.Zero then
            Log.debug "Got steam client"
            let pipe = SteamAPI_GetHSteamPipe()
            if pipe <> 0 then
                Log.debug "Got steam pipe"
                let globalUser = SteamAPI_ISteamClient_ConnectToGlobalUser(client, pipe)
                if globalUser <> 0 then
                    Log.debug "Got steam global user"
                    userHandle <- SteamAPI_ISteamClient_GetISteamUser(client, globalUser, pipe, "SteamUser019")
                    if userHandle <> IntPtr.Zero then
                        Log.debug "Got steam user"
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
        if initialized then Ok ()
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
                    Log.debug "Requesting steam auth ticket"
                    sessionToken <- SteamAPI_ISteamUser_GetAuthSessionTicket(handle, rawToken, 1024u, &count)

                    if sessionToken <> 0u then
                        Log.debug "Got steam auth ticket"
                        Ok { UserId = userId; SessionToken = bytesToHex rawToken ((int)count) }
                    else errorResult
                else errorResult
    
    interface IDisposable with
        member this.Dispose() =
            cleanup true
            GC.SuppressFinalize(this)
            
    override this.Finalize() =
        cleanup false

        
