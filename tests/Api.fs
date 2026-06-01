module EdLauncher.Tests.Api

open Expecto
open MinEdLauncher

[<Tests>]
let tests =            
    testList "getAccountName" [
        let fId = Some "abc1234"
        let registeredName = Some "Dwight Schrute"
        
        test "Checks Frontier Id" {
            let aliases = [(fId.Value, "test")] |> Map.ofList
            let alias = Api.getAccountName aliases fId registeredName
            
            Expect.equal alias (Some "test") ""
        }
        
        test "Checks registered name" {
            let aliases = [(registeredName.Value, "test")] |> Map.ofList
            let alias = Api.getAccountName aliases fId registeredName
            
            Expect.equal alias (Some "test") ""
        }
        
        test "Falls back to registered name if no alias found" {
            let aliases = Map.empty
            let alias = Api.getAccountName aliases fId registeredName
            
            Expect.equal alias registeredName ""
        }
        
        test "Prioritizes Frontier Id over registered name" {
            let aliases = [(fId.Value, "fid"); (registeredName.Value, "name")] |> Map.ofList
            let alias = Api.getAccountName aliases fId registeredName
            
            Expect.equal alias (Some "fid") ""
        }
    ]