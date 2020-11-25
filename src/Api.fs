namespace EdLauncher

module Api =
    open System
    open System.Threading.Tasks
    open FSharp.Control.Tasks.NonAffine
    open Types

    type AuthResult =
    | Authorized of sessionToken:EdSession * authToken:string * registeredName:string
    | RegistrationRequired of Uri
    | LinkAvailable of Uri
    | Denied of string
    | Failed of string

    type Request =
    | ServerTimestamp
    | LauncherStatus of string
    | Authenticate of authToken:AuthToken * platform:Platform * machineId:string
    | AuthorizedProjects of edToken:EdSession * platform:Platform * machineId:string * language:string option
    | CheckForUpdates of sessionToken:EdSession * platform:Platform * machineToken:string * machineId:string * Product

    type Response =
    | TimestampReceived of int64
    | StatusReceived of LauncherStatus
    | Authenticated of AuthResult
    | ProductsReceived of AuthorizedProduct list
    | UpdatesChecked of Product
        
    let getTime (now:DateTime) (serverRequest: Request -> Task<Result<Response, 'a>>) = task {
        match! serverRequest ServerTimestamp with
        | Ok (TimestampReceived unixTimestamp) -> return Ok unixTimestamp
        | Error msg ->
            let localTimestamp = (int64)(now.Subtract(DateTime(1970, 1, 1))).TotalSeconds
            return Error (localTimestamp, msg)
        | Ok _ -> return failwith "Invalid return type"
    }

    let authenticate token platform machineId (serverRequest: Request -> Task<Result<Response, string>>) = task {
        match! serverRequest (Authenticate (token, platform, machineId)) with
        | Ok (Authenticated result) -> return result 
        | Error msg -> return Failed msg
        | Ok _ -> return failwith "Invalid return type"
    }
    
    let getAuthorizedProducts token platform machineId lang (serverRequest: Request -> Task<Result<Response, 'a>>) = task {
        match! serverRequest (AuthorizedProjects (token, platform, machineId, lang)) with
        | Ok (ProductsReceived projects) -> return Ok projects
        | Error msg -> return Error msg
        | Ok _ -> return failwith "Invalid return type"
    }
    
    let checkForUpdates sessionToken platform machineToken machineId (serverRequest: Request -> Task<Result<Response, string>>) (products: Product list) = task {
        let! result =
            products
            |> List.map (fun p -> serverRequest (CheckForUpdates (sessionToken, platform, machineToken, machineId, p)))
            |> Task.whenAll
        
        return result
            |> Array.map (fun r ->
                match r with
                | Ok (UpdatesChecked product) -> Some product
                | Error msg -> Log.error msg; None
                | Ok _ -> failwith "Invalid return type")
            |> Array.choose id
            |> List.ofArray
            |> Ok
    }
        

