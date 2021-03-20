module MinEdLauncher.Tests.Settings

open System.IO
open Expecto
open MinEdLauncher
open MinEdLauncher.Settings
open MinEdLauncher.Types

[<Tests>]
let tests =
    let parseWithFallback fallback args =
        match parseArgs Settings.defaults fallback args with
        | Ok settings -> settings
        | Error _ -> Settings.defaults        
    let parse args =
        parseWithFallback (fun _ -> Ok ".") args
      
    testList "Parsing command line arguments" [
        test "Matches /steamid" {
            let settings = parse [| "/steamid" |]
            Expect.equal settings.Platform Steam ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches /steam" {
            let settings = parse [| "/steam" |]
            Expect.equal settings.Platform Steam ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches /epic" {
            let settings = parse [| "/epic" |]
            Expect.equal settings.Platform (Epic EpicDetails.Empty) ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Last platform wins" {
            let settingsEpic = parse [| "/steam"; "/epic" |]
            let settingsSteam = parse [| "/epic"; "/steam" |]
            
            Expect.equal settingsEpic.Platform (Epic EpicDetails.Empty) ""
            Expect.equal settingsSteam.Platform Steam ""
        }
        test "Matches epic password" {
            let settings = parse [| "-AUTH_PASSWORD=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with ExchangeCode = "asdf" }) ""
        }
        test "Matches epic type" {
            let settings = parse [| "-AUTH_TYPE=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with Type = "asdf" }) ""
        }
        test "Matches epic app id" {
            let settings = parse [| "-epicapp=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with AppId = "asdf" }) ""
        }
        test "Matches /oculus nonce" {
            let settings = parse [| "/oculus"; "123" |]
            Expect.equal settings.Platform (Oculus "123") ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Ignores /oculus without nonce as next arg" {
            let settings = parse [| "/oculus"; "/123" |]
            Expect.equal settings.Platform Settings.defaults.Platform ""
        }
        test "Matches /vr" {
            let settings = parse [| "/vr" |]
            Expect.equal settings.DisplayMode Vr ""
            Expect.equal settings.AutoRun true "VR mode should autorun the game"
        }
        test "Matches /autorun" {
            let settings = parse [| "/autorun" |]
            Expect.equal settings.AutoRun true ""
        }
        test "Matches /autoquit" {
            let settings = parse [| "/autoquit" |]
            Expect.equal settings.AutoQuit true ""
        }
        test "Matches /forcelocal" {
            let settings = parse [| "/forcelocal" |]
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches /ed" {
            let settings = parse [| "/ed" |]
            Expect.equal (settings.ProductWhitelist.Contains "ed") true ""
        }
        test "Matches /edh" {
            let settings = parse [| "/edh" |]
            Expect.equal (settings.ProductWhitelist.Contains "edh") true ""
        }
        test "Matches /eda" {
            let settings = parse [| "/eda" |]
            Expect.equal (settings.ProductWhitelist.Contains "eda") true ""
        }
        
        yield! [
            "non steam linux runtime",               [ Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "non steam linux runtime custom folder", [ Path.Combine("Steam", "compatibilitytools.d", "Proton 5.0", "proton"); "protonAction" ]
        ] |> List.map (fun (name, protonArgs) ->
            test $"Matches proton args {name}" {
                let launcherDir = "launchDir"
                let launcherPath = Path.Combine(launcherDir, "EDLaunch.exe")
                let args = protonArgs @ [launcherPath; "/other"; "/args"] |> List.toArray
                let settings = parse args
                
                let expected = { EntryPoint = "python3"; Args = args.[..^3] }
                Expect.equal settings.Proton (Some expected) ""
                Expect.equal settings.CbLauncherDir launcherDir ""
            }
        )
        
        yield! [
            "steam linux runtime - extra args", [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "steam linux runtime",              [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
        ] |> List.map (fun (name, protonArgs) ->
            test $"Matches proton args {name}" {
                let launcherDir = "launchDir"
                let launcherPath = Path.Combine(launcherDir, "EDLaunch.exe")
                let args = protonArgs @ [launcherPath; "/other"; "/args"] |> List.toArray
                
                let settings = parse args
                
                let expectedArgs = protonArgs.[1..^2] @ [ "python3" ] @ protonArgs.[^1..] |> List.toArray
                let expected = { EntryPoint = args.[0]; Args = expectedArgs }
                Expect.equal settings.Proton (Some expected) ""
                Expect.equal settings.CbLauncherDir launcherDir ""
            }
        )
        test "Fewer than three args means no Proton" {
            let settings = parse [| "asdf"; "fdsa" |]
            Expect.equal settings.Proton None ""
        }
        test "First arg doesn't contain steamapps/common/Proton or SteamRuntimeLinux means no Proton" {
            let settings = parse [| "asdf"; "fdsa"; "launchDir" |]
            Expect.equal settings.Proton None ""
        }
        test "Uses first arg as launch dir if it points to EDLaunch.exe" {
            let expectedDir = Path.Combine("test", "dir")
            let settings = parse [| Path.Combine(expectedDir, "EDLaunch.exe") |]
            Expect.equal settings.CbLauncherDir expectedDir ""
        }
        test "Non Proton uses fallback dir for cobra bay launcher dir" {
            let expectedDir = Path.Combine("test", "dir")
            let settings = parseWithFallback (fun _ -> Ok expectedDir) [||]
            Expect.equal settings.CbLauncherDir expectedDir ""
        }
        testProperty "Unknown arg doesn't change any values" <|
            fun (args:string[]) -> parse args = Settings.defaults
    ]
