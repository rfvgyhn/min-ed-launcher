module MinEdLauncher.Epic

open FsToolkit.ErrorHandling
open Rop
open MinEdLauncher.Token
open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks
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
        
let parseJson element =
    let accessToken = element >>= Json.parseProp "access_token" >>= Json.toString
    let expiresIn = element >>= Json.parseProp "expires_in" >>= Json.toInt
    let refreshToken = element >>= Json.parseProp "refresh_token" >>= Json.toString
    
    match accessToken, expiresIn, refreshToken with
    | Ok accessToken, Ok expiresIn, Ok refreshToken ->
        Ok { Token = accessToken
             TokenInterval = expiresIn - 60
             RefreshToken = refreshToken }
    | _ ->
      $"Unexpected json object %s{element.ToString()}" |> Error

let private epicValues = lazy (
    let err msg = Error $"Couldn't extract Epic credentials - {msg}"
    try
        let eosIfAsm = Assembly.LoadFrom("EosIF.dll")
        let eosIfType = eosIfAsm.GetType("EosIF.EosInterface")
        let eosAsm = Assembly.LoadFrom("EosSdk.dll")
        let credType = eosAsm.GetType("Epic.OnlineServices.Platform.ClientCredentials")
        let optType = eosAsm.GetType("Epic.OnlineServices.Platform.Options")
        
        if eosIfType <> null && credType <> null && optType <> null then
            let methodInfo = eosIfType.GetMethod("CreatePlatformOptions", BindingFlags.Instance ||| BindingFlags.NonPublic)
            let credIdProp = credType.GetProperty("ClientId")
            let credSecretProp = credType.GetProperty("ClientSecret")
            let depIdProp = optType.GetProperty("DeploymentId")
            let sandboxIdProp = optType.GetProperty("SandboxId")
            let credsProp = optType.GetProperty("ClientCredentials")
            
            if methodInfo <> null && credIdProp <> null && credSecretProp <> null && depIdProp <> null && sandboxIdProp <> null && credsProp <> null then
                let eosIf = Activator.CreateInstance(eosIfType, null)
                let options = methodInfo.Invoke(eosIf, null)
                
                if options <> null then
                    let credentials = credsProp.GetValue(options)
                    if credentials <> null then
                        let depId = depIdProp.GetValue(options) :?> string
                        let sandboxId = sandboxIdProp.GetValue(options) :?> string
                        let cId = credIdProp.GetValue(credentials) :?> string
                        let cSecret = credSecretProp.GetValue(credentials) :?> string
                        
                        if depId <> null && sandboxId <> null && cId <> null && cSecret <> null then
                            Ok (cId, cSecret, depId, sandboxId)
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
    with e -> err (e.ToString()))

let private requestToStr formValues contentHeaders (request: HttpRequestMessage) =
    let sb = StringBuilder()
    sb.AppendLine()
      .Append("  Method: ")
      .AppendLine(request.Method.ToString())
      .Append("  Uri: ")
      .AppendLine(request.RequestUri.ToString())
      .Append("  Content: ")
    |> List.dump formValues 2
    sb.Append("  Headers: ")
    |> Http.dumpHeaders [ request.Headers; contentHeaders ] 2
    sb.ToString()

let private requestToken launcherVersion (formValues: string list) : Task<Result<RefreshableToken, string>> =
    match epicValues.Force() with
    | Ok (clientId, clientSecret, depId, _) -> task {
        let formValues =
            [ $"deployment_id={depId}"
              "scope=basic_profile friends_list presence" ]
            |> List.append formValues
        use content = new StringContent(String.Join("&", formValues), Encoding.UTF8, "application/x-www-form-urlencoded")
        
        let authHeaderValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"%s{clientId}:%s{clientSecret}"))
        use request = new HttpRequestMessage()
        request.Headers.Authorization <- AuthenticationHeaderValue("Basic", authHeaderValue)
        request.Method <- HttpMethod.Post
        request.RequestUri <- Uri("https://api.epicgames.dev/epic/oauth/v1/token")
        request.Content <- content
        
        Log.debug $"Requesting epic token %s{requestToStr formValues content.Headers request}"
        use httpClient = Http.createClient launcherVersion
        let! response = httpClient.SendAsync(request)
        
        if response.IsSuccessStatusCode then
            Log.debug "Requesting epic token success"
            use! content = response.Content.ReadAsStreamAsync()
            return content |> Json.parseStream >>= Json.rootElement |> parseJson
        else
            let! content = response.Content.ReadAsStringAsync()
            Log.debug $"Requesting epic token failed: %s{content}"
            return $"%i{int response.StatusCode}: %s{response.ReasonPhrase}" |> Error }
    | Error msg -> Error msg |> Task.fromResult

let private requestAsEpic path applyOptions = task {
    use httpClient = new HttpClient()
    use request = new HttpRequestMessage()
    
    applyOptions request
    
    request.RequestUri <- Uri($"https://account-public-service-prod03.ol.epicgames.com%s{path}")
    request.Headers.TryAddWithoutValidation("User-Agent", "UELauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit") |> ignore
    
    return! httpClient.SendAsync(request)
}

let private generateExchangeCode token = task {
    let! response = requestAsEpic "/account/api/oauth/exchange" (fun r -> r.Headers.Authorization <- AuthenticationHeaderValue("bearer", token))
    
    if response.IsSuccessStatusCode then
        Log.debug "Requesting epic exchange code success"
        use! content = response.Content.ReadAsStreamAsync()
        return content |> Json.parseStream >>= Json.rootElement >>= Json.parseProp "code" >>= Json.toString
    else
        let! content = response.Content.ReadAsStringAsync()
        Log.debug $"Requesting epic exchange code failed: %s{content}"
        return $"%i{int response.StatusCode}: %s{response.ReasonPhrase}" |> Error }

let getDeploymentId() =
    epicValues.Force()
    |> Result.map (fun (_, _, depId, _) -> depId)
    
let getSandboxId() =
    epicValues.Force()
    |> Result.map (fun (_, _, _, sandboxId) -> sandboxId)

let loginWithCode launcherVersion exchangeCode =             
    requestToken launcherVersion [ "grant_type=exchange_code"; $"exchange_code=%s{exchangeCode}" ]
    
let refreshToken launcherVersion (token: RefreshableToken) =
    requestToken launcherVersion [ "grant_type=refresh_token"; $"refresh_token=%s{token.RefreshToken}" ]

let loginWithExistingToken launcherVersion token =
    generateExchangeCode token
    |> TaskResult.bind(loginWithCode launcherVersion) 
