module EdLauncher.Cobra
open System
open System.Diagnostics
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
        Error <| sprintf "Unable to find CBViewModel.dll in directory %s" cbLauncherDir
    else
        let cobraVersion =
            let version = FileVersionInfo.GetVersionInfo(cobraPath)
            if String.IsNullOrEmpty(version.FileVersion) then version.ProductVersion else version.FileVersion
        let launcherVersion = typeof<Steam>.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion
        
        Ok (cobraVersion, launcherVersion)

let getGameLang cbLauncherDir =
    let asm = Assembly.LoadFrom(Path.Combine(cbLauncherDir, "LocalResources.dll"))
    let resManager = ResourceManager("LocalResources.Properties.Resources", asm)
    try
        resManager.GetString("GameLanguage") |> Some
    with
    | e -> None