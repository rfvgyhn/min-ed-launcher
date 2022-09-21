module MinEdLauncher.Tests.AuthorizedProduct

open MinEdLauncher
open MinEdLauncher.Types
open Expecto

[<Tests>]
let tests =            
    testList "AuthorizedProduct" [
        testList "fixDirectoryPath" [
            let product =
                { Name = ""; Filter = ""; DirectoryName = "directory"; GameArgs = ""; ServerArgs = ""; SortKey = 0; Sku = "sku"; TestApi = false }
            
            testList "Steam" [
                test "Prefers [directory].rdr over SKU" {
                    let directoryExists _ = true
                    let fileExists (path: string) = path.EndsWith($"{product.DirectoryName}.rdr")
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" Steam directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.DirectoryName ""
                }
                test "Prefers directory over SKU" {
                    let directoryExists _ = true
                    let fileExists (path: string) = not (path.EndsWith($"{product.DirectoryName}.rdr"))
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" Steam directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.DirectoryName ""
                }
                test "Uses SKU if [directory].rdr or directory doesn't exist" {
                    let directoryExists path = path <> product.DirectoryName
                    let fileExists (path: string) = not (path.EndsWith($"{product.DirectoryName}.rdr"))
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" Steam directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.Sku ""
                }
            ]
            
            testList "Epic" [
                test "Prefers [directory].rdr over SKU" {
                    let directoryExists _ = true
                    let fileExists (path: string) = path.EndsWith($"{product.DirectoryName}.rdr")
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" (Epic EpicDetails.Empty) directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.DirectoryName ""
                }
                test "Prefers directory over SKU" {
                    let directoryExists _ = true
                    let fileExists (path: string) = not (path.EndsWith($"{product.DirectoryName}.rdr"))
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" (Epic EpicDetails.Empty) directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.DirectoryName ""
                }
                test "Uses SKU if [directory].rdr or directory doesn't exist" {
                    let directoryExists path = path <> product.DirectoryName
                    let fileExists (path: string) = not (path.EndsWith($"{product.DirectoryName}.rdr"))
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" (Epic EpicDetails.Empty) directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.Sku ""
                }
            ]
            
            testList "Frontier" [
                test "Prefers [sku].rdr over directory" {
                    let directoryExists _ = true
                    let fileExists (path: string) = path.EndsWith($"{product.Sku}.rdr")
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" (Frontier FrontierDetails.Empty) directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.Sku ""
                }
                test "Prefers SKU over directory" {
                    let directoryExists _ = true
                    let fileExists (path: string) = not (path.EndsWith($"{product.Sku}.rdr"))
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" (Frontier FrontierDetails.Empty) directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.Sku ""
                }
                test "Uses directory if [SKU].rdr or SKU doesn't exist" {
                    let directoryExists path = path <> product.Sku
                    let fileExists (path: string) = not (path.EndsWith($"{product.Sku}.rdr"))
                    
                    let actual = AuthorizedProduct.fixDirectoryName "" (Frontier FrontierDetails.Empty) directoryExists fileExists product
                    
                    Expect.equal actual.DirectoryName product.DirectoryName ""
                }
            ]
        ]
        
        testList "fixFilters" [
            let product =
                { Name = ""; Filter = "oldFilter"; DirectoryName = ""; GameArgs = ""; ServerArgs = ""; SortKey = 0; Sku = "sku"; TestApi = false }
            test "No change when overrides is empty" {
                let actual = AuthorizedProduct.fixFilters OrdinalIgnoreCaseMap.empty product
                
                Expect.equal actual product ""
            }
            test "No change when no matching sku" {
                let overrides = [ "asdf", "newFilter" ] |> OrdinalIgnoreCaseMap.ofSeq
                let actual = AuthorizedProduct.fixFilters overrides product
                
                Expect.equal actual product ""
            }
            test "Updates filter when matching sku" {
                let newFilter = "newFilter"
                let overrides = [ product.Sku, newFilter ] |> OrdinalIgnoreCaseMap.ofSeq
                let actual = AuthorizedProduct.fixFilters overrides product
                
                Expect.equal actual.Filter newFilter ""
            }
        ]
    ]
