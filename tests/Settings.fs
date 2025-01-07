module MinEdLauncher.Tests.Settings

open System
open System.IO
open Expecto
open MinEdLauncher
open MinEdLauncher.Settings
open MinEdLauncher.Types
open MinEdLauncher.Tests.Extensions

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
        test "Matches /frontier when profile is specified" {
            let profileName = "test"
            let settings = parse [| "/frontier"; profileName |]
            Expect.equal settings.Platform (Frontier { Profile = profileName; Credentials = None; AuthToken = None}) ""
        }
        test "Ignores /frontier when profile is not specified" {
            let settings = parse [| "/frontier" |]
            Expect.equal settings.Platform Settings.defaults.Platform ""
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
            Expect.equal settings.AutoRun false "VR mode should only autorun the game if /autorun is specified"
        }
        test "Matches /novr" {
            let settings = parse [| "/novr" |]
            Expect.equal settings.DisplayMode Pancake ""
        }
        test "Matches /skipInstallPrompt" {
            let settings = parse [| "/skipInstallPrompt" |]
            Expect.equal settings.SkipInstallPrompt true ""
        }
        test "Matches /dryrun" {
            let settings = parse [| "/dryrun" |]
            Expect.isTrue settings.DryRun ""
        }
        test "Matches /autorun" {
            let settings = parse [| "/autorun" |]
            Expect.equal settings.AutoRun true ""
        }
        test "Matches /autoquit" {
            let settings = parse [| "/autoquit" |]
            Expect.equal settings.QuitMode Immediate ""
        }
        test "Matches /autoquit waitForExit" {
            let settings = parse [| "/autoquit"; "waitForExit" |]
            Expect.equal settings.QuitMode WaitForExit ""
        }
        test "Default quit mode should be WaitForInput" {
            let settings = parse [| |]
            Expect.equal settings.QuitMode WaitForInput ""
        }
        test "Matches /forcelocal" {
            let settings = parse [| "/forcelocal" |]
            Expect.equal settings.ForceLocal true ""
        }
        test "Matches /restart delay" {
            let settings = parse [| "/restart"; "2" |]
            
            Expect.equal settings.Restart (Some 2000L) ""
        }
        test "Ignores /restart with missing delay" {
            let settings = parse [| "/restart" |]
            Expect.equal settings.Restart None ""
        }
        test "Ignores /restart when delay isn't a number" {
            let settings = parse [| "/restart"; "a" |]
            Expect.equal settings.Restart None ""
        }
        test "Ignores /restart when delay is negative" {
            let settings = parse [| "/restart"; "-1" |]
            Expect.equal settings.Restart None ""
        }
        test "Matches /ed" {
            let settings = parse [| "/ed" |]
            Expect.contains settings.ProductWhitelist "ed" ""
        }
        test "Matches /edh" {
            let settings = parse [| "/edh" |]
            Expect.contains settings.ProductWhitelist "edh" ""
        }
        test "Matches /edh4" {
            let settings = parse [| "/edh4" |]
            Expect.contains settings.ProductWhitelist "edh4" ""
        }
        test "Matches /eda" {
            let settings = parse [| "/eda" |]
            Expect.contains settings.ProductWhitelist "eda" ""
        }
        test "Matches /edo" {
            let settings = parse [| "/edo" |]
            Expect.contains settings.ProductWhitelist "edo" ""
        }
        
        yield! [
            "non steam linux runtime",               [ Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "non steam linux runtime custom folder", [ Path.Combine("Steam", "compatibilitytools.d", "Proton 5.0", "proton"); "protonAction" ]
            "non steam linux runtime custom folder case insensitive", [ Path.Combine("sTeAm", "compatibilitytools.d", "proton 5.0", "Proton"); "protonAction" ]
        ] |> List.map (fun (name, protonArgs) ->
            test $"Matches proton args {name}" {
                let launcherDir = "launchDir"
                let launcherPath = Path.Combine(launcherDir, "EDLaunch.exe")
                let args = protonArgs @ [launcherPath; "/other"; "/args"] |> List.toArray
                let settings = parse args
                
                let expected = { EntryPoint = "python3"; Args = args.[..^3] }
                Expect.equal settings.CompatTool (Some expected) ""
                Expect.equal settings.CbLauncherDir launcherDir ""
            }
        )
        
        yield! [
            "steam linux runtime - reaper",               [ Path.Combine("Steam", "ubuntu12_32", "reaper"); "SteamLaunch"; "AppId=359320"; "--"; Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "steam linux runtime - steam-launch-wrapper", [ Path.Combine("Steam", "ubuntu12_32", "steam-launch-wrapper"); "--"; Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "steam linux runtime - extra args",           [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "steam linux runtime",                        [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
        ] |> List.map (fun (name, protonArgs) ->
            test $"Matches proton args {name}" {
                let launcherDir = "launchDir"
                let launcherPath = Path.Combine(launcherDir, "EDLaunch.exe")
                let args = protonArgs @ [launcherPath; "/other"; "/args"] |> List.toArray
                
                let settings = parse args
                
                let expectedArgs = protonArgs.[1..^2] @ [ "python3" ] @ protonArgs.[^1..] |> List.toArray
                let expected = { EntryPoint = args.[0]; Args = expectedArgs }
                Expect.equal settings.CompatTool (Some expected) ""
                Expect.equal settings.CbLauncherDir launcherDir ""
            }
        )
        test "Matches wine args" {
            let launcherDir = "launchDir"
            let launcherPath = Path.Combine(launcherDir, "EDLaunch.exe")
            let settings = parse [| "wine"; launcherPath |]
            
            let expected = { EntryPoint = "wine"; Args = [||] } |> Some
            Expect.equal settings.CompatTool expected ""
        }
        test "First arg doesn't contain steamapps/common/Proton or SteamRuntimeLinux means no Proton" {
            let settings = parse [| "asdf"; "fdsa"; "launchDir" |]
            Expect.equal settings.CompatTool None ""
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
        test "Known flag shouldn't be treated as whitelist filter" {
            let knownFlags = [| "/steamid"; "/steam"; "/epic"; "/frontier"; "profile"; "/oculus"; "nonce"; "/restart"; "1"; "/vr"; "/novr"; "/autorun"; "/autoquit"; "/forcelocal" |]
            
            let settings = parse knownFlags
            
            Expect.isEmpty settings.ProductWhitelist ""
        }
        yield! [
            "no proton",           [ ]
            "proton",              [ Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "steam linux runtime", [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
        ] |> List.map (fun (name, protonArgs) ->            
            test $"EDLaunch path arg should be ignored {name}" {
                let launcherPath = Path.Combine("/launchDir", "EDLaunch.exe")
                let args = protonArgs @ [launcherPath] |> List.toArray
                
                let settings = parse args
                
                Expect.notContainsString settings.ProductWhitelist "EDLaunch.exe" StringComparison.OrdinalIgnoreCase ""
            }
        )
        
        testProperty "Unknown arg doesn't change any values" <|
            fun (args:string[]) ->
                // /* args are considered whitelist args and not unknown
                let args = args |> Array.filter (fun arg -> arg = null || (arg.StartsWith('/') && arg.Length < 2))
                let settings = parse args
                
                settings = Settings.defaults
    ]
