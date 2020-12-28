module MinEdLauncher.Cobra
open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Reflection
open System.Resources
open Steam
open Types
        
let getProductsDir fallbackPath hasWriteAccess (forceLocal:ForceLocal) launcherDir =
    let productsPath = "Products"
    let localPath = Path.Combine(launcherDir, productsPath)
    if forceLocal then localPath
    elif hasWriteAccess launcherDir then localPath
    else Path.Combine(fallbackPath, productsPath)
    
let getVersion cbLauncherDir =
    let cobraPath = Path.Combine(cbLauncherDir, "CBViewModel.dll")
    
    if not (File.Exists cobraPath) then
        Error $"Unable to find CBViewModel.dll in directory %s{cbLauncherDir}"
    else
        let cobraVersion =
            let version = FileVersionInfo.GetVersionInfo(cobraPath)
            if String.IsNullOrEmpty(version.FileVersion) then version.ProductVersion else version.FileVersion
        let launcherVersion = typeof<Steam>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion
        
        Ok (cobraVersion, launcherVersion)

let getGameLang cbLauncherDir langCode =
    let asm = Assembly.LoadFrom(Path.Combine(cbLauncherDir, $"LocalResources.dll"))
    let resManager = ResourceManager("LocalResources.Properties.Resources", asm)
    try
        langCode
        |> Option.bind (fun c ->
            if Directory.Exists(Path.Combine(cbLauncherDir, c))
               || c.Equals("en", StringComparison.OrdinalIgnoreCase)
            then c.ToLowerInvariant() |> Some
            else None)
        |> Option.map (fun c ->
            let culture = CultureInfo.CreateSpecificCulture(c)
            resManager.GetString("GameLanguage", culture))
        |> Option.orElseWith (fun () -> resManager.GetString("GameLanguage") |> Some)
    with e -> None