module EdLauncher.Log

open System.IO
open System.Text.RegularExpressions
open Serilog
open Serilog.Events
open Serilog.Formatting
open Serilog.Formatting.Display

type EpicSanitizer(mainFormatter: ITextFormatter) = // https://github.com/serilog/serilog/issues/938#issuecomment-383440607
    let sanitiser = Regex(@"[a-z0-9]{32}", RegexOptions.IgnoreCase)
    
    interface ITextFormatter with
        member this.Format(logEvent, output) =
            use stringWriter = new StringWriter()
            mainFormatter.Format(logEvent, stringWriter)
            let input = stringWriter.ToString();
            let sanitized = sanitiser.Replace(input, (fun m -> $"{m.Value.[..2]}...{m.Value.[^2..]}"))

            try
                output.Write(sanitized);
            with
            | e -> Log.Error(e, "serilog Sanitiser broke");

let private fileFormatter = EpicSanitizer(MessageTemplateTextFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null))

let logger =
  let consoleLevel =
#if DEBUG
      LogEventLevel.Debug
#else      
      LogEventLevel.Information
#endif
  LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.Console(consoleLevel)
    .WriteTo.File(formatter = fileFormatter, path="ed.log", restrictedToMinimumLevel=LogEventLevel.Verbose)
    .CreateLogger()
    
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