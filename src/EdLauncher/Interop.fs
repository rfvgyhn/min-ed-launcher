module EdLauncher.Interop
#nowarn "9"

open System.Diagnostics
open System
open System.Runtime.InteropServices

#if WINDOWS
//    [<DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = false)>]
//    extern IntPtr private SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);
//
//    [<Literal>]
//    let private WM_CLOSE = 0x10u;
//    let private terminate handle =
//        SendMessage(handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero) |> ignore
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
let private terminate pid =
    let code = kill(pid, SIGTERM)
    let errno = Marshal.GetLastWin32Error()
    if code = -1 && errno <> ESRCH then // ESRCH = process does not exist, assume it exited
        getErrorMessage errno |> Error
    else
        Ok code
#endif

let termProcess (p: Process) =
    if p.HasExited then
        Ok ()
    else
#if WINDOWS
        match terminate p with
#else
        match terminate p.Id with
#endif
        | Ok _ -> Ok ()
        | Error msg ->
            p.Kill()
            Error $"Unable to gracefully stop %s{p.ProcessName}. Killed process instead. %s{msg}"