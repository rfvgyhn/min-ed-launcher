module MinEdLauncher.Tests.Settings

open System
open System.IO
open Expecto
open MinEdLauncher
open MinEdLauncher.Settings
open MinEdLauncher.Types
open MinEdLauncher.Tests.Extensions

let private writeJsonToTempFile (json: string) =
    let path = Path.Combine(Path.GetTempPath(), $"test-settings-{Guid.NewGuid()}.json")
    File.WriteAllText(path, json)
    path

let private minimalJson = """{
    "apiUri": "https://api.zaonce.net",
    "watchForCrashes": false,
    "autoUpdate": true,
    "checkForLauncherUpdates": true,
    "maxConcurrentDownloads": 4,
    "forceUpdate": "",
    "processes": [],
    "shutdownProcesses": [],
    "filterOverrides": [],
    "additionalProducts": []
}"""

[<Tests>]
let parseConfigTests =
    testList "Parsing config file" [
        test "Unknown key with close match suggests correction" {
            let json = minimalJson.Replace("\"forceUpdate\"", "\"forceUdate\"")
            let path = writeJsonToTempFile json
            try
                let result = parseConfig path
                Expect.isOk result "Config should still parse with unknown keys"
            finally
                File.Delete(path)
        }
        test "forceUpdate as comma-separated string" {
            let json = minimalJson.Replace("\"forceUpdate\": \"\"", "\"forceUpdate\": \"a, b , c\"")
            let path = writeJsonToTempFile json
            try
                let config = Expect.wantOk (parseConfig path) ""
                Expect.equal config.ForceUpdate ["a"; "b"; "c"] ""
            finally
                File.Delete(path)
        }
        test "forceUpdate as JSON array" {
            let json = minimalJson.Replace("\"forceUpdate\": \"\"", "\"forceUpdate\": [\"x\", \"y\"]")
            let path = writeJsonToTempFile json
            try
                let config = Expect.wantOk (parseConfig path) ""
                Expect.equal config.ForceUpdate ["x"; "y"] ""
            finally
                File.Delete(path)
        }
        test "forceUpdate as empty string yields empty list" {
            let path = writeJsonToTempFile minimalJson
            try
                let config = Expect.wantOk (parseConfig path) ""
                Expect.isEmpty config.ForceUpdate ""
            finally
                File.Delete(path)
        }
        test "forceUpdate as empty array yields empty list" {
            let json = minimalJson.Replace("\"forceUpdate\": \"\"", "\"forceUpdate\": []")
            let path = writeJsonToTempFile json
            try
                let config = Expect.wantOk (parseConfig path) ""
                Expect.isEmpty config.ForceUpdate ""
            finally
                File.Delete(path)
        }
    ]

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
        test "Matches epic sandbox id" {
            let settings = parse [| "-epicsandboxid=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with SandboxId = Some "asdf" }) ""
        }
        test "Matches epic deployment id" {
            let settings = parse [| "-epicdeploymentid=asdf" |]
            Expect.equal settings.Platform (Epic { EpicDetails.Empty with DeploymentId = Some "asdf" }) ""
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
            "proton - no wrapper",                 [ Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "proton - no wrapper - custom folder", [ Path.Combine("sTeAm", "compatibilitytools.d", "compat-tool", "proton"); "protonAction" ]
            "proton - reaper",                     [ Path.Combine("Steam", "ubuntu12_32", "reaper"); "SteamLaunch"; "AppId=359320"; "--"; Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "proton - steam-launch-wrapper",       [ Path.Combine("Steam", "ubuntu12_32", "steam-launch-wrapper"); "--"; Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "proton - extra args",                 [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--deploy=soldier"; "--suite=soldier"; "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "proton - steam linux runtime",        [ Path.Combine("steamapps", "common", "SteamLinuxRuntime_soldier", "_v2-entry-point"); "--verb=protonAction"; "--"; Path.Combine("steamapps", "common", "Proton 5.0", "proton"); "protonAction" ]
            "proton - cachyos",                    [ Path.Combine("Steam", "ubuntu12_32", "steam-launch-wrapper"); "--"; Path.Combine("Steam", "ubuntu12_32", "reaper"); "SteamLaunch"; "AppId=359320"; "--"; Path.Combine("steam", "compatibilitytools.d", "proton-cachyos", "proton"); "waitforexitandrun" ]
            "wine",                                [ "wine" ]
        ] |> List.map(fun (name, protonArgs) ->
            test $"Matches compat tool args {name}" {
                let launcherDir = "launchDir"
                let launcherArgs = [Path.Combine(launcherDir, "EDLaunch.exe"); "/other"; "/args"]
                let args = protonArgs @ launcherArgs |> List.toArray
                
                let settings = parse args
                
                let expected = Some { EntryPoint = args[0]; Args = args[1..^launcherArgs.Length] }
                Expect.equal settings.CompatTool expected ""
                Expect.equal settings.CbLauncherDir launcherDir ""
            }
        )
        
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

[<Tests>]
let configTests =
    let parseConfigWithProcesses (processJson: string) =
        let json = $"""{{
            "apiUri": "https://api.zaonce.net",
            "watchForCrashes": false,
            "processes": [%s{processJson}],
            "shutdownProcesses": [],
            "filterOverrides": [],
            "additionalProducts": []
        }}"""
        let path = Path.GetTempFileName()
        try
            File.WriteAllText(path, json)
            match parseConfig path with
            | Ok config -> config.Processes
            | Error _ -> failwith "Failed to parse config"
        finally
            File.Delete(path)

    testList "Parsing process delay config" [
        test "No delay fields defaults to 0 seconds and processStart" {
            let procs = parseConfigWithProcesses """{ "fileName": "/usr/bin/test" }"""
            Expect.hasLength procs 1 ""
            Expect.equal procs.[0].Delay 0 ""
            Expect.equal procs.[0].DelayReference None ""
        }
        test "delay only defaults to processStart reference" {
            let procs = parseConfigWithProcesses """{ "fileName": "/usr/bin/test", "delay": 10 }"""
            Expect.equal procs.[0].Delay 10 ""
            Expect.equal procs.[0].DelayReference None ""
        }
        test "delay with gameLaunch reference" {
            let procs = parseConfigWithProcesses """{ "fileName": "/usr/bin/test", "delay": 5, "delayReference": "gameLaunch" }"""
            Expect.equal procs.[0].Delay 5 ""
            Expect.equal procs.[0].DelayReference (Some "gameLaunch") ""
        }
        test "delay with gameRunning reference" {
            let procs = parseConfigWithProcesses """{ "fileName": "/usr/bin/test", "delay": 0, "delayReference": "gameRunning" }"""
            Expect.equal procs.[0].Delay 0 ""
            Expect.equal procs.[0].DelayReference (Some "gameRunning") ""
        }
        test "Negative delay is preserved" {
            let procs = parseConfigWithProcesses """{ "fileName": "/usr/bin/test", "delay": -5, "delayReference": "gameLaunch" }"""
            Expect.equal procs.[0].Delay -5 ""
        }
    ]

[<Tests>]
let delayReferenceTests =
    testList "Parsing delay reference values" [
        test "None defaults to ProcessStart" {
            let result = Settings.parseDelayReference None
            Expect.equal result ProcessStart ""
        }
        test "processStart maps to ProcessStart" {
            let result = Settings.parseDelayReference (Some "processStart")
            Expect.equal result ProcessStart ""
        }
        test "gameLaunch maps to GameLaunch" {
            let result = Settings.parseDelayReference (Some "gameLaunch")
            Expect.equal result GameLaunch ""
        }
        test "gameRunning maps to GameRunning" {
            let result = Settings.parseDelayReference (Some "gameRunning")
            Expect.equal result GameRunning ""
        }
        test "Unknown value defaults to ProcessStart" {
            let result = Settings.parseDelayReference (Some "bogus")
            Expect.equal result ProcessStart ""
        }
        test "Case insensitive matching" {
            let result = Settings.parseDelayReference (Some "GAMELAUNCH")
            Expect.equal result GameLaunch ""
        }
    ]
