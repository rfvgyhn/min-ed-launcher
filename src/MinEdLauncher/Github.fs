module MinEdLauncher.Github

open System
open System.Net
open System.Net.Http
open System.Net.Http.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions

[<CLIMutable>]
type ReleaseJson = {
    [<JsonPropertyName("tag_name")>]
    TagName: string
    Draft: bool
    Body: string
}
type ReleaseDetails = {
    Version: Version
    ReleaseNotes: string
}
type SecurityReleaseDetails = {
    Details: ReleaseDetails
    Cves: string list
}
type Release = Standard of ReleaseDetails | Security of SecurityReleaseDetails

let mergeReleases releases =
    releases
    |> List.sortBy (function
        | Security d -> d.Details.Version
        | Standard d -> d.Version)
    |> List.fold (fun state current ->
        match state with
        | Some(Security details) ->
            match current with
            | Security r -> Security { Details = r.Details; Cves = details.Cves @ r.Cves } |> Some
            | Standard r ->  Security { Details = r; Cves = details.Cves } |> Some
        | Some(Standard _) -> Some current
        | None -> Some current) None

let releasesSince version (releases: ReleaseJson list) =
    releases
    |> List.filter (fun r -> not r.Draft)
    |> List.choose (fun r ->
        if r.TagName = null || r.TagName.Length = 0 then
            None
        else
            let tag = if r.TagName.StartsWith('v') then r.TagName.AsSpan().Slice(1) else r.TagName.AsSpan()
            Version.TryParse(tag) |> function
            | true, v -> Some v
            | false, _ -> None
            |> Option.map(fun v ->
                { Version = v
                  ReleaseNotes = r.Body
                }))
    |> List.filter(fun r -> r.Version > version)
    |> List.map (fun r ->
        if r.ReleaseNotes.Contains("### Security") then
            let cves =
                try
                    [ for m in Regex.Matches(r.ReleaseNotes, "CVE[- ](\d{4}-\d+)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) do
                        yield! m.Groups |> Seq.skip 1 |> Seq.map (fun g -> g.Value) ]
                with
                    | :? RegexMatchTimeoutException -> []
            Security { Details = r; Cves = cves }
        else
            Standard r
    )

let getUpdatedLauncher version (httpClient: HttpClient) cancellationToken = task {
    try
        raise (HttpRequestException("asdf", Exception("inner"), HttpStatusCode.Forbidden))
        let! releases = httpClient.GetFromJsonAsync<ReleaseJson list>("https://api.github.com/repos/rfvgyhn/min-ed-launcher/releases", cancellationToken)
        
        return releases
        |> releasesSince version
        |> mergeReleases
        |> Ok
    with
    | e ->
        return (Error e)
    
}