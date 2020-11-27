namespace EdLauncher

module Epic =
    open Types
    open Rop
    open FSharp.Control.Tasks.NonAffine
    open System
    open System.IO
    open System.Net.Http
    open System.Net.Http.Headers
    open System.Reflection
    open System.Text
    
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
        
    type EpicUser =
        { AccessToken: string
          TokenExpires: DateTime
          RefreshToken: string
          RefreshTokenExpires: DateTime }
        with member this.ToAuthToken() = { Token = this.AccessToken; TokenExpiry = this.TokenExpires; RefreshToken = this.RefreshToken; RefreshTokenExpiry = this.RefreshTokenExpires } |> Expires
        
    type Epic() =
        let mutable disposed = false
        let httpClient = new HttpClient()
        
        let cleanup disposing =
            if not disposed then
                Log.debug "Disposing Epic resources"
                disposed <- true
                
                httpClient.Dispose()
                
        let parseJson element =
            let accessToken = element >>= Json.parseProp "access_token" >>= Json.toString
            let refreshToken = element >>= Json.parseProp "refresh_token" >>= Json.toString
            let accessExpiry = element >>= Json.parseProp "expires_at" >>= Json.asDateTime
            let refreshExpiry = element >>= Json.parseProp "refresh_expires_at" >>= Json.asDateTime
            
            match accessToken, refreshToken, accessExpiry, refreshExpiry with
            | Ok accessToken, Ok refreshToken, Ok accessExpiry, Ok refreshExpiry ->
                Ok { AccessToken = accessToken
                     TokenExpires = accessExpiry.ToUniversalTime()
                     RefreshToken = refreshToken
                     RefreshTokenExpires = refreshExpiry.ToUniversalTime() }
            | _ ->
              $"Unexpected json object %s{element.ToString()}" |> Error

        let extractEpicValues() =
            let err msg = Error $"Couldn't extract Epic credentials - {msg}"
            try
                let eosIfType = Assembly.LoadFrom("EosIF.dll").GetType("EosIF.EosInterface")
                let eos = Assembly.LoadFrom("EosSdk.dll")
                let credType = eos.GetType("Epic.OnlineServices.Platform.ClientCredentials")
                let optType = eos.GetType("Epic.OnlineServices.Platform.Options")
                
                if eosIfType <> null && credType <> null && optType <> null then
                    let methodInfo = eosIfType.GetMethod("CreatePlatformOptions", BindingFlags.Instance ||| BindingFlags.NonPublic)
                    let credIdProp = credType.GetProperty("ClientId")
                    let credSecretProp = credType.GetProperty("ClientSecret")
                    let depIdProp = optType.GetProperty("DeploymentId")
                    let credsProp = optType.GetProperty("ClientCredentials")
                    
                    if methodInfo <> null && credIdProp <> null && credSecretProp <> null && depIdProp <> null && credsProp <> null then
                        let eosIf = Activator.CreateInstance(eosIfType, null)
                        let options = methodInfo.Invoke(eosIf, null)
                        
                        if options <> null then
                            let credentials = credsProp.GetValue(options)
                            if credentials <> null then
                                let depId = depIdProp.GetValue(options) :?> string
                                let cId = credIdProp.GetValue(credentials) :?> string
                                let cSecret = credSecretProp.GetValue(credentials) :?> string
                                
                                if depId <> null && cId <> null && cSecret <> null then
                                    Ok (cId, cSecret, depId)
                                else
                                    err "Unable to get values of IDs"
                            else
                                err "Unable to get value of credentials"
                        else
                            err "Unable to call method CreatePlatformOptions"
                    else
                        err "Unable to reflect method/props for Epic IDs"
                else
                    err "Unable to reflect types for Epic IDs"
            with e -> err (e.ToString())

        member this.Login epicDetails =
            match extractEpicValues() with
            | Ok (clientId, clientSecret, dId) -> task {
                use content = new StringContent(String.Join("&", [
                    "grant_type=exchange_code"
                    $"deployment_id={dId}"
                    "scope=basic_profile friends_list presence"
                    $"exchange_code={epicDetails.ExchangeCode}"
                ]), Encoding.UTF8, "application/x-www-form-urlencoded")
                
                let authHeaderValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"%s{clientId}:%s{clientSecret}"))
                use request = new HttpRequestMessage()
                request.Headers.Authorization <- AuthenticationHeaderValue("Basic", authHeaderValue)
                request.Method <- HttpMethod.Post
                request.RequestUri <- Uri("https://api.epicgames.dev/epic/oauth/v1/token")
                request.Content <- content
                
                let! response = httpClient.SendAsync(request)
                
                if response.IsSuccessStatusCode then
                    use! content = response.Content.ReadAsStreamAsync()
                    return content |> Json.parseStream >>= Json.rootElement |> parseJson
                else
                    return $"%i{int response.StatusCode}: %s{response.ReasonPhrase}" |> Error }
            | Error msg -> Error msg |> Task.fromResult
        
        interface IDisposable with
            member this.Dispose() =
                cleanup true
                GC.SuppressFinalize(this)
                
        override this.Finalize() =
            cleanup false
