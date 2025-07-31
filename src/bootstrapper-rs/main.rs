#[cfg(not(target_os = "windows"))]
compile_error!("Can only be built on Windows");

use std::ffi::OsStr;
use std::fs::OpenOptions;
use std::io::Write;
use std::os::windows::ffi::OsStrExt;
use std::path::PathBuf;
use std::{env, fs};
use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::GetLastError;
use windows::Win32::UI::Shell::{
    FOLDERID_LocalAppData, SHGetKnownFolderPath, ShellExecuteW, KNOWN_FOLDER_FLAG,
};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args = env::args().skip(1).collect::<Vec<String>>().join(" ");
    let args_pcwstr = OsStr::new(args.as_str())
        .encode_wide()
        .chain(Some(0))
        .collect::<Vec<_>>();
    let result = unsafe {
        ShellExecuteW(
            None,
            w!("open"),
            w!("MinEdLauncher.exe"),
            PCWSTR(args_pcwstr.as_ptr()),
            None,
            SW_SHOWNORMAL,
        )
    };
    if result.0 < 33 {
        // https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shellexecutew#return-value
        // https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes
        // ERROR_FILE_NOT_FOUND	    2
        // ERROR_PATH_NOT_FOUND	    3
        // ERROR_BAD_FORMAT	        11
        // SE_ERR_ACCESSDENIED	    5
        // SE_ERR_ASSOCINCOMPLETE   27
        // SE_ERR_DDEBUSY	        30
        // SE_ERR_DDEFAIL	        29
        // SE_ERR_DDETIMEOUT	    28
        // SE_ERR_DLLNOTFOUND	    32
        // SE_ERR_FNF	            2
        // SE_ERR_NOASSOC	        31
        // SE_ERR_OOM	            8
        // SE_ERR_PNF	            3
        // SE_ERR_SHARE	            26
        let (error_code, error_msg) = unsafe {
            let error = GetLastError();
            (error.0, error.to_hresult().message().to_string_lossy())
        };
        write_error(result.0, error_code, &error_msg)?;
    }
    Ok(())
}

fn write_error(result_code: isize, error_code: u32, msg: &str) -> Result<(), Box<dyn std::error::Error>> {
    eprintln!("Bootstrapper Error MinEdLauncher.exe: {msg} result: {result_code} error: {error_code}");
    
    let local_app_data_path = unsafe {
        SHGetKnownFolderPath(&FOLDERID_LocalAppData, KNOWN_FOLDER_FLAG(0), None)?.to_string()?
    };

    let file_path: PathBuf = [
        local_app_data_path.as_str(),
        "min-ed-launcher",
        "min-ed-launcher.log",
    ]
    .iter()
    .collect();

    let write = fs::create_dir_all(file_path.parent().unwrap()).and_then(|()| {
        let mut file = OpenOptions::new()
            .append(true)
            .create(true)
            .open(&file_path)?;

        writeln!(&mut file, "Bootstrapper Error MinEdLauncher.exe: {msg} result: {result_code} error: {error_code}")
    });

    if let Err(e) = write {
        eprintln!("Couldn't write to {}: {}", file_path.display(), e);
    }

    Ok(())
}
