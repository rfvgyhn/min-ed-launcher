namespace EdLauncher

open System.IO



module Epic =
    open Types
    open Rop
    open System
    open System.Threading.Tasks
    open Epic.OnlineServices
    open Epic.OnlineServices.Auth
    open Epic.OnlineServices.Logging
    open Epic.OnlineServices.Platform
    
    let potentialInstallPaths appId =
        let progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        Path.Combine(progData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat")
        |> Json.parseFile
        >>= Json.rootElement
        >>= Json.parseProp "InstallationList"
        >>= Json.arrayTryFind (fun e ->
            let appName = e |> Json.parseProp "AppName" >>= Json.toString |> Result.defaultValue ""
            appName = appId)
        |> Result.map (fun element ->
            element
            |> Option.bind (fun element ->
                element |> Json.parseProp "InstallLocation" >>= Json.toString
                |> function
                    | Ok path -> Some path
                    | Error msg ->
                        Log.error $"Unable to parse 'InstallLocation': {msg}"
                        None
                )
            |> Option.defaultValue "")
        |> function
            | Error msg -> []
            | Ok dir -> [ dir ]
        
    
    let getLoginMethod (epicDetails: EpicDetails) =
        epicDetails.RefreshToken
        |> Option.map (fun token -> LoginCredentialType.RefreshToken, token)
        |> Option.defaultValue (LoginCredentialType.ExchangeCode, epicDetails.ExchangeCode)
    
    let getLogger = function
        | LogLevel.Off -> (fun _ -> ())
        | LogLevel.Fatal -> Log.error
        | LogLevel.Error -> Log.error
        | LogLevel.Warning -> Log.warn
        | LogLevel.Info -> Log.info
        | LogLevel.Verbose -> Log.debug
        | LogLevel.VeryVerbose -> Log.debug
        | _ -> Log.debug
    
    // TODO: decide which values should be used
    let getPlatformOptions() =
        let lines = System.IO.File.ReadAllLines("epic-sdk-details.txt")
        let credentials = ClientCredentials()
        credentials.ClientId <- lines.[0]
        credentials.ClientSecret <- lines.[1]
        let platformOptions = Options()
        platformOptions.ProductId <- lines.[2]
        platformOptions.SandboxId <- lines.[3]
        platformOptions.ClientCredentials <- credentials
        platformOptions.Flags <- PlatformFlags.DisableOverlay
        platformOptions.DeploymentId <- lines.[4]
        
        platformOptions
    
    let private configureLogging() =
        LoggingInterface.SetLogLevel(LogCategory.AllCategories, LogLevel.VeryVerbose) |> ignore
        LoggingInterface.SetCallback(fun m -> m.Level |> getLogger <| $"[EOS-SDK] [{m.Category}] {m.Message}") |> ignore
        
    let private getAuthInterface() =        
        Log.debug "Initializing Epic Online Services"
        let options = InitializeOptions()
        options.ProductName <- "Callisto";
        options.ProductVersion <- "1.0";
        let result = PlatformInterface.Initialize(options)
        
        if result <> Result.Success then
            Error $"Unable to initialize Epic Online Services %A{result}"
        else
            configureLogging()
            let platform = PlatformInterface.Create(getPlatformOptions())
            if platform = null then
                Error $"Failed to create platform. Ensure the relevant {typedefof<Options>} are set or passed into the application as arguments."
            else
                Ok (platform.GetAuthInterface())
      
    type EpicUser =
        { AccessToken: string
          TokenExpires: DateTime
          RefreshToken: string
          RefreshTokenExpires: DateTime }
        with member this.ToAuthToken() = { Token = this.AccessToken; TokenExpiry = this.TokenExpires; RefreshToken = this.RefreshToken; RefreshTokenExpiry = this.RefreshTokenExpires } |> Expires
        
    let private loginCallback (authInterface: AuthInterface) (tcs: TaskCompletionSource<Result<EpicUser, string>>) (info: LoginCallbackInfo) =
        if info.ResultCode = Result.Success then                        
            let user =
                let tokenResult, authToken = authInterface.CopyUserAuthToken(CopyUserAuthTokenOptions(), info.LocalUserId)
                if tokenResult = Result.Success then
                    Log.debug $"Got Epic auth token %A{authToken}"
                    Ok { AccessToken = authToken.AccessToken
                         TokenExpires = DateTime.Parse(authToken.RefreshExpiresAt).ToUniversalTime()
                         RefreshToken = authToken.RefreshToken
                         RefreshTokenExpires = DateTime.Parse(authToken.ExpiresAt).ToUniversalTime() }
                else
                    Error $"Unable to copy user auth token %A{tokenResult}"
            tcs.SetResult(user)
        else if (Helper.IsOperationComplete(info.ResultCode)) then
            tcs.SetResult(Error $"Unable to login %A{info.ResultCode}")
        else
            ()
        
    let login (epicDetails: EpicDetails) =
        let tcs = TaskCompletionSource<Result<EpicUser, string>>()
        
        match getAuthInterface() with
        | Ok authInterface ->
            let method, token = getLoginMethod epicDetails
            let credentials = Credentials()
            credentials.Type <- method
            credentials.Token <- token
            let loginOptions = LoginOptions()
            loginOptions.Credentials <- credentials
            loginOptions.ScopeFlags <- AuthScopeFlags.BasicProfile ||| AuthScopeFlags.FriendsList ||| AuthScopeFlags.Presence
            authInterface.Login(loginOptions, null, (fun i -> loginCallback authInterface tcs i))
        | Error m -> tcs.SetResult(Error m)
        
        tcs.Task |> Async.AwaitTask
