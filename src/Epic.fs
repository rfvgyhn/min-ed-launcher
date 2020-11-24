namespace EdLauncher

module Epic =
    open Types
    open Rop
    open FSharp.Control.Tasks.NonAffine
    open System
    open System.IO
    open System.Net.Http
    open System.Net.Http.Headers
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

        member this.Login epicDetails = task {
            let lines = System.IO.File.ReadAllLines("epic-sdk-details.txt")     // TODO: decide which values should be used
            let clientId = lines.[0]
            let clientSecret = lines.[1]
            let dId = lines.[4]
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
                return $"%i{int response.StatusCode}: %s{response.ReasonPhrase}" |> Error
        }          
        
        interface IDisposable with
            member this.Dispose() =
                cleanup true
                GC.SuppressFinalize(this)
                
        override this.Finalize() =
            cleanup false
