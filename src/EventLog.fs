namespace EdLauncher

module EventLog =
    open System
    open System.Collections
    open System.Collections.Generic
    open System.IO
    open System.Net.Http
    open System.Text.Json
    open Microsoft.FSharp.Reflection
    open Types

    [<Literal>]
    let MaxLogSize = 134217728L
    
    type Entry =
        | ClientVersion of application:string * path:string * modification:DateTime
        | LogStarted
        | LogReset
        | AvailableProjects of users:string * projects:string list
        
    type LocalFile = private LocalFile of string
    module LocalFile =
        let create (path:string) = async {
            if File.Exists path then
                return Ok <| LocalFile path
            else
                try
                    do! File.AppendAllTextAsync(path, "") |> Async.AwaitTask
                    return Ok <| LocalFile path
                with
                | e -> return Error e.Message
        }
        let value (LocalFile str) = str
    type RemoteLogParams =
        { Uri: Uri
          MachineToken: string
          AuthToken: string
          MachineId: string
          RunningTime: unit -> int64 }
    type RemoteLog = RemoteLog of HttpClient:HttpClient * RemoteLogParams
        
    let private getFields (case:UnionCaseInfo) (fields:Object[]) =
        let (|IsCollection|_|) (candidate : obj) =
            let t = candidate.GetType()
            if typedefof<ICollection>.IsAssignableFrom(t) || t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<list<_>>
            then Some (candidate :?> IEnumerable)
            else None
        case.GetFields()
        |> Seq.mapi (fun i p ->
            let field = fields.[i]
            
            p.Name,
            match field with
            | IsCollection c ->"[" + String.Join(", ", (Seq.cast<obj> c |> Seq.map (fun s -> s.ToString()))) + "]"
            | _ -> field.ToString())
        |> List.ofSeq
        
    let entryToString (now: unit -> DateTime) (entry:'T) =
        let case, values = FSharpValue.GetUnionFields(entry, typeof<'T>)
        let now = now()
        let data = [ "action", case.Name ]
                   @ getFields case values
                   @ [ "date", now.ToString("yyyyMMdd"); "time", now.ToString("HHmmss") ]
                   |> dict
                   |> Dictionary
        
        sprintf "%s: %s ;%s" (now.ToString("yyyyMMdd/HHmmss")) (JsonSerializer.Serialize(data)) Environment.NewLine

    let entryToRemote (entry:'T) =
        let case, values = FSharpValue.GetUnionFields(entry, typeof<'T>)
        let data = getFields case values
                   |> List.map (fun (key, value) -> sprintf "%s=%s" key value)
        case.Name, new StringContent (String.Join("&", data))
    
    let private getNow = fun () -> DateTime.UtcNow
    let private entryToStringUtcNow = entryToString getNow
    
    let private writeLocal path entries = async {
        match path with
        | None -> return Ok ()
        | Some path ->
            let filePath = LocalFile.value path
            let resetLine = [ if (FileIO.deleteFileIfTooBig MaxLogSize) filePath then entryToStringUtcNow LogReset ]
            let lines = String.Join("", entries
                                        |> List.map entryToStringUtcNow
                                        |> List.append resetLine)
            try
                do! File.AppendAllTextAsync(filePath, lines) |> Async.AwaitTask
                return Ok ()
            with
            | e -> return Error e.Message
    }

    let private writeRemote (remoteEntry:RemoteLog option) entries = 
        entries
        |> List.rev
        |> List.map (fun entry -> async { 
            match remoteEntry with 
            | None -> return Ok ()
            | Some (RemoteLog (client, parms)) ->
                let action, content = entryToRemote entry
                let uri = parms.Uri |> Uri.addQueryParams [
                    "event", action
                    "machineToken", parms.MachineToken
                    "authToken", parms.AuthToken
                    "fTime", parms.RunningTime().ToString()
                    "machineId", parms.MachineId
                ]
                use c = content
                try
                    let! result = (client.PostAsync(uri, c) |> Async.AwaitTask)
                    return Ok ()
                with
                | e -> return Error <| sprintf "Unable to write to remote log: %s - %s" action e.Message
        })

    let write localPath remoteUrl entries =
        [ writeLocal localPath entries ]
        |> List.append <| writeRemote remoteUrl entries
        |> Async.Parallel