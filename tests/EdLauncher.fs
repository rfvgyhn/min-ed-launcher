module MinEdLauncher.Tests.EdLauncher

open MinEdLauncher
open System.IO
open Expecto

[<Tests>]
let tests =
    let launcherDir = "."
    let productsDir = "Products"
    let fallbackDir = "fallback"
    let hasLauncherWriteAccess = (fun d -> d = launcherDir)
    let noLauncherWriteAccess = (fun d -> d <> launcherDir)
    let hasProductsWriteAccess = (fun (d: string) -> d.EndsWith(productsDir))
    let noProductsWriteAccess = (fun (d: string) -> not (d.EndsWith(productsDir)))
    let dirExists = (fun _ -> true)
    let dirNotExists = (fun _ -> false)
    let getProdDir = Cobra.getDefaultProductsDir fallbackDir
    
    testList "Absolute Products Directory" [
        test "In launcher directory if forceLocal is true and has write access to launcherDir and productsDir doesn't exist" {
            let expected = Path.Combine(launcherDir, productsDir) |> Cobra.ProductsDir.PermissionsIgnored
            let actual = getProdDir hasLauncherWriteAccess dirNotExists true launcherDir
            
            Expect.equal actual expected ""
        }
        test "In launcher directory if forceLocal is true and has write access productsDir" {
            let expected = Path.Combine(launcherDir, productsDir) |> Cobra.ProductsDir.PermissionsIgnored
            let actual = getProdDir hasProductsWriteAccess dirExists true launcherDir
            
            Expect.equal actual expected ""
        }
        test "In launcher directory if forceLocal is true and doesn't have write access to launcherDir when productsDir doesn't exist" {
            let expected = Path.Combine(launcherDir, productsDir) |> Cobra.ProductsDir.PermissionsIgnored
            let actual = getProdDir hasLauncherWriteAccess dirNotExists true launcherDir
            
            Expect.equal actual expected ""
        }
        test "In launcher directory if forceLocal is true and doesn't have write access to productsDir" {
            let expected = Path.Combine(launcherDir, productsDir) |> Cobra.ProductsDir.PermissionsIgnored
            let actual = getProdDir noProductsWriteAccess dirExists true launcherDir
            
            Expect.equal actual expected ""
        }
        test "In launcher directory if forceLocal is false and has write access to productsDir" {
            let expected = Path.Combine(launcherDir, productsDir) |> Cobra.ProductsDir.Local
            let actual = getProdDir hasProductsWriteAccess dirExists false launcherDir
            
            Expect.equal actual expected ""
        }
        test "In launcher directory if forceLocal is false and has write access launcherDir when productsDir doesn't exist" {
            let expected = Path.Combine(launcherDir, productsDir) |> Cobra.ProductsDir.Local
            let actual = getProdDir hasLauncherWriteAccess dirNotExists false launcherDir
            
            Expect.equal actual expected ""
        }
        test "In fallback directory if forceLocal is false and doesn't have write access to launcherDir when productsDir doesn't exist" {
            let expected = (Path.Combine(fallbackDir, productsDir), Path.Combine(launcherDir, productsDir)) |> Cobra.ProductsDir.NoWriteAccess
            let actual = getProdDir noLauncherWriteAccess dirNotExists false launcherDir
            
            Expect.equal actual expected ""
        }
        test "In fallback directory if forceLocal is false and doesn't have write access productsDir but has access to launcherDir" {
            let expected = (Path.Combine(fallbackDir, productsDir), Path.Combine(launcherDir, productsDir)) |> Cobra.ProductsDir.NoWriteAccess
            let actual = getProdDir noProductsWriteAccess dirExists false launcherDir
            
            Expect.equal actual expected ""
        }
    ]
