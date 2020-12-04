module EdLauncher.Tests.EdLauncher

open System.IO
open Expecto
open EdLauncher.Cobra

[<Tests>]
let tests =
    let hasWriteAccess = (fun _ -> true)
    let noWriteAccess = (fun _ -> false)
    let productsDir = "Products"
    let fallbackDir = "fallback"
    let getProdDir = getProductsDir fallbackDir
    let launcherDir = "."
    
    testList "Absolute Products Directory" [
        test "In launcher directory if forceLocal is true and has write access" {
            let expected = Path.Combine(launcherDir, productsDir)
            let actual = getProdDir hasWriteAccess true "."
            
            Expect.equal actual expected ""
        }
        test "In launcher directory if forceLocal is true and doesn't have write access" {
            let expected = Path.Combine(launcherDir, productsDir)
            let actual = getProdDir noWriteAccess true "."
            
            Expect.equal actual expected ""
        }
        test "In launcher directory if forceLocal is false and has write access" {
            let expected = Path.Combine(launcherDir, productsDir)
            let actual = getProdDir hasWriteAccess false "."
            
            Expect.equal actual expected ""
        }
        test "In fallback directory if forceLocal is false and doesn't have write access" {
            let expected = Path.Combine(fallbackDir, productsDir)
            let actual = getProdDir noWriteAccess false "."
            
            Expect.equal actual expected ""
        }
    ]
