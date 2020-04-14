module Tests.MachineId

open System
open Expecto
open EdLauncher.MachineId

[<Tests>]
let tests =
    testList "MachineId" [
        test "Correct combined id from machine and frontier ids" {
            let expected = "21e0c12b086d9897"
            let actual = getId "d42a0b1b-1dbe-4771-b204-8abcdcbc79c3" "f3e40e19-aa88-4e25-82a6-7fe0c0682674"
            
            Expect.equal actual expected ""
        }        
        test "Ignores leading and trailing whitespace" {
            let expected = "21e0c12b086d9897"
            let actual = getId " d42a0b1b-1dbe-4771-b204-8abcdcbc79c3 " "\tf3e40e19-aa88-4e25-82a6-7fe0c0682674\n"
            
            Expect.equal actual expected ""
        }
        test "Result is lowercase" {
            let id = getId "d42a0b1b-1dbe-4771-b204-8abcdcbc79c3" "f3e40e19-aa88-4e25-82a6-7fe0c0682674"
            
            Expect.all id (fun c -> Char.IsDigit(c) || Char.IsLower(c)) ""
        }
    ]

