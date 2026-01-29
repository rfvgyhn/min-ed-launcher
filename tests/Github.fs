module MinEdLauncher.Tests.Github

open System
open MinEdLauncher.Github
open MinEdLauncher.Tests.Extensions
open Expecto

[<Tests>]
let tests =
    testList "Github" [
        testList "releasesSince" [
            let release = { TagName = "1.0"; Draft = false; Prerelease = false; Body = "" }
            test "Can parse version with 'v' prefix" {
                let githubReleases = [ { release with TagName = "v1.0.0" }]
                let expected = Standard { Version = new Version(1, 0, 0); ReleaseNotes = "" }
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.equal releases.Head expected ""
            }
            test "Can parse version without 'v' prefix" {
                let githubReleases = [{ release with TagName = "1.0.0" }]
                let expected = Standard { Version = new Version(1, 0, 0); ReleaseNotes = "" }
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.equal releases.Head expected ""
            }
            test "Ignores release with empty tag name" {
                let githubReleases = [{ release with TagName = "" }]
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.isEmpty releases ""
            }
            test "Ignores release with null tag name" {
                let githubReleases = [{ release with TagName = null }]
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.isEmpty releases ""
            }
            test "Ignores release with non-version-like tag name" {
                let githubReleases = [{ release with TagName = "asdf" }]
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.isEmpty releases ""
            }
            test "Ignores releases released before specified version" {
                let githubReleases = [
                    { release with TagName = "1.0" }
                    { release with TagName = "2.0" }
                    { release with TagName = "3.0" }
                ]
                
                let releases = releasesSince (new Version(2, 0)) githubReleases
                
                Expect.hasLength releases 1 ""
            }
            test "Ignores draft releases" {
                let githubReleases = [
                    { release with Draft = true; Body = "one" }
                    { release with Draft = false; Body = "two" }
                ]
                let expected = Standard { Version = new Version(1, 0); ReleaseNotes = "two" }
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.hasLength releases 1 ""
                Expect.equal releases.Head expected ""
            }
            test "Ignores pre-releases" {
                let githubReleases = [
                    { release with Prerelease = true; Body = "one" }
                    { release with Prerelease = false; Body = "two" }
                ]
                let expected = Standard { Version = new Version(1, 0); ReleaseNotes = "two" }
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.hasLength releases 1 ""
                Expect.equal releases.Head expected ""
            }
            test "Is a security release when body contains security header" {
                let githubReleases = [{ release with Body = "asdf ### Security asdf" }]
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.isUnionCase releases.Head <@ Security @> ""
            }
            test "Is a standard release when body doesn't contain security header" {
                let githubReleases = [{ release with Body = "asdf # Security asdf" }]
                
                let releases = releasesSince (new Version()) githubReleases
                
                Expect.isUnionCase releases.Head <@ Standard @> ""
            }
            test "Matches CVEs" {
                let githubReleases = [
                    { release with Body = "### Security CVE 2023-1234 CVE-2023-1235 CVE-2023 0000" }
                ]
                
                let releases = releasesSince (new Version()) githubReleases
                
                match releases.Head with
                | Security d ->
                    Expect.hasLength d.Cves 2 ""
                    Expect.contains d.Cves "2023-1234" ""
                    Expect.contains d.Cves "2023-1235" ""
                | _ -> failwith "Not a security release"
                Expect.isUnionCase releases.Head <@ Security @> ""
            }
        ]
        testList "mergeReleases" [
            test "Is newest" {
                let releases = [
                    Standard { Version = new Version(1, 0, 0); ReleaseNotes = "" }
                    Standard { Version = new Version(3, 0, 0); ReleaseNotes = "" }
                    Standard { Version = new Version(2, 0, 0); ReleaseNotes = "" }
                ]
                let expected = Standard { Version = new Version(3, 0, 0); ReleaseNotes = "" } |> Some
                
                let actual = mergeReleases releases
                
                Expect.equal actual expected ""
            }
            test "Is none if no releases" {
                let releases = []            
                
                let actual = mergeReleases releases
                
                Expect.equal actual None ""
            }            
            test "Includes CVEs from all releases" {
                let releases = [
                    Security { Cves = ["2023-1234"]; Details = { Version = new Version(1, 0, 0); ReleaseNotes = "" } }
                    Standard { Version = new Version(2, 0, 0); ReleaseNotes = "" }
                    Security { Cves = ["2023-1235"]; Details = { Version = new Version(3, 0, 0); ReleaseNotes = "" } }
                ]
                let expected =
                    Security { Cves = ["2023-1234"; "2023-1235"]
                               Details = { Version = new Version(3, 0, 0); ReleaseNotes = "" } }
                    |> Some
                
                let actual = mergeReleases releases
                
                Expect.equal actual expected ""
            }
            test "Is a Security release if at least one release is a Security release" {
                let releases = [
                    Security { Cves = ["2023-1234"]; Details = { Version = new Version(1, 0, 0); ReleaseNotes = "" } }
                    Standard { Version = new Version(2, 0, 0); ReleaseNotes = "" }
                ]
                let expected =
                    Security { Cves = ["2023-1234"]
                               Details = { Version = new Version(2, 0, 0); ReleaseNotes = "" } }
                    |> Some
                
                let actual = mergeReleases releases
                
                Expect.equal actual expected ""
            }
        ]
    ]