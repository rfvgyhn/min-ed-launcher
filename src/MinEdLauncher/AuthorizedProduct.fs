[<RequireQualifiedAccess>]
module MinEdLauncher.AuthorizedProduct

open System
open System.IO
open Microsoft.Extensions.Configuration
open MinEdLauncher.Types
open Rop

let fixDirectoryName productsDir platform directoryExists fileExists (product: AuthorizedProduct) =
    // I don't know why, but the default launcher changes the directory name based on platform
    // If Steam or Epic, try the following paths
    //     1. Products/[directory].rdr if it exists
    //     2. Products/[directory] if it exists
    //     3. Products/[sku].rdr if it exists
    //     4. Products/[sku]
    // else
    //     1. Products/[sku].rdr if it exists
    //     2. Products/[sku] if it exists
    //     3. Products/[directory].rdr if it exists
    //     4. Products/[directory]
    let exists (check: string -> bool) path = check (Path.Combine(productsDir, path))
    let dirExists = exists directoryExists
    let fileExists = exists fileExists
    let dir =
        match platform with
        | Steam | Epic _ ->
            if fileExists $"{product.DirectoryName}.rdr" || dirExists product.DirectoryName then
                product.DirectoryName
            else
                product.Sku
        | Frontier _ | Oculus _ | Dev ->
            if fileExists $"{product.Sku}.rdr" || dirExists product.Sku then
                product.Sku
            else
                product.DirectoryName
            
    { product with DirectoryName = dir }
    
// Allows for overriding a product's filter. Useful for when FDev makes a copy/paste error
// for a new product (i.e. when they released Odyssey with an "edh" filter instead of "edo") 
let fixFilters (overrides: OrdinalIgnoreCaseMap<string>) (product: AuthorizedProduct) =    
    overrides.TryFind product.Sku
    |> Option.map (fun filter -> { product with Filter = filter })
    |> Option.defaultValue product
    
let fromJson element =
    let directory = element |> Json.parseProp "directory" >>= Json.toString
    let sortKey = element |> Json.parseProp "sortkey" >>= Json.toInt 
    let name = element |> Json.parseProp "product_name" >>= Json.toString
    let sku = element |> Json.parseProp "product_sku" >>= Json.toString
    match directory, sortKey, name, sku with
    | Ok directory, Ok sortKey, Ok name, Ok sku ->
        Ok { Name = name
             Filter = element |> Json.parseProp "filter" >>= Json.toString |> Result.defaultValue ""
             DirectoryName = directory
             GameArgs = element |> Json.parseProp "gameargs" >>= Json.toString |> Result.defaultValue ""
             ServerArgs = element |> Json.parseProp "serverargs" >>= Json.toString |> Result.defaultValue ""
             SortKey = sortKey
             Sku = sku
             TestApi = element |> Json.parseProp "testapi" >>= Json.asBool |> Result.defaultValue false }
    | _ ->
        Error $"Unexpected json object %s{element.ToString()}"
    
let private (|NotEmpty|_|) (str: string) = if String.IsNullOrWhiteSpace(str) then None else Some str
let fromConfig (section: IConfigurationSection) =
    let directory = section.GetValue<string>("directory")
    let name = section.GetValue<string>("product_name")
    let sku = section.GetValue<string>("product_sku")
    match directory, name, sku with
    | NotEmpty directory, NotEmpty name, NotEmpty sku ->
        Ok { Name = name
             Filter = section.GetValue<string>("filter")
             DirectoryName = directory
             GameArgs = section.GetValue<string>("gameargs")
             ServerArgs = section.GetValue<string>("serverargs")
             SortKey = section.GetValue<int>("sortKey")
             Sku = sku
             TestApi = section.GetValue<bool>("testapi") }
    | _ ->
        Error $"Invalid configuration section: %s{section.Path}"