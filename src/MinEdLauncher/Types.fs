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
    let any (set: OrdinalIgnoreCaseSet) = not set.IsEmpty
    let ofSeq (items: string[]) = OrdinalIgnoreCaseSet.Create(OrdinalIgnoreCaseComparer(), items)
    let empty = OrdinalIgnoreCaseSet.Empty(OrdinalIgnoreCaseComparer())
    
type OrdinalIgnoreCaseMap<'Value> = FSharpx.Collections.Tagged.Map<string, 'Value, OrdinalIgnoreCaseComparer>
[<RequireQualifiedAccess>]
module OrdinalIgnoreCaseMap =
    let ofSeq<'Value> items = OrdinalIgnoreCaseMap<'Value>.Create(OrdinalIgnoreCaseComparer(), items)
    let empty<'Value> = OrdinalIgnoreCaseMap<'Value>.Empty(OrdinalIgnoreCaseComparer())

type ISampledProgress<'T> =
    inherit IProgress<'T>
    abstract member Flush : unit -> unit

type SampledProgress<'T>(interval: TimeSpan, handler: 'T -> unit, finishedHandler: unit -> unit) =
    let stopwatch = Stopwatch()
    let mutable lastReport = 0L
    let mutable current : 'T option = None
    let report value = lock stopwatch (fun () ->
        if not stopwatch.IsRunning then stopwatch.Start()
        if lastReport = 0 || stopwatch.ElapsedMilliseconds - lastReport >= interval.Milliseconds then
            lastReport <- stopwatch.ElapsedMilliseconds
            handler value
        current <- Some value)
    
    interface IProgress<'T> with
        member this.Report(value: 'T) = report value
        
    interface ISampledProgress<'T> with
        member this.Flush() = lock stopwatch (fun () ->
            match current with
            | None -> ()
            | Some value ->
                lastReport <- 0
                report value
                finishedHandler())

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
type QuitMode = Immediate | WaitForExit | WaitForInput
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
      QuitMode: QuitMode
      WatchForCrashes: WatchForCrashes
      ProductWhitelist: OrdinalIgnoreCaseSet
      SkipInstallPrompt: bool
      ForceLocal: ForceLocal
      CompatTool: CompatTool option
      CbLauncherDir: string
      PreferredLanguage: string option
      ApiUri: Uri
      Restart: int64 option
      AutoUpdate: bool
      CheckForLauncherUpdates: bool
      MaxConcurrentDownloads: int
      ForceUpdate: string Set
      Processes: {| Info: ProcessStartInfo; RestartOnRelaunch: bool; KeepOpen: bool |} list
      ShutdownProcesses: ProcessStartInfo list
      FilterOverrides: OrdinalIgnoreCaseMap<string>
      AdditionalProducts: AuthorizedProduct list
      DryRun: bool
      ShutdownTimeout: TimeSpan
      CacheDir: string
      GameStartDelay: TimeSpan
      ShutdownDelay: TimeSpan }
type ProductMode = Online | Offline
type VersionInfo =
    { Name: string
      Executable: string
      UseWatchDog64: bool
      SteamAware: bool
      Version: Version
      Mode: ProductMode }
    with static member Empty = { Name = ""; Executable = ""; UseWatchDog64 = false; SteamAware = false; Version = Version.Parse("0.0.0"); Mode = ProductMode.Offline }
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
      VInfo: VersionInfo
      Directory: string
      GameArgs: string
      ServerArgs: string
      SortKey: int
      Metadata: ProductMetadata option }
type ProductManifest = XmlProvider<"""<Manifest title="Win64_4_0_0_10_Alpha" version="2021.04.09.263090"><File><Path>AppConfig.xml</Path><Hash>b73379436461d1596b39f6aa07dd6d83724cca6d</Hash><Size>3366</Size><Download>http://path.to/file</Download></File><File><Path>AudioConfiguration.xml</Path><Hash>ad79d0c6ca5988175b45c929ec039e86cd6967f3</Hash><Size>2233</Size><Download>http://path.to/file2</Download></File></Manifest>""">
type Product =
    | Playable of ProductDetails
    | RequiresUpdate of ProductDetails
    | Missing of ProductDetails
    | Unknown of name:string
