module MinEdLauncher.Interop
#nowarn "9"

open System.Diagnostics
open System
open System.Runtime.InteropServices

#if WINDOWS
[<Literal>]
let private STD_OUTPUT_HANDLE = -11
[<Literal>]
let private ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004u;

[<DllImport("kernel32.dll")>]
extern bool private GetConsoleMode(IntPtr hConsoleHandle, uint& lpMode)
[<DllImport("kernel32.dll")>]
extern IntPtr private GetStdHandle(int nStdHandle)

let ansiColorSupported() =
    let stdOut = GetStdHandle(STD_OUTPUT_HANDLE)
    let mutable consoleMode = Unchecked.defaultof<uint>
    GetConsoleMode(stdOut, &consoleMode)
    && consoleMode &&& ENABLE_VIRTUAL_TERMINAL_PROCESSING = ENABLE_VIRTUAL_TERMINAL_PROCESSING

let private terminate (p: Process) =
    if p.CloseMainWindow() then
        Ok ()
    else
        Error ""
#else
open Microsoft.FSharp.NativeInterop

[<DllImport("libc", SetLastError = true)>]
extern int private kill(int pid, int signal)

[<DllImport("libc", SetLastError = true)>]
extern int private strerror_r(int errnum, char *buf, UInt64 buflen);

let private getErrorMessage errno =    
    let buffer = NativePtr.stackalloc<char> 1024;
    let result = strerror_r(errno, buffer, 1024UL);

    if result = 0 then
        Marshal.PtrToStringAnsi(buffer |> NativePtr.toNativeInt)
    else
        $"errno %i{errno}"

[<Literal>]
let private SIGTERM = 15
[<Literal>]
let private ESRCH = 3  
let private terminate (p: Process) =
    let code = kill(p.Id, SIGTERM)
    let errno = Marshal.GetLastWin32Error()
    if code = -1 && errno <> ESRCH then // ESRCH = process does not exist, assume it exited
        getErrorMessage errno |> Error
    else
        Ok code
        
let ansiColorSupported() = not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM")))
#endif

let termProcess (p: Process) =
    if p.HasExited then
        Ok ()
    else
        match terminate p with
        | Ok _ -> Ok ()
        | Error msg ->
            p.Kill(true)
            Error $"Unable to gracefully stop %s{p.ProcessName}. Killed process instead. %s{msg}"