module FileIO

open System
open System.IO

let hasWriteAccess directory =
    try
        let temp = Path.Combine(directory, "deleteme.txt")
        File.WriteAllText(temp, "")
        File.Delete(temp)
        true
    with
    | :? UnauthorizedAccessException -> false

let ensureDirExists directory =
    try
        Directory.CreateDirectory directory |> ignore
        Ok directory
    with
    | :? UnauthorizedAccessException -> Error "Insufficient permissions"
    | :? ArgumentNullException
    | :? ArgumentException -> Error "Path is empty or contains invalid characters"
    | :? PathTooLongException -> Error "Exceeds maximum length"
    | :? DirectoryNotFoundException -> Error "The specified path is invalid (for example, it is on an unmapped drive)."
    | :? IOException -> Error "The directory specified by path is a file or the network name is not known."
    | :? NotSupportedException -> Error @"Contains a colon character (:) that is not part of a drive label (""C:\"")." 

