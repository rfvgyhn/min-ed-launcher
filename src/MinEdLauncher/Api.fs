module MinEdLauncher.Api

open System
open System.IO.Compression
open System.Net
open System.Net.Http
open System.Text
open System.Threading.Tasks
open FsToolkit.ErrorHandling
open MinEdLauncher.Token
open Types
open Rop

type Connection(httpClient: HttpClient, session: EdSession, runningTime: unit -> double) =
    let mutable disposableResource : IDisposable option = None
    
    member this.HttpClient = httpClient
    member this.Session = session
    member this.RunningTime = runningTime
    member this.WithResource resource =
        disposableResource <- Some resource
        this
    
    interface IDisposable with
        member this.Dispose() =
            disposableResource
            |> Option.iter (fun d -> d.Dispose())

type AuthResult =
| Authorized of Connection
| RegistrationRequired of Uri
| LinkAvailable of Uri
| Denied of string
| Failed of string

let private buildUri (host: Uri) (path: string) queryParams =
    let builder = UriBuilder(host)
    builder.Path <- path
    builder.Uri |> Uri.addQueryParams queryParams

let private buildRequest (path: string) platform (connection: Connection) queryParams =
    let request = new HttpRequestMessage()
    request.RequestUri <- buildUri connection.HttpClient.BaseAddress path queryParams
    match platform with
    | Steam -> Some $"bearer %s{connection.Session.Token}"
    | Epic _ -> Some $"epic %s{connection.Session.PlatformToken.GetAccessToken()}"
    | _ -> None
    |> Option.iter (fun header -> request.Headers.Add("Authorization", header))

    request
    
let private postSingle (httpClient: HttpClient) path successProp errorProp queryParams = task {
    use request = new HttpRequestMessage()
    request.RequestUri <- buildUri httpClient.BaseAddress path queryParams
    request.Method <- HttpMethod.Post
    
    use! response = httpClient.SendAsync(request)
    use! content = response.Content.ReadAsStreamAsync()
    let content = content |> Json.parseStream >>= Json.rootElement
    
    let result =
        if response.IsSuccessStatusCode then
            content >>= Json.parseProp successProp >>= Json.toString
        else
            let code = content >>= Json.parseProp errorProp >>= Json.toString |> Result.defaultValue "Unknown"
            let message = content >>= Json.parseProp "message" >>= Json.toString |> Result.defaultValue "Unknown"
            Error $"%i{int response.StatusCode}: %s{message} - ErrorCode = %s{code}"
    
    return result }

let private fetch (httpClient: HttpClient) requestMessage = task {
    use! response = httpClient.SendAsync(requestMessage)
    if response.IsSuccessStatusCode then
        use! content = response.Content.ReadAsStreamAsync()
        return content |> Json.parseStream >>= Json.rootElement
    else
        return Error $"%i{int response.StatusCode}: %s{response.ReasonPhrase}"
}

let createClient apiUri version =
    let httpClient = Http.createClient version
    httpClient.BaseAddress <- apiUri
    httpClient.DefaultRequestHeaders.ConnectionClose <- Nullable<bool>(false)
    httpClient

let getTime (now:DateTime) (httpClient: HttpClient) = task {
    let localTimestamp = (int64)(now.Subtract(DateTime(1970, 1, 1))).TotalSeconds
    use! response = httpClient.GetAsync("/1.1/server/time")
    if response.IsSuccessStatusCode then
        use! content = response.Content.ReadAsStreamAsync()
        return content |> Json.parseStream
                       >>= Json.rootElement
                       >>= Json.parseProp "unixTimestamp"
                       >>= Json.asInt64
                       |> Result.mapError (fun msg -> (localTimestamp, msg))
    else
        return Error (localTimestamp, $"%i{int response.StatusCode}: %s{response.ReasonPhrase}")
}

let firstTimeSignin (runningTime: unit -> double) (httpClient:HttpClient) details machineId lang =
    Cobra.decrypt details.Password
    |> Result.map Encoding.UTF8.GetBytes
    |> Result.map Hex.toString
    |> Result.bindTask (fun password ->
        [ "email", details.Username |> Encoding.UTF8.GetBytes |> Hex.toString
          "password", password
          "machineId", machineId
          "lang", lang
          "fTime", runningTime().ToString() ]
        |> postSingle httpClient "/1.1/user/auth" "encCode" "errorCode")

let requestMachineToken (httpClient:HttpClient) machineId lang twoFactorToken twoFactorCode =
    [ "machineId", machineId
      "plainCode", twoFactorCode
      "encCode", twoFactorToken
      "lang", lang ]
    |> postSingle httpClient "/1.1/user/token" "machineToken" "error_num"

type LoginRequest =
    { RunningTime: unit -> double
      HttpClient: HttpClient
      Details: FrontierDetails
      MachineId: string
      Lang: string
      SaveCredentials: Credentials -> string option -> Task<Result<unit, string>>
      GetTwoFactor: string -> string
      GetUserPass: unit -> string * string }
let rec login request =
    match request.Details.Credentials, request.Details.AuthToken with
    | None, _ ->
        let user, pass = request.GetUserPass()
        login { request with Details = { request.Details with Credentials = Some { Username = user; Password = pass } } }
    | Some cred, None ->
        firstTimeSignin request.RunningTime request.HttpClient cred request.MachineId request.Lang
        |> TaskResult.bind (fun twoFactorToken ->
            request.GetTwoFactor cred.Username |> requestMachineToken request.HttpClient request.MachineId request.Lang twoFactorToken)
        |> TaskResult.bind (fun machineToken ->
            request.SaveCredentials cred (Some machineToken) |> TaskResult.bind (fun () -> Ok machineToken |> Task.fromResult))
        |> TaskResult.map (fun token -> (cred.Username, cred.Password, token))
    | Some cred, Some authToken -> (cred.Username, cred.Password, authToken) |> Ok |> Task.fromResult

let authenticate (runningTime: unit -> double) (token: AuthToken) platform machineId lang (httpClient:HttpClient) = task {
    let info =
        let queryParams other =
            [ "machineId", machineId
              "fTime", runningTime().ToString() ] |> List.append other
        let parseMachineToken content = content >>= Json.parseProp "machineToken" >>= Json.toString 
        match platform with
        | Steam -> Ok ("/3.0/user/steam/auth", queryParams [ "steamTicket", token.GetAccessToken() ], parseMachineToken)
        | Epic _ -> Ok ("/3.0/user/forctoken", queryParams [], parseMachineToken)
        | Frontier _ ->
            let username, pass, token =
                match token with
                | PasswordBased t -> t.Username, t.Password, t.Token
                | _ -> raise (InvalidOperationException())
            Cobra.decrypt pass
            |> Result.map Encoding.UTF8.GetBytes
            |> Result.map Hex.toString
            |> Result.map (fun password ->
                "/1.1/user/auth",
                queryParams [ "email", Encoding.UTF8.GetBytes(username) |> Hex.toString
                              "password", password
                              "machineId", machineId
                              "machineToken", token
                              "lang", lang
                              "fTime", runningTime().ToString() ],
                fun _ -> Ok token)
        | _ -> raise (NotImplementedException())
    match info with
    | Error m -> return Failed m
    | Ok (path, query, parseMachineToken) ->
        use request = new HttpRequestMessage()
        request.RequestUri <- buildUri httpClient.BaseAddress path query
        match platform with
        | Epic _ -> request.Headers.Add("Authorization", $"epic %s{token.GetAccessToken()}")
        | Frontier _ -> request.Method <- HttpMethod.Post
        | _ -> ()
        
        use! response = httpClient.SendAsync(request)
        use! content = response.Content.ReadAsStreamAsync()
        let content = content |> Json.parseStream >>= Json.rootElement
        let mapResult f = function
            | Ok value -> f value
            | Error msg -> Failed msg
        let parseError content =
            let errorValue = content >>= Json.parseEitherProp "error_enum" "errorCode" >>= Json.toString |> Result.defaultValue "Unknown"
            let errorMessage = content >>= Json.parseProp "message" >>= Json.toString |> Result.defaultValue ""
            errorValue, errorMessage
        
        return
            match response.StatusCode with
            | code when int code < 300 ->
                let fdevAuthToken = content >>= Json.parseProp "authToken" >>= Json.toString
                let machineToken = parseMachineToken content
                let registeredName = content >>= Json.parseProp "registeredName"
                                         >>= Json.toString
                                         |> Result.defaultValue $"%s{platform.Name} User"
                let errorValue = content >>= Json.parseEitherProp "error_enum" "errorCode" >>= Json.toString
                let errorMessage = content >>= Json.parseProp "message" >>= Json.toString
                
                match fdevAuthToken, machineToken, errorValue, errorMessage with
                | Error _, Error _, Ok value, Ok msg -> Failed $"%s{value} - %s{msg}"
                | Ok fdevToken, Ok machineToken, _, _ ->
                    let session = { Token = fdevToken; PlatformToken = token; Name = registeredName; MachineToken = machineToken }
                    Authorized <| new Connection(httpClient, session, runningTime)
                | Error msg, _, _, _
                | _, Error msg, _, _ -> Failed msg
            | HttpStatusCode.Found ->
                content >>= Json.parseProp "Location"
                        >>= Json.asUri
                        |> mapResult RegistrationRequired
            | HttpStatusCode.TemporaryRedirect ->
                content >>= Json.parseProp "Location"
                        >>= Json.asUri
                        |> mapResult LinkAvailable
            | HttpStatusCode.BadRequest ->
                let errValue, errMessage = content |> parseError
                $"Bad Request: %s{errValue} - %s{errMessage}" |> Denied
            | HttpStatusCode.Forbidden ->
                let errValue, errMessage = content |> parseError
                $"Forbidden: %s{errValue} - %s{errMessage}" |> Denied
            | HttpStatusCode.Unauthorized ->
                let errValue, errMessage = content |> parseError
                $"Unauthorized: %s{errValue} - %s{errMessage}" |> Denied
            | HttpStatusCode.ServiceUnavailable ->
                Failed "Service unavailable"
            | code ->
                Failed $"%i{int code}: %s{response.ReasonPhrase}"
}

let getAuthorizedProducts platform lang (connection: Connection) = task {
    use request =
        match platform with
        | Frontier _ -> [ "authToken", connection.Session.Token; "machineToken", connection.Session.MachineToken ]
        | Steam | Epic _ -> []
        | p ->
            Log.error $"Attempting to get projects for unsupported platform {p.Name}"
            []
        |> List.append [ if Option.isSome lang then "lang", Option.get lang
                         "fTime", connection.RunningTime().ToString() ]
        |> buildRequest "/3.0/user/purchases" platform connection
        
    let! content = fetch connection.HttpClient request
    return content
       >>= ((fun json -> Log.debug $"Purchases Response:{Environment.NewLine}%s{json.ToString()}"; json)
            >> Json.parseProp "purchases")
       >>= Json.mapArray AuthorizedProduct.fromJson
       |> Result.map (Seq.chooseResultDoErr Log.debug)
       |> Result.map (fun products -> products |> Seq.sortBy (fun p -> p.SortKey) |> List.ofSeq)
}

let getProductManifest (httpClient: HttpClient) (uri: Uri) = task {
    try
        use! responseStream = httpClient.GetStreamAsync(uri)
        use decompStream = new GZipStream(responseStream, CompressionMode.Decompress)
        return ProductManifest.Load(decompStream) |> Ok
    with e -> return e.ToString() |> Error
}

let checkForUpdate platform machineId (connection: Connection) product = task {
    match product with
    | Unknown name -> return Error $"{name}: Can't check updates for unknown product"
    | Missing p -> return Error $"{p.Name}: Can't check updates for missing product"
    | RequiresUpdate product
    | Playable product ->
        let queryParams = [
            "machineToken", connection.Session.MachineToken
            "authToken", connection.Session.Token
            "machineId", machineId
            "sku", product.Sku
            "os", "win"
            "fTime", connection.RunningTime().ToString() ]
        use request = buildRequest "/3.0/user/installer" platform connection queryParams
        let! content = fetch connection.HttpClient request
        
        let version = content >>= Json.parseProp "version" >>= Json.asVersion
        let remotePath = content >>= Json.parseProp "remotePath" >>= Json.toString |> (Result.map Hex.parseIso88591String)
        let localFile = content >>= Json.parseProp "localFile" >>= Json.toString |> (Result.map System.IO.Path.GetFileName)
        let hash = content >>= Json.parseProp "md5" >>= Json.toString
        let size = content >>= Json.parseProp "size" >>= Json.toInt64
        
        return
            match version, remotePath, localFile, hash, size with
            | Ok version, Ok remotePath, Ok localFile, Ok hash, Ok size ->
                let metadata = { Hash = hash; LocalFile = localFile; RemotePath = Uri(remotePath); Size = size; Version = version }
                let product = { product with Metadata = Some metadata }
                if version = product.Version then
                    product |> Playable |> Ok
                else
                    product |> RequiresUpdate |> Ok
            | _ ->
                let content = content >>= Json.toString |> MinEdLauncher.Result.defaultWith id
                let msg = $"Unexpected json object %s{content}"
                Log.debug msg
                Error msg
}

let checkForUpdates platform machineId connection (products: Product list) = task {
    let! result =
        products
        |> List.map (checkForUpdate platform machineId connection)
        |> Task.whenAll
    
    return result
        |> Array.map (fun r ->
            match r with
            | Ok product -> Some product
            | Error msg -> Log.error msg; None)
        |> Array.choose id
        |> List.ofArray
        |> Ok
}
        

