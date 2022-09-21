module MinEdLauncher.Types

open System
open System.Collections.Generic
open FSharp.Data
open System.Diagnostics
open Token

type OrdinalIgnoreCaseComparer() = 
    interface IComparer<string> with
        member x.Compare(a, b) = StringComparer.OrdinalIgnoreCase.Compare(a, b)

type OrdinalIgnoreCaseSet = FSharpx.Collections.Tagged.Set<string, OrdinalIgnoreCaseComparer>
[<RequireQualifiedAccess>]
module OrdinalIgnoreCaseSet =
    let intersect set2 set1 = OrdinalIgnoreCaseSet.Intersection(set1, set2)
    let any (set: OrdinalIgnoreCaseSet) = set.Count > 0
    let ofSeq (items: string[]) = OrdinalIgnoreCaseSet.Create(OrdinalIgnoreCaseComparer(), items)
    let empty = OrdinalIgnoreCaseSet.Empty(OrdinalIgnoreCaseComparer())
    
type OrdinalIgnoreCaseMap<'Value> = FSharpx.Collections.Tagged.Map<string, 'Value, OrdinalIgnoreCaseComparer>
[<RequireQualifiedAccess>]
module OrdinalIgnoreCaseMap =
    let ofSeq<'Value> items = OrdinalIgnoreCaseMap<'Value>.Create(OrdinalIgnoreCaseComparer(), items)
    let empty<'Value> = OrdinalIgnoreCaseMap<'Value>.Empty(OrdinalIgnoreCaseComparer())

type EpicDetails =
    { ExchangeCode: string
      Type: string
      AppId: string }
    with static member Empty = { ExchangeCode = ""
                                 Type = ""
                                 AppId = "" }
type Credentials = { Username: string; Password: string }
type FrontierDetails =
    { Profile: string; Credentials: Credentials option; AuthToken: string option }
    with static member Empty = { Profile = ""; Credentials = None; AuthToken = None }
type Platform =
    | Steam
    | Epic of EpicDetails
    | Frontier of FrontierDetails
    | Oculus of string
    | Dev
    with member this.Name = Union.getCaseName this
type CompatTool = { EntryPoint: string; Args: string array }
type DisplayMode = Vr | Pancake
type AutoRun = bool
type AutoQuit = bool
type WatchForCrashes = bool
type ForceLocal = bool
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
      DirectoryName: string
      ServerArgs: string
      GameArgs: string
      SortKey: int
      Sku: string
      TestApi: bool }
type LauncherSettings =
    { Platform: Platform
      DisplayMode: DisplayMode
      AutoRun: AutoRun
      AutoQuit: AutoQuit
      WatchForCrashes: WatchForCrashes
      ProductWhitelist: OrdinalIgnoreCaseSet
      ForceLocal: ForceLocal
      CompatTool: CompatTool option
      CbLauncherDir: string
      PreferredLanguage: string option
      ApiUri: Uri
      Restart: int64 option
      AutoUpdate: bool
      MaxConcurrentDownloads: int
      ForceUpdate: string Set
      Processes: ProcessStartInfo list
      FilterOverrides: OrdinalIgnoreCaseMap<string>
      AdditionalProducts: AuthorizedProduct list }
type ProductMode = Online | Offline
type VersionInfo =
    { Name: string
      Executable: string
      UseWatchDog64: bool
      SteamAware: bool
      Version: Version
      Mode: ProductMode }
type ProductMetadata =
    { Hash: string
      LocalFile: string
      RemotePath: Uri
      Size: int64
      Version: Version }
type ProductDetails =
    { Sku: string
      Name: string
      Filters: OrdinalIgnoreCaseSet
      Executable: string
      UseWatchDog64: bool
      SteamAware: bool
      Version: Version
      Mode: ProductMode
      Directory: string
      GameArgs: string
      ServerArgs: string
      SortKey: int
      Metadata: ProductMetadata option }
type MissingProductDetails =
    { Sku: string
      Name: string
      Filters: OrdinalIgnoreCaseSet
      Directory: string }
type ProductManifest = XmlProvider<"""<Manifest title="Win64_4_0_0_10_Alpha" version="2021.04.09.263090"><File><Path>AppConfig.xml</Path><Hash>b73379436461d1596b39f6aa07dd6d83724cca6d</Hash><Size>3366</Size><Download>http://path.to/file</Download></File><File><Path>AudioConfiguration.xml</Path><Hash>ad79d0c6ca5988175b45c929ec039e86cd6967f3</Hash><Size>2233</Size><Download>http://path.to/file2</Download></File></Manifest>""">
type Product =
    | Playable of ProductDetails
    | RequiresUpdate of ProductDetails
    | Missing of MissingProductDetails
    | Unknown of name:string
