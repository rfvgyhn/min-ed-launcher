namespace EdLauncher

module Api =
    open System
    open Types

    type AuthDetails =
    | Steam of sessionToken:string * machineId:string

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
    | AuthorizedProjects of sessionToken:string * language:string option

    type Response =
    | TimestampReceived of int64
    | StatusReceived of LauncherStatus
    | Authenticated of AuthResult
    | ProductsReceived of AuthorizedProduct list
        
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
    
    let getAuthorizedProducts sessionToken lang serverRequest = async {
        match! serverRequest (AuthorizedProjects (sessionToken, lang)) with
        | Ok (ProductsReceived projects) -> return Ok projects
        | Error msg -> return Error msg
        | Ok _ -> return failwith "Invalid return type"
    }
        

