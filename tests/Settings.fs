module Tests.Settings

open Expecto
open Settings
open Types



[<Tests>]
let tests =
    let parse = parseArgs ILog.Noop Settings.defaults
      
    testList "Parings command line arguments" [
        test "Matches /steamid id" {
            let settings = parse [| "/steamid"; "123" |]
            Expect.equal settings.Platform (Steam "123") ""
            Expect.equal settings.ForceLocal true ""
        }
        test "Ignores /steamid without id as next arg" {
            let settings = parse [| "/steamid"; "/123" |]
            Expect.equal settings.Platform Settings.defaults.Platform ""
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
        test "Matches /noremotelogs" {
            let settings = parse [| "/noremotelogs" |]
            Expect.equal settings.RemoteLogging false ""
        }
        test "Matches /nowatchdog" {
            let settings = parse [| "/nowatchdog" |]
            Expect.equal settings.WatchForCrashes false ""
        }
        test "Matches /vr" {
            let settings = parse [| "/vr" |]
            Expect.equal settings.ProductMode Vr ""
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
        testProperty "Unknown arg doesn't change any values" <|
            fun (args:string[]) -> parse args = Settings.defaults
    ]
