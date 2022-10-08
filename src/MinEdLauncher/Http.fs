module MinEdLauncher.Http

open System
open System.Collections.Generic
open System.IO
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Net.Http
open System.Text
open System.Threading.Tasks
open HttpClientExtensions

type DownloadProgress = { TotalFiles: int; BytesSoFar: int64; Elapsed: TimeSpan; TotalBytes: int64; }
type DownloadAll<'a, 'b> = IProgress<int> -> 'a[] -> Task<'b[]>
type Downloader<'a, 'b> = { Download: DownloadAll<'a, 'b>; Progress: IProgress<DownloadProgress> }
type FileDownloadRequest = { RemotePath: string; TargetPath: string; ExpectedHash: string }
type FileIntegrity = Valid | Invalid
module FileIntegrity =
    let fromBool = function true -> Valid | false -> Invalid
type FileDownloadResponse = { FilePath: string; Hash: string; Integrity: FileIntegrity }

let downloadFile (httpClient: HttpClient) (createHash: unit -> HashAlgorithm) cancellationToken progress request = task {
    let bufferSize = 8192
    use hashAlgorithm = createHash()
    use fileStream = new FileStream(request.TargetPath, FileMode.Create, FileAccess.Write, FileShare.Write, bufferSize, FileOptions.Asynchronous)
    use cryptoStream = new CryptoStream(fileStream, hashAlgorithm, CryptoStreamMode.Write) // Calculate hash as file is downloaded
    
    do! httpClient.DownloadAsync(request.RemotePath, cryptoStream, bufferSize, progress, cancellationToken)
    cryptoStream.Dispose()
    let hash = hashAlgorithm.Hash |> Hex.toString |> String.toLower
    return { FilePath = request.TargetPath; Hash = hash; Integrity = request.ExpectedHash = hash |> FileIntegrity.fromBool } }

let createClient launcherVersion =
    let appName = "min-ed-launcher"
    let userAgent = $"%s{appName}/%s{launcherVersion}/%s{RuntimeInformation.getOsIdent()}"
    let httpClient = new HttpClient()
    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent) |> ignore
    httpClient
    
let dumpHeaders (httpHeaders: HttpHeaders list) indentAmt (sb: StringBuilder) =
    let indent = String.replicate indentAmt " "
    sb.AppendLine("{") |> ignore
    httpHeaders
    |> List.iter (fun hh ->
        hh.NonValidated
        |> Seq.iter (fun (header: KeyValuePair<string, HeaderStringValues>) ->
            header.Value
            |> Seq.iter (fun value ->
                sb.Append(String.replicate indentAmt indent)
                  .Append(header.Key)
                  .Append(": ")
                  .AppendLine(value)
                |> ignore
            )
        )
    )
    sb.AppendLine(indent + "}") |> ignore
