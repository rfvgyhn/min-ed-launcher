module EdLauncher.Log

open System
open System.IO
open Serilog
open Serilog.Events

let logger = 
  LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("ed.log")
    .CreateLogger()
    
let private timestamp (tw: TextWriter) = tw.Write("{0:yyyy-MM-dd hh:mm:ss.ff}", DateTime.Now)
let private write level msg =
    logger.Write(level, msg)
let private writeExn exn level msg =
    logger.Write(level, msg)
    
let private log level format = Printf.ksprintf (write level) format
let private logExn level exn format = Printf.ksprintf (writeExn exn level) format

let debugf format = log LogEventLevel.Debug format
let debug msg = debugf "%s" msg
let infof format = log LogEventLevel.Information format
let info msg = infof "%s" msg
let warnf format = log LogEventLevel.Warning format
let warn msg = warnf "%s" msg
let errorf format = log LogEventLevel.Error format
let error msg = errorf "%s" msg
let exnf e format = logExn LogEventLevel.Fatal e format
let exn e msg = exnf e "%s" msg