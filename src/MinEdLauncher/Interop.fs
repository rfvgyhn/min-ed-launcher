module MinEdLauncher.Interop
#nowarn "9"

open System.Diagnostics
open System
open System.IO
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
[<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
extern bool GetDiskFreeSpaceEx(string lpDirectoryName, int64& lpFreeBytesAvailable, int64& lpTotalNumberOfBytes, int64& lpTotalNumberOfFreeBytes);

let freeDiskSpace path =
    let mutable freeBytes = Unchecked.defaultof<int64>
    let mutable dummy = Unchecked.defaultof<int64>
    if GetDiskFreeSpaceEx(path, &freeBytes, &dummy, &dummy) then
        Some freeBytes
    else
        None

let ansiColorSupported() =
    let stdOut = GetStdHandle(STD_OUTPUT_HANDLE)
    let mutable consoleMode = Unchecked.defaultof<uint>
    GetConsoleMode(stdOut, &consoleMode)
    && consoleMode &&& ENABLE_VIRTUAL_TERMINAL_PROCESSING = ENABLE_VIRTUAL_TERMINAL_PROCESSING

let private terminate (p: Process) =
    if p.CloseMainWindow() then
        Ok ()
    else
        Error "Process has no main window or the main window is disabled"
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

let freeDiskSpace path =
    try
        let drive = DriveInfo(path)
        Some drive.AvailableFreeSpace
    with _ ->
        None
#endif

let termProcess (timeout: TimeSpan) (p: Process) =
    if p.HasExited then
        Ok ()
    else
        terminate p
        |> Result.bind(fun _ ->
            if p.WaitForExit(timeout) then
                Ok ()
            else
                Error $"Process took longer than %f{timeout.TotalSeconds} seconds to shutdown"
            )
        |> Result.mapError (fun msg ->
            p.Kill(true)
            $"Unable to gracefully stop %s{p.ProcessName}. Killed process instead. %s{msg}"
            )