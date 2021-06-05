[<RequireQualifiedAccess>]
module MinEdLauncher.AuthorizedProduct

open System.IO
open MinEdLauncher.Types

let fixDirectoryPath productsDir platform directoryExists (product: AuthorizedProduct) =
    // I don't know why, but the default launcher changes the directory path based on platform
    // If Steam or Epic, try the following paths
    //     1. Products/directory
    //     2. Products/sku 
    // else
    //     1. Products/sku
    //     2. Products/directory
    let dirExists path = directoryExists (Path.Combine(productsDir, path))
    let dir =
        match platform with
        | Steam | Epic _ ->
            if dirExists product.Directory then product.Directory else product.Sku
        | Frontier _ | Oculus _ | Dev ->
            if dirExists product.Sku then product.Sku else product.Directory
    { product with Directory = dir }
    
// Allows for overriding a product's filter. Useful for when FDev makes a copy/paste error
// for a new product (i.e. when they released Odyssey with an "edh" filter instead of "edo") 
let fixFilters (overrides: OrdinalIgnoreCaseMap<string>) (product: AuthorizedProduct) =    
    overrides.TryFind product.Sku
    |> Option.map (fun filter -> { product with Filter = filter })
    |> Option.defaultValue product