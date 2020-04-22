module EdLauncher.Log

open System
open System.IO

let private logSinks =
    [ fun (format: Printf.TextWriterFormat<'T, unit>) -> printfn format ]
let private exnLogSinks =
    [ fun (format: Printf.TextWriterFormat<'T, unit>) -> eprintfn format ]
    
let private timestamp (tw: TextWriter) = tw.Write("{0:yyyy-MM-dd hh:mm:ss.ff}", DateTime.Now)
let private write level msg =
    logSinks
    |> List.iter (fun sink -> sink "%t [%s] %s" timestamp level msg)
let private writeExn exn level msg =
    exnLogSinks
    |> List.iter (fun sink -> sink "%t [%s] %s%s  %A" timestamp level msg Environment.NewLine exn)
    
let private log level format = Printf.ksprintf (write level) format
let private logExn level exn format = Printf.ksprintf (writeExn exn level) format

let debugf format = log "DBG" format
let debug msg = debugf "%s" msg
let infof format = log "INF" format
let info msg = infof "%s" msg
let warnf format = log "WRN" format
let warn msg = warnf "%s" msg
let errorf format = log "ERR" format
let error msg = errorf "%s" msg
let exnf e format = logExn "ERR" e format
let exn e msg = exnf e "%s" msg