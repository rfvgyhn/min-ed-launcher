module MinEdLauncher.Tests.Cobra

open MinEdLauncher
open MinEdLauncher.Types
open Expecto

[<Tests>]
let tests =            
    testList "Cobra" [
        testList "getProductDir" [
            let defaultProductsDir = "Products"
            let product =
                { Name = ""; Filter = ""; DirectoryName = "directory"; GameArgs = ""; ServerArgs = ""; SortKey = 0; Sku = "sku"; TestApi = false }
            
            test "Uses first path that exists in .rdr" {
                let lines = [|"invalid"; "valid"; "valid2"|]
                let readAllLines _ = lines
                let fileExists _ = true
                let directoryExists path = path <> lines[0] 
                
                let actual = Cobra.getProductDir defaultProductsDir fileExists readAllLines directoryExists product.DirectoryName
                
                Expect.equal actual lines[1] ""
            }            
            test "Ignores whitespace in .rdr lines" {
                let lines = [|" path "|]
                let readAllLines _ = lines
                let fileExists _ = true
                let directoryExists _ = true
                
                let actual = Cobra.getProductDir defaultProductsDir fileExists readAllLines directoryExists product.DirectoryName
                
                Expect.equal actual (lines[0].Trim()) ""
            }            
            test "Uses default if no .rdr" {
                let readAllLines _ = Array.empty
                let fileExists (path: string) = not (path.EndsWith(".rdr"))
                let directoryExists _ = true
                
                let actual = Cobra.getProductDir defaultProductsDir fileExists readAllLines directoryExists product.DirectoryName
                
                Expect.equal actual defaultProductsDir ""
            }            
            test "Uses default if no paths in .rdr exist" {
                let readAllLines _ = [|"path"; "path2"|]
                let fileExists _ = true
                let directoryExists _ = false
                
                let actual = Cobra.getProductDir defaultProductsDir fileExists readAllLines directoryExists product.DirectoryName
                
                Expect.equal actual defaultProductsDir ""
            }
        ]
    ]
