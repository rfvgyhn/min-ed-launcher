namespace EdLauncher

open System
open System.IO

module Types =
    type ILog =
        { Debug: string -> unit
          Info: string -> unit
          Warn: string -> unit
          Error: string -> unit }
        with static member Noop = { Debug = (fun _ -> ()); Info = (fun _ -> ()); Warn = (fun _ -> ()); Error = (fun _ -> ()) }
    type Platform =
        | Steam of string
        | Frontier
        | Oculus of string
        | Dev
    type DisplayMode = Vr | Pancake
    type AutoRun = bool
    type AutoQuit = bool
    type WatchForCrashes = bool
    type RemoteLogging = bool
    type ForceLocal = bool
    type LauncherSettings =
        { Platform: Platform
          DisplayMode: DisplayMode
          AutoRun: AutoRun
          AutoQuit: AutoQuit
          WatchForCrashes: WatchForCrashes
          RemoteLogging: RemoteLogging
          ProductWhitelist: Set<string>
          ForceLocal: ForceLocal }
    type ServerStatus = Healthy
    type LocalVersion = Version
    type LauncherStatus =
        | Current
        | Supported
        | Expired
        | Future
    type ServerInfo =
        { Status: ServerStatus}
    type User =
        { Name: string
          EmailAddress: string
          SessionToken: string
          MachineToken: string }
    type AuthorizedProduct =
        { Name: string
          Filter: string
          Directory: string
          ServerArgs: string
          GameArgs: string
          SortKey: int
          Sku: string
          TestApi: bool }
    type ProductMode = Online | Offline
    type VersionInfo =
        { Name: string
          Executable: string
          UseWatchDog64: bool
          SteamAware: bool
          Version: Version
          Mode: ProductMode }
    type ProductDetails =
        { Sku: string
          Name: string
          Filters: Set<string>
          Executable: string
          UseWatchDog64: bool
          SteamAware: bool
          Version: Version
          Mode: ProductMode
          Directory: string
          GameArgs: string
          ServerArgs: string }
    type MissingProductDetails =
        { Sku: string
          Name: string
          Filters: Set<string>
          Directory: string }
    type Product =
        | Playable of ProductDetails
        | RequiresUpdate of ProductDetails
        | Missing of MissingProductDetails
        | Unknown of name:string
