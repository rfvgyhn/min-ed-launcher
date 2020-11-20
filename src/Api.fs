namespace EdLauncher

module Api =
    open System
    open Types

    type AuthDetails =
    | Steam of sessionToken:string * machineId:string
    | Epic of accessToken:string * machineId:string
    | Frontier of sessionToken:string * machineId:string

    type AuthResult =
    | Authorized of sessionToken:string * authToken:string * registeredName:string
    | RegistrationRequired of Uri
    | LinkAvailable of Uri
    | Denied of string
    | Failed of string

    type Request =
    | ServerTimestamp
    | LauncherStatus of string
    | Authenticate of AuthDetails
    | AuthorizedProjects of AuthDetails * language:string option
    | CheckForUpdates of sessionToken:string * machineToken:string * machineId:string * Product

    type Response =
    | TimestampReceived of int64
    | StatusReceived of LauncherStatus
    | Authenticated of AuthResult
    | ProductsReceived of AuthorizedProduct list
    | UpdatesChecked of Product
        
    let getTime (now:DateTime) serverRequest = async {
        match! serverRequest ServerTimestamp with
        | Ok (TimestampReceived unixTimestamp) -> return Ok unixTimestamp
        | Error msg ->
            let localTimestamp = (int64)(now.Subtract(DateTime(1970, 1, 1))).TotalSeconds
            return Error (localTimestamp, msg)
        | Ok _ -> return failwith "Invalid return type"
    }

    let authenticate authDetails serverRequest = async {
        match! serverRequest (Authenticate authDetails) with
        | Ok (Authenticated result) -> return result 
        | Error msg -> return Failed msg
        | Ok _ -> return failwith "Invalid return type"
    }
    
    let getAuthorizedProducts authDetails lang serverRequest = async {
        match! serverRequest (AuthorizedProjects (authDetails, lang)) with
        | Ok (ProductsReceived projects) -> return Ok projects
        | Error msg -> return Error msg
        | Ok _ -> return failwith "Invalid return type"
    }
    
    let checkForUpdates sessionToken machineToken machineId serverRequest (products: Product list) : Async<Result<Product list, string>> = async {
        let! result =
            products
            |> List.map (fun p -> serverRequest (CheckForUpdates (sessionToken, machineToken, machineId, p)))
            |> Async.Parallel
        return result
            |> List.ofArray
            |> List.map (fun r ->
                match r with
                | Ok (UpdatesChecked product) -> Some product
                | Error msg -> None
                | Ok _ -> failwith "Invalid return type")
            |> List.choose id
            |> Ok
    }
        

