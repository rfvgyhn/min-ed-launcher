open System
open System.IO
open System.Runtime.InteropServices
open Steam
open Types
open Settings

let log =
    let write level msg = printfn "[%s] - %s" level msg
    { Debug = fun msg -> write "DBG" msg
      Info = fun msg -> write "INF" msg
      Warn = fun msg -> write "WRN" msg
      Error = fun msg -> write "ERR" msg }
    
let getOsIdent() =
    let platToStr plat =
        if   plat = OSPlatform.Linux   then "Linux"
        elif plat = OSPlatform.Windows then "Win"
        elif plat = OSPlatform.OSX     then "Mac"
        elif plat = OSPlatform.FreeBSD then "FreeBSD"
        else "Unknown"        
    let platform =
        [ OSPlatform.Linux; OSPlatform.Windows; OSPlatform.OSX; OSPlatform.FreeBSD ]
        |> List.pick (fun p -> if RuntimeInformation.IsOSPlatform(p) then Some p else None)
        |> platToStr
    let arch =
        match RuntimeInformation.ProcessArchitecture with
        | Architecture.Arm -> "Arm"
        | Architecture.Arm64 -> "Arm64"
        | Architecture.X64 -> "64"
        | Architecture.X86 -> "32"
        | unknownArch -> unknownArch.ToString()
    
    platform + arch

let getUserDetails = function
    | Dev -> Ok { UserId = 12345UL; SessionToken = "DevToken" }
    | Oculus _ -> Error "Oculus not supported"
    | Frontier -> Error "Frontier not supported"
    | Steam _ ->
        use steam = new Steam(log)
        steam.Login()

let printInfo platform user =
    printfn "Elite: Dangerous Launcher"
    printfn "Platform: %A" platform
    printfn "OS: %s" (getOsIdent())
    match user with
    | Error msg -> printfn "User: Error - %s" msg
    | Ok user ->
        printfn "User: %u" user.UserId
        printfn "Session Token: %s" user.SessionToken

[<EntryPoint>]
let main argv =
    let settings = parseArgs log Settings.defaults [| |] //argv
    let user = getUserDetails settings.Platform
    
    printInfo settings.Platform user
    
    0
