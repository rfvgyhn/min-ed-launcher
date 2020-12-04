module MinEdLauncher.Api

open System
open System.Net
open System.Net.Http
open FSharp.Control.Tasks.NonAffine
open MinEdLauncher.Token
open Types
open Rop

type Connection =
    { HttpClient: HttpClient
      Session: EdSession
      RunningTime: unit -> double }
    with static member Create httpClient session runningTime =
            { HttpClient = httpClient; Session = session; RunningTime = runningTime }
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

let private buildRequest (path: string) platform connection queryParams =
    let request = new HttpRequestMessage()
    request.RequestUri <- buildUri connection.HttpClient.BaseAddress path queryParams
    match platform with
    | Steam -> Some $"bearer %s{connection.Session.Token}"
    | Epic _ -> Some $"epic %s{connection.Session.PlatformToken.GetAccessToken()}"
    | _ -> None
    |> Option.iter (fun header -> request.Headers.Add("Authorization", header))

    request
    
let private fetch (httpClient: HttpClient) requestMessage = task {
    use! response = httpClient.SendAsync(requestMessage)
    if response.IsSuccessStatusCode then
        use! content = response.Content.ReadAsStreamAsync()
        return content |> Json.parseStream >>= Json.rootElement
    else
        return Error $"%i{int response.StatusCode}: %s{response.ReasonPhrase}"
}

let createClient apiUri version os =
    let appName = "min-ed-launcher"
    let userAgent = $"%s{appName}/%s{version}/%s{os}"
    let httpClient = new HttpClient()
    httpClient.BaseAddress <- apiUri
    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
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

let authenticate (runningTime: unit -> double) (token: AuthToken) platform machineId (httpClient:HttpClient) = task {
    let path, query =
        let queryParams other =
            [ "machineId", machineId
              "fTime", runningTime().ToString() ] |> List.append other
        match platform with
        | Steam -> "/3.0/user/steam/auth", queryParams [ "steamTicket", token.GetAccessToken() ]
        | Epic _ -> "/3.0/user/forctoken", queryParams []
        | _ -> raise (NotImplementedException())
    use request = new HttpRequestMessage()
    request.RequestUri <- buildUri httpClient.BaseAddress path query
    match platform with
    | Epic _ -> request.Headers.Add("Authorization", $"epic %s{token.GetAccessToken()}")
    | _ -> ()
    // TODO: set allow redirect to false
    
    use! response = httpClient.SendAsync(request)
    use! content = response.Content.ReadAsStreamAsync()
    let content = content |> Json.parseStream >>= Json.rootElement
    let mapResult f = function
        | Ok value -> f value
        | Error msg -> Failed msg
    let parseError content =
        let errorValue = content >>= Json.parseProp "error_enum" >>= Json.toString |> Result.defaultValue "Unknown"
        let errorMessage = content >>= Json.parseProp "message" >>= Json.toString |> Result.defaultValue ""
        errorValue, errorMessage
    
    return
        match response.StatusCode with
        | code when int code < 300 ->
            let fdevAuthToken = content >>= Json.parseProp "authToken" >>= Json.toString
            let machineToken = content >>= Json.parseProp "machineToken" >>= Json.toString
            let registeredName = content >>= Json.parseProp "registeredName"
                                     >>= Json.toString
                                     |> Result.defaultValue $"%s{platform.Name} User"
            let errorValue = content >>= Json.parseProp "error_enum" >>= Json.toString
            let errorMessage = content >>= Json.parseProp "message" >>= Json.toString
            
            match fdevAuthToken, machineToken, errorValue, errorMessage with
            | Error _, Error _, Ok value, Ok msg -> Failed $"%s{value} - %s{msg}"
            | Ok fdevToken, Ok machineToken, _, _ ->
                let session = { Token = fdevToken; PlatformToken = token; Name = registeredName; MachineToken = machineToken }
                Authorized <| Connection.Create httpClient session runningTime
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

let getAuthorizedProducts platform lang connection = task {
    use request =
        match platform with
        | Frontier -> [ "authToken", connection.Session.Token; "machineToken", connection.Session.MachineToken ]
        | Steam | Epic _ -> []
        | p ->
            Log.error $"Attempting to get projects for unsupported platform {p.Name}"
            []
        |> List.append [ if Option.isSome lang then "lang", Option.get lang
                         "fTime", connection.RunningTime().ToString() ]
        |> buildRequest "/3.0/user/purchases" platform connection
        
    let! content = fetch connection.HttpClient request
    return content
       >>= Json.parseProp "purchases"
       >>= Json.mapArray (fun (element) ->
              let filter = element |> Json.parseProp "filter" >>= Json.toString |> Result.defaultValue ""
              let directory = element |> Json.parseProp "directory" >>= Json.toString
              let gameArgs = element |> Json.parseProp "gameargs" >>= Json.toString |> Result.defaultValue ""
              let serverArgs = element |> Json.parseProp "serverargs" >>= Json.toString |> Result.defaultValue ""
              let sortKey = element |> Json.parseProp "sortkey" >>= Json.toInt 
              let name = element |> Json.parseProp "product_name" >>= Json.toString
              let sku = element |> Json.parseProp "product_sku" >>= Json.toString
              let testApi = element |> Json.parseProp "testapi" >>= Json.asBool |> Result.defaultValue false
              match directory, sortKey, name, sku with
              | Ok directory, Ok sortKey, Ok name, Ok sku ->
                  Ok { Name = name
                       Filter = filter
                       Directory = directory
                       GameArgs = gameArgs
                       ServerArgs = serverArgs
                       SortKey = sortKey
                       Sku = sku
                       TestApi = testApi }
              | _ ->
                  let msg = $"Unexpected json object %s{element.ToString()}"
                  Log.debug msg
                  Error msg)
       |> Result.map Seq.chooseResult
       |> Result.map (fun products -> products |> Seq.sortBy (fun p -> p.SortKey) |> List.ofSeq)
}

let checkForUpdate platform machineId connection product = task {
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
        
        return content
            >>= Json.parseProp "version"
            >>= Json.asVersion
            |> Result.map (fun remoteVersion ->
                if remoteVersion = product.Version then
                    product |> Playable
                else
                    product |> RequiresUpdate)
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
        

