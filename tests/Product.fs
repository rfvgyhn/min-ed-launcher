module MinEdLauncher.Tests.Product

open System.IO
open MinEdLauncher.Product
open MinEdLauncher.Token
open MinEdLauncher.Types
open Expecto

    module Expect =
        let notStringContains (subject : string) (substring : string) message =
          if (subject.Contains(substring)) then
            failtestf "%s. Expected subject string '%s' to not contain substring '%s'."
                            message subject substring

    [<Tests>]
    let tests =
        let product =
            { Sku = ""
              Name = ""
              Filters = Set.empty
              Executable = ""
              UseWatchDog64 = false
              SteamAware = false
              Version = System.Version()
              Mode = Offline
              Directory = ""
              GameArgs = ""
              ServerArgs = "" }
        let getTimestamp = fun () -> (double)1
        let hashFile = fun str -> Result.Ok Array.empty<byte>
        let token = EdSession.Empty
            
        testList "Product" [
            testList "Argument String" [
                test "Language provided" {
                    let actual = createArgString Vr (Some "theLang") token "" getTimestamp false Dev hashFile product
                    
                    Expect.stringContains actual "/language theLang" ""
                }
                test "No language provided" {
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "/language" ""
                    Expect.notStringContains actual "theLang" ""
                }
                test "Steam platform and steam aware product" {
                    let product = { product with SteamAware = true }
                    let actual = createArgString Vr None token "" getTimestamp false Steam hashFile product
                    
                    Expect.stringContains actual "/steam" ""
                }
                test "Steam platform and non steam aware product" {
                    let product = { product with SteamAware = false }
                    let actual = createArgString Vr None token "" getTimestamp false Steam hashFile product
                    
                    Expect.notStringContains actual "/steam" ""
                }
                test "Non steam platform and steam aware product" {
                    let product = { product with SteamAware = true }
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "/steam" ""
                }
                test "Non steam platform and non steam aware product" {
                    let product = { product with SteamAware = false }
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "/steam" ""
                }
                test "Epic platform contains refresh token" {
                    let token = { EdSession.Empty with PlatformToken = Expires (fun () -> { RefreshableToken.Empty with RefreshToken = "asdf" }) }
                    let actual = createArgString Vr None token "" getTimestamp false (Epic EpicDetails.Empty) hashFile product
                    
                    Expect.stringContains actual "\"EpicToken asdf\"" ""
                }
                test "Non epic platform doesn't contain refresh token" {
                    let token = { EdSession.Empty with PlatformToken = Expires (fun () -> { RefreshableToken.Empty with RefreshToken = "asdf" }) }
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.notStringContains actual "\"EpicToken" ""
                }
                test "VR mode" {
                    let actual = createArgString Vr None token "" getTimestamp false Dev hashFile product
                    
                    Expect.stringContains actual "/vr" ""
                }
                test "Non VR mode" {
                    let actual = createArgString Pancake None token "" getTimestamp false Dev hashFile product
                    
                    Expect.stringContains actual "/novr" ""
                }
                test "ServerToken is used when product is online and not watching for crashes" {
                    let session = { EdSession.Empty with Token = "54321"; MachineToken = "12345" }
                    let serverArgs = "/some arg"
                    let gameArgs = "/gameargs"
                    let product = { product with ServerArgs = serverArgs; Mode = Online; GameArgs = gameArgs }
                    let actual = createArgString Vr None session "" getTimestamp false Dev hashFile product
                    
                    let expected = $"\"ServerToken %s{session.MachineToken} %s{session.Token} %s{serverArgs}\""
                    Expect.stringStarts actual expected ""
                    Expect.stringEnds actual gameArgs ""
                }
                test "Product is online and watching for crashes" {
                    let session = { EdSession.Empty with Token = "456"; MachineToken = "123" }
                    let serverArgs = "/Test"
                    let machineId = "789"
                    let timeStamp = 12345.12345
                    let hashFile = fun _ -> Result.Ok [|228uy; 20uy; 11uy; 154uy;|]
                    let version = System.Version(1, 2, 3)
                    let product = { product with ServerArgs = serverArgs; Mode = Online; Version = version; Directory = Path.Combine("path", "to"); Executable = "theExe.exe" }
                    let actual = createArgString Vr None session machineId timeStamp true Dev hashFile product
                    
                    let expectedExe = sprintf "/Executable \"%s\"" (Path.Combine("path", "to", "theExe.exe"))
                    let expectedExeArgs = sprintf "/ExecutableArgs \"%s\"" <| sprintf "\"\"ServerToken %s %s %s\"\" /vr" session.MachineToken session.Token serverArgs
                    let expectedMachineToken = $"/MachineToken %s{session.MachineToken}"
                    let expectedVersion = $"/Version %s{version.ToString()}"
                    let expectedsessionToken = $"/AuthToken %s{session.Token}"
                    let expectedMachineId = $"/MachineId %s{machineId}"
                    let expectedTime = $"/Time %s{timeStamp.ToString()}"
                    let expectedHash = $"/ExecutableHash \"E4140B9A\""
                    Expect.stringContains actual expectedExe ""
                    Expect.stringContains actual expectedExeArgs ""
                    Expect.stringContains actual expectedMachineToken ""
                    Expect.stringContains actual expectedVersion ""
                    Expect.stringContains actual expectedsessionToken ""
                    Expect.stringContains actual expectedMachineId ""
                    Expect.stringContains actual expectedTime ""
                    Expect.stringContains actual expectedHash ""
                }
            ]
    //        testProperty "Unknown arg doesn't change any values" <|
    //            fun (args:string[]) -> parse args = Settings.defaults
        ]
