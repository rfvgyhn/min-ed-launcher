namespace EdLauncher

module Server =

    open System
    open System.Net
    open System.Net.Http
    open FSharp.Control.Tasks.NonAffine
    open Api
    open Types
    open Rop

    let buildUri (host:Uri) (path:string) =
        let builder = UriBuilder(host)
        builder.Path <- path
        
        builder.Uri
    
        
    let private getTime (httpClient:HttpClient) = task {
        let uri = buildUri httpClient.BaseAddress "/1.1/server/time"
        use! result = httpClient.GetAsync(uri)
        
        if result.IsSuccessStatusCode then
            use! content = result.Content.ReadAsStreamAsync()
            let timeStamp = content |> Json.parseStream
                                    >>= Json.rootElement
                                    >>= Json.parseProp "unixTimestamp"
                                    >>= Json.asInt64
            match timeStamp with
            | Ok value -> return TimestampReceived value |> Ok
            | Error msg -> return Error msg            
        else
            return Error <| sprintf "%i: %s" ((int)result.StatusCode) result.ReasonPhrase
    }
    
    let private authenticate (httpClient:HttpClient) (runningTime: unit -> double) (token: AuthToken) platform machineId = task {
        // TODO: event log entries
        let uri, authHeader =
            let commonParams machineId =
                [ "machineId", machineId
                  "fTime", runningTime().ToString() ]
            match platform with
            | Steam ->
                buildUri httpClient.BaseAddress "/3.0/user/steam/auth"
                |> Uri.addQueryParam ("steamTicket", token.GetAccessToken())
                |> Uri.addQueryParams (commonParams machineId), None
            | Epic _ ->
                buildUri httpClient.BaseAddress "/3.0/user/forctoken"
                |> Uri.addQueryParams (commonParams machineId), Some $"epic %s{token.GetAccessToken()}"
            | p -> raise (NotImplementedException())
        use request = new HttpRequestMessage()
        request.Method <- HttpMethod.Get
        request.RequestUri <- uri
        match authHeader with
        | Some header -> request.Headers.Add("Authorization", header)
        | None -> ()
            
        // TODO: set allow redirect to false
        use! response = httpClient.SendAsync(request)
        use! content = response.Content.ReadAsStreamAsync()
        
        let result r = r |> Authenticated |> Ok
        let mapResult f = function
            | Ok value -> f value |> result
            | Error msg -> Failed msg |> result
        let parseError content =
            let root = content |> Json.parseStream >>= Json.rootElement
            let errorValue = root >>= Json.parseProp "error_enum" >>= Json.toString |> Result.defaultValue "Unknown"
            let errorMessage = root >>= Json.parseProp "message" >>= Json.toString |> Result.defaultValue ""
            errorValue, errorMessage
        
        return
            match response.StatusCode with
            | code when (int)code < 300 ->
                let root = content |> Json.parseStream >>= Json.rootElement
                let edAuthToken = root >>= Json.parseProp "authToken" >>= Json.toString
                let machineToken = root >>= Json.parseProp "machineToken" >>= Json.toString
                let registeredName = root >>= Json.parseProp "registeredName"
                                         >>= Json.toString
                                         |> Result.defaultValue "Steam User"
                let errorValue = root >>= Json.parseProp "error_enum" >>= Json.toString
                let errorMessage = root >>= Json.parseProp "message" >>= Json.toString
                
                match edAuthToken, machineToken, errorValue, errorMessage with
                | Error _, Error _, Ok value, Ok msg -> Failed <| sprintf "%s - %s" value msg
                | Ok auth, Ok machine, _, _ ->
                    let edToken = { Token = auth; PlatformToken = token }
                    Authorized (edToken, machine, registeredName)
                | Error msg, _, _, _
                | _, Error msg, _, _ -> Failed msg
                |> result
            | HttpStatusCode.Found ->
                content |> Json.parseStream
                        >>= Json.rootElement
                        >>= Json.parseProp "Location"
                        >>= Json.asUri
                        |> mapResult (fun uri -> RegistrationRequired uri)
            | HttpStatusCode.TemporaryRedirect ->
                content |> Json.parseStream
                        >>= Json.rootElement
                        >>= Json.parseProp "Location"
                        >>= Json.asUri
                        |> mapResult (fun uri -> LinkAvailable uri)
            | HttpStatusCode.BadRequest ->
                let errValue, errMessage = content |> parseError
                sprintf "Bad Request: %s - %s" errValue errMessage |> Denied |> result
            | HttpStatusCode.Forbidden ->
                let errValue, errMessage = content |> parseError
                sprintf "Forbidden: %s - %s" errValue errMessage |> Denied |> result
            | HttpStatusCode.Unauthorized ->
                let errValue, errMessage = content |> parseError
                sprintf "Unauthorized: %s - %s" errValue errMessage |> Denied |> result
            | HttpStatusCode.ServiceUnavailable ->
                Failed "Service unavailable" |> result
            | code ->
                sprintf "%i: %s" ((int)code) response.ReasonPhrase |> Error
    }
    
    let private getAvailableProjects (httpClient:HttpClient) (runningTime: unit -> double) session platform machineToken lang = task {
        let authHeader, authParams =
            match platform with
            | Steam -> Some $"bearer %s{session.Token}", []
            | Epic _ -> Some $"epic %s{session.PlatformToken.GetAccessToken()}", []
            | Frontier -> None, [ "authToken", session.Token; "machineToken", machineToken ]
            | p ->
                Log.error $"Attempting to get projects for unsupported platform {p |> Union.getCaseName}"
                None, []
        use request = new HttpRequestMessage()
        authHeader |> Option.iter (fun header -> request.Headers.Add("Authorization", header))
        request.Method <- HttpMethod.Get
        request.RequestUri <- buildUri httpClient.BaseAddress "/3.0/user/purchases"
                              |> Uri.addQueryParams [
                                      if Option.isSome lang then "lang", Option.get lang
                                      "fTime", runningTime().ToString() ]
                              |> Uri.addQueryParams authParams
        let! response = httpClient.SendAsync(request)
        
        if response.IsSuccessStatusCode then
            use! content = response.Content.ReadAsStreamAsync()
            return content |> Json.parseStream
                           >>= Json.rootElement
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
                           |> Result.map (fun products -> products |> Seq.sortBy (fun p -> p.SortKey)
                                                                   |> List.ofSeq
                                                                   |> ProductsReceived)
        else
            return sprintf "%i: %s" ((int)response.StatusCode) response.ReasonPhrase |> Error
    }
        
    let private checkForUpdates (httpClient:HttpClient) (runningTime: unit -> double) session platform machineToken machineId product = task {
        match product with
        | Unknown name -> return Error $"{name}: Can't check updates for unknown product"
        | Missing p -> return Error $"{p.Name}: Can't check updates for missing product"
        | RequiresUpdate product
        | Playable product ->
            let authHeader =
                match platform with
                | Steam -> None
                | Epic _ -> Some $"epic %s{session.PlatformToken.GetAccessToken()}"
                | Frontier -> None
                | p ->
                    Log.error $"Attempting to check for updates for unsupported platform {p |> Union.getCaseName}"
                    None
            use request = new HttpRequestMessage()
            authHeader |> Option.iter (fun header -> request.Headers.Add("Authorization", header))
            request.Method <- HttpMethod.Get
            request.RequestUri <- buildUri httpClient.BaseAddress "/3.0/user/installer"
                                  |> Uri.addQueryParams [
                                      "machineToken", machineToken
                                      "authToken", session.Token
                                      "machineId", machineId
                                      "sku", product.Sku
                                      "os", "win"
                                      "fTime", runningTime().ToString() ]
                                  
            use! response = httpClient.SendAsync(request)
            if response.IsSuccessStatusCode then
                use! content = response.Content.ReadAsStreamAsync()
                let root = content |> Json.parseStream >>= Json.rootElement
                let remoteVersion = root >>= Json.parseProp "version" >>= Json.asVersion
                let result p = p |> UpdatesChecked |> Ok
                
                match remoteVersion with
                | Ok remoteVersion ->
                    if remoteVersion = product.Version then
                        return product |> Playable |> result
                    else
                        return product |> RequiresUpdate |> result
                | Error msg -> return Error msg            
            else
                return Error $"%s{product.Name}: %i{int response.StatusCode}: %s{response.ReasonPhrase}"
    }
        
        
    let request (httpClient:HttpClient) (runningTime: unit -> double) request = task {
        try
            return! match request with
                    | ServerTimestamp -> getTime httpClient
                    | LauncherStatus currentVersion -> task { return Ok <| StatusReceived Current }
                    | Authenticate (token, platform, machineId) -> authenticate httpClient runningTime token platform machineId
                    | AuthorizedProjects (edSession, platform, machineId, lang) -> getAvailableProjects httpClient runningTime edSession platform machineId lang
                    | CheckForUpdates (edSession, platform, machineToken, machineId, product) -> checkForUpdates httpClient runningTime edSession platform machineToken machineId product
        with
        | e -> return Error e.Message
    }