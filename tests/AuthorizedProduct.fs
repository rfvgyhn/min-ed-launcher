module MinEdLauncher.Tests.AuthorizedProduct

open MinEdLauncher
open MinEdLauncher.Types
open Expecto

[<Tests>]
let tests =            
    testList "AuthorizedProduct" [
        testList "fixDirectoryPath" [
            let product =
                { Name = ""; Filter = ""; Directory = "directory"; GameArgs = ""; ServerArgs = ""; SortKey = 0; Sku = "sku"; TestApi = false }
            
            test "Steam prefers Directory over SKU" {
                let directoryExists path = true
                
                let actual = AuthorizedProduct.fixDirectoryPath "" Steam directoryExists product
                
                Expect.equal actual.Directory product.Directory ""
            }
            test "Steam uses SKU if Directory doesn't exist" {
                let directoryExists path = if path = product.Directory then false else true
                
                let actual = AuthorizedProduct.fixDirectoryPath "" Steam directoryExists product
                
                Expect.equal actual.Directory product.Sku ""
            }
            test "Epic prefers Directory over SKU" {
                let directoryExists path = true
                
                let actual = AuthorizedProduct.fixDirectoryPath "" (Epic EpicDetails.Empty) directoryExists product
                
                Expect.equal actual.Directory product.Directory ""
            }
            test "Epic uses SKU if Directory doesn't exist" {
                let directoryExists path = if path = product.Directory then false else true
                
                let actual = AuthorizedProduct.fixDirectoryPath "" (Epic EpicDetails.Empty) directoryExists product
                
                Expect.equal actual.Directory product.Sku ""
            }
            test "Frontier prefers SKU over directory" {
                let directoryExists path = true
                
                let actual = AuthorizedProduct.fixDirectoryPath "" (Frontier FrontierDetails.Empty) directoryExists product
                
                Expect.equal actual.Directory product.Sku ""
            }
            test "Frontier uses Directory if SKU doesn't exist" {
                let directoryExists path = if path = product.Sku then false else true
                
                let actual = AuthorizedProduct.fixDirectoryPath "" (Frontier FrontierDetails.Empty) directoryExists product
                
                Expect.equal actual.Directory product.Directory ""
            }
        ]
    ]
