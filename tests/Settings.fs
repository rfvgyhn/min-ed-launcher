module MinEdLauncher.Tests.Settings

open System.IO
open Expecto
open FSharp.Control.Tasks.NonAffine
open MinEdLauncher
open MinEdLauncher.Settings
open MinEdLauncher.Types

[<Tests>]
let tests =
    let parseWithFallback fallback args = task {
        match! parseArgs Settings.defaults fallback args with
        | Ok settings -> return settings
        | Error _ -> return Settings.defaults }
    let parse args =
        parseWithFallback (fun _ -> Ok ".") args
      
    testList "Parsing command line arguments" [
        testTask "Matches /steamid" {
            let! settings = parse [| "/steamid" |]
            Expect.equal settings.Platform Steam ""
            Expect.equal settings.ForceLocal true ""
        }
        testTask "Matches /steam" {
            let! settings = parse [| "/steam" |]
            Expect.equal settings.Platform Steam ""
            Expect.equal settings.ForceLocal true ""
        }
        testTask "Matches /epic" {
            let! settings = parse [| "/epic" |]
            Expect.equal settings.Platform (Epic EpicDetails.Empty) ""
            Expect.equal settings.ForceLocal true ""
        }
        testTask "Matches /frontier when profile is specified" {
            let profileName = "test"
            let! settings = parse [| "/frontier"; profileName |]
            Expect.equal settings.Platform (Frontier { Profile = profileName; Credentials = None; AuthToken = None}) ""
        }
        testTask "Ignores /frontier when profile is not specified" {
            let! settings = parse [| "/frontier" |]
            Expect.equal settings.Platform Settings.defaults.Platform ""
        }
        testTask "Last platform wins" {
            let! settingsEpic = parse [| "/steam"; "/epic" |]
            let! settingsSteam = parse [| "/epic"; "/steam" |]
            
            Expect.equal settingsEpic.Platform (Epic EpicDetails.Empty) ""
            Expect.equal settingsSteam.Platform Steam ""
        }
        testTask "Matches epic password" {
            let! settings = parse [| "-AUTH_PASSWORD=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with ExchangeCode = "asdf" }) ""
        }
        testTask "Matches epic type" {
            let! settings = parse [| "-AUTH_TYPE=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with Type = "asdf" }) ""
        }
        testTask "Matches epic app id" {
            let! settings = parse [| "-epicapp=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with AppId = "asdf" }) ""
        }
        testTask "Matches /oculus nonce" {
            let! settings = parse [| "/oculus"; "123" |]
            Expect.equal settings.Platform (Oculus "123") ""
            Expect.equal settings.ForceLocal true ""
        }
        testTask "Ignores /oculus without nonce as next arg" {
            let! settings = parse [| "/oculus"; "/123" |]
            Expect.equal settings.Platform Settings.defaults.Platform ""
        }
        testTask "Matches /vr" {
            let! settings = parse [| "/vr" |]
            Expect.equal settings.DisplayMode Vr ""
            Expect.equal settings.AutoRun true "VR mode should autorun the game"
        }
        testTask "Matches /novr" {
            let! settings = parse [| "/novr" |]
            Expect.equal settings.DisplayMode Pancake ""
        }
        testTask "Matches /autorun" {
            let! settings = parse [| "/autorun" |]
            Expect.equal settings.AutoRun true ""
        }
        testTask "Matches /autoquit" {
            let! settings = parse [| "/autoquit" |]
            Expect.equal settings.AutoQuit true ""
        }
        testTask "Matches /forcelocal" {
            let! settings = parse [| "/forcelocal" |]
            Expect.equal settings.ForceLocal true ""
        }
        testTask "Matches /restart delay" {
            let! settings = parse [| "/restart"; "2" |]
            
            Expect.equal settings.Restart (Some 2000L) ""
        }
        testTask "Ignores /restart with missing delay" {
            let! settings = parse [| "/restart" |]
            Expect.equal settings.Restart None ""
        }
        testTask "Ignores /restart when delay isn't a number" {
            let! settings = parse [| "/restart"; "a" |]
            Expect.equal settings.Restart None ""
        }
        testTask "Ignores /restart when delay is negative" {
            let! settings = parse [| "/restart"; "-1" |]
            Expect.equal settings.Restart None ""
        }
        testTask "Matches /ed" {
            let! settings = parse [| "/ed" |]
            Expect.equal (settings.ProductWhitelist.Contains "ed") true ""
        }
        testTask "Matches /edh" {
            let! settings = parse [| "/edh" |]
            Expect.equal (settings.ProductWhitelist.Contains "edh") true ""
        }
        testTask "Matches /eda" {
            let! settings = parse [| "/eda" |]
            Expect.equal (settings.ProductWhitelist.Contains "eda") true ""
        }
        testTask "Matches /edo" {
            let! settings = parse [| "/edo" |]
            Expect.equal (settings.ProductWhitelist.Contains "edo") true ""
        }
        
        yield! [
            "non steam linux runtime",               [ Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "non steam linux runtime custom folder", [ Path.Combine("Steam", "compatibilitytools.d", "Proton 5.0", "proton"); "protonAction" ]
        ] |> List.map (fun (name, protonArgs) ->
            testTask $"Matches proton args {name}" {
                let launcherDir = "launchDir"
                let launcherPath = Path.Combine(launcherDir, "EDLaunch.exe")
                let args = protonArgs @ [launcherPath; "/other"; "/args"] |> List.toArray
                let! settings = parse args
                
                let expected = { EntryPoint = "python3"; Args = args.[..^3] }
                Expect.equal settings.Proton (Some expected) ""
                Expect.equal settings.CbLauncherDir launcherDir ""
            }
        )
        
        yield! [
            "steam linux runtime - extra args", [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "steam linux runtime",              [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
        ] |> List.map (fun (name, protonArgs) ->
            testTask $"Matches proton args {name}" {
                let launcherDir = "launchDir"
                let launcherPath = Path.Combine(launcherDir, "EDLaunch.exe")
                let args = protonArgs @ [launcherPath; "/other"; "/args"] |> List.toArray
                
                let! settings = parse args
                
                let expectedArgs = protonArgs.[1..^2] @ [ "python3" ] @ protonArgs.[^1..] |> List.toArray
                let expected = { EntryPoint = args.[0]; Args = expectedArgs }
                Expect.equal settings.Proton (Some expected) ""
                Expect.equal settings.CbLauncherDir launcherDir ""
            }
        )
        testTask "Fewer than three args means no Proton" {
            let! settings = parse [| "asdf"; "fdsa" |]
            Expect.equal settings.Proton None ""
        }
        testTask "First arg doesn't contain steamapps/common/Proton or SteamRuntimeLinux means no Proton" {
            let! settings = parse [| "asdf"; "fdsa"; "launchDir" |]
            Expect.equal settings.Proton None ""
        }
        testTask "Uses first arg as launch dir if it points to EDLaunch.exe" {
            let expectedDir = Path.Combine("test", "dir")
            let! settings = parse [| Path.Combine(expectedDir, "EDLaunch.exe") |]
            Expect.equal settings.CbLauncherDir expectedDir ""
        }
        testTask "Non Proton uses fallback dir for cobra bay launcher dir" {
            let expectedDir = Path.Combine("test", "dir")
            let! settings = parseWithFallback (fun _ -> Ok expectedDir) [||]
            Expect.equal settings.CbLauncherDir expectedDir ""
        }
        testTask "Known flag shouldn't be treated as whitelist filter" {
            let knownFlags = [| "/steamid"; "/steam"; "/epic"; "/frontier"; "profile"; "/oculus"; "nonce"; "/restart"; "1"; "/vr"; "/novr"; "/autorun"; "/autoquit"; "/forcelocal" |]
            
            let! settings = parse knownFlags
            
            Expect.isEmpty settings.ProductWhitelist ""
        }

        testProperty "Unknown arg doesn't change any values" <|
            fun (args:string[]) ->
                // /* args are considered whitelist args and not unknown
                let args = args |> Array.filter (fun arg -> arg = null || (arg.StartsWith('/') && arg.Length < 2))
                let settings = parse args
                settings.Wait()
                settings.Result = Settings.defaults
    ]
