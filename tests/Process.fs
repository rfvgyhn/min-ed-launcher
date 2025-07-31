module MinEdLauncher.Tests.Process

open System
open System.Diagnostics
open Expecto

[<Tests>]
let tests =            
    testList "Process" [
        let MEL_LIBRARY_PATH = "MEL_LD_LIBRARY_PATH"
        let LD_LIBRARY_PATH = "LD_LIBRARY_PATH"
        let overrideTests setup = [            
            test "Sets LD_LIBRARY_PATH if MEL_LD_LIBRARY_PATH is set" {
                setup (fun () ->
                    let startInfo = ProcessStartInfo()
                
                    Environment.SetEnvironmentVariable(MEL_LIBRARY_PATH, "test")
                    MinEdLauncher.Process.overrideLdLibraryPath startInfo |> ignore
                    
                    Expect.isTrue (startInfo.EnvironmentVariables.ContainsKey(LD_LIBRARY_PATH)) ""
                    Expect.equal startInfo.EnvironmentVariables[LD_LIBRARY_PATH] "test" ""
                )
            }
            test "Doesn't set LD_LIBRARY_PATH if MEL_LD_LIBRARY_PATH is not set" {
                setup (fun () ->
                    let startInfo = ProcessStartInfo()
                    
                    MinEdLauncher.Process.overrideLdLibraryPath startInfo |> ignore
                    
                    Expect.isFalse (startInfo.EnvironmentVariables.ContainsKey(LD_LIBRARY_PATH)) ""
                )
            }
        ]
        overrideTests (fun test ->
            try
                test()
            finally
                Environment.SetEnvironmentVariable(MEL_LIBRARY_PATH, null)
                Environment.SetEnvironmentVariable(LD_LIBRARY_PATH, null)
        )
        |> testList "overrideLdLibraryPath"
        |> testSequenced
    ]
