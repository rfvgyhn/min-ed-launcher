module MinEdLauncher.Types

open System
open System.Diagnostics
open Token

type EpicDetails =
    { ExchangeCode: string
      Type: string
      AppId: string }
    with static member Empty = { ExchangeCode = ""
                                 Type = ""
                                 AppId = "" }
type Platform =
    | Steam
    | Epic of EpicDetails
    | Frontier
    | Oculus of string
    | Dev
    with member this.Name = Union.getCaseName this
type DisplayMode = Vr | Pancake
type AutoRun = bool
type AutoQuit = bool
type WatchForCrashes = bool
type ForceLocal = bool
type LauncherSettings =
    { Platform: Platform
      DisplayMode: DisplayMode
      AutoRun: AutoRun
      AutoQuit: AutoQuit
      WatchForCrashes: WatchForCrashes
      ProductWhitelist: Set<string>
      ForceLocal: ForceLocal
      Proton: (string * string) option
      CbLauncherDir: string
      ApiUri: Uri
      Restart: (bool * int64)
      Processes: ProcessStartInfo list }
type ServerStatus = Healthy
type LocalVersion = Version
type LauncherStatus =
    | Current
    | Supported
    | Expired
    | Future
type ServerInfo =
    { Status: ServerStatus}
type EdSession =
    { Token: string
      PlatformToken: AuthToken
      MachineToken: string
      Name: string }
    with static member Empty = { Token = ""; PlatformToken = Permanent ""; Name = ""; MachineToken = "" }
type User =
    { Name: string
      EmailAddress: string option
      Session: EdSession
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
