module Types

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
type ProductMode = Vr | Pancake
type AutoRun = bool
type AutoQuit = bool
type WatchForCrashes = bool
type RemoteLogging = bool
type LauncherSettings =
    { Platform: Platform
      ProductMode: ProductMode
      AutoRun: AutoRun
      AutoQuit: AutoQuit
      WatchForCrashes: WatchForCrashes
      RemoteLogging: RemoteLogging }
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
    { Name: string }
