namespace EdLauncher

module Server =

    open System
    open System.Net
    open System.Net.Http
    open Api
    open Types
    open Rop

    let buildUri (host:Uri) (path:string) =
        let builder = UriBuilder(host)
        builder.Path <- path
        
        builder.Uri
    
        
    let private getTime (httpClient:HttpClient) = async {
        let uri = buildUri httpClient.BaseAddress "/1.1/server/time"
        use! result = httpClient.GetAsync(uri) |> Async.AwaitTask
        
        if result.IsSuccessStatusCode then
            use! content = result.Content.ReadAsStreamAsync() |> Async.AwaitTask
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
    
    let private authenticate (httpClient:HttpClient) (runningTime: unit -> double) details = async {
        match details with
        | Api.Steam (sessionToken, machineId) ->
            // TODO: event log entries
            let uri = buildUri httpClient.BaseAddress "/3.0/user/steam/auth"
                      |> Uri.addQueryParams [
                          "steamTicket", sessionToken
                          "machineId", machineId
                          "fTime", runningTime().ToString() ]
            // TODO: set allow redirect to false
            use! response = httpClient.GetAsync(uri) |> Async.AwaitTask
            use! content = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
            
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
                    let authToken = root >>= Json.parseProp "authToken" >>= Json.toString
                    let machineToken = root >>= Json.parseProp "machineToken" >>= Json.toString
                    let registeredName = root >>= Json.parseProp "registeredName"
                                             >>= Json.toString
                                             |> Result.defaultValue "Steam User"
                    let errorValue = root >>= Json.parseProp "error_enum" >>= Json.toString
                    let errorMessage = root >>= Json.parseProp "message" >>= Json.toString
                                         
                    match authToken, machineToken, errorValue, errorMessage with
                    | Error _, Error _, Ok value, Ok msg -> Failed <| sprintf "%s - %s" value msg
                    | Ok auth, Ok machine, _, _ -> Authorized (auth, machine, registeredName)
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
    
    let private getAvailableProjects (httpClient:HttpClient) (runningTime: unit -> double) sessionToken lang = async {
        let uri = buildUri httpClient.BaseAddress "/3.0/user/purchases"
                  |> Uri.addQueryParams [
                          if Option.isSome lang then "lang", Option.get lang
                          "fTime", runningTime().ToString() ]
        use request = new HttpRequestMessage()
        request.Headers.Add("Authorization", sprintf "bearer %s" sessionToken)
        request.Method <- HttpMethod.Get
        request.RequestUri <- uri
        let! response = httpClient.SendAsync(request) |> Async.AwaitTask
        
        if response.IsSuccessStatusCode then
            use! content = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
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
                                       printfn "Unexpected json object %s" (element.ToString()) // TODO: log invalid json objects
                                       sprintf "Unexpected json object %s" (element.ToString()) |> Error)
                           |> Result.map Seq.chooseResult
                           |> Result.map (fun products -> products |> Seq.sortBy (fun p -> p.SortKey)
                                                                   |> List.ofSeq
                                                                   |> ProductsReceived)
        else
            return sprintf "%i: %s" ((int)response.StatusCode) response.ReasonPhrase |> Error
    }
        
    let request (httpClient:HttpClient) (runningTime: unit -> double) request = async {
        try
            return! match request with
                    | ServerTimestamp -> getTime httpClient
                    | LauncherStatus currentVersion -> async { return Ok <| StatusReceived Current }
                    | Authenticate details -> authenticate httpClient runningTime details
                    | AuthorizedProjects (sessionToken, lang) -> getAvailableProjects httpClient runningTime sessionToken lang 
        with
        | e -> return Error e.Message
    }