module EdLauncher.Tests.Legendary

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open Microsoft.Extensions.Time.Testing

[<Tests>]
let tests =            
    testList "Legendary" [
        testList "getExistingCredentials" [
            let defaultJson = JsonDocument.Parse("""
            {
              "access_token": "eg1~abc...def",
              "account_id": "89270d0d394841eda5d57cf3388f9241",
              "acr": "urn:epic:loa:aal2",
              "app": "launcher",
              "auth_time": "2024-06-03T20:23:50.919Z",
              "client_id": "9a64592735eb430287fe03e23d2cb3e7",
              "client_service": "launcher",
              "device_id": "33c751da562c41608c63b4727fc106c1",
              "displayName": "Dwight Schrute",
              "expires_at": "2024-01-01T06:34:52.193Z",
              "expires_in": 28800,
              "in_app_id": "6476a6acab374e169472067a6e48c0d9",
              "internal_client": true,
              "refresh_expires": 1987200,
              "refresh_expires_at": "2024-01-23T19:51:43.163Z",
              "refresh_token": "eg1~123...456",
              "scope": [],
              "token_type": "bearer"
            }""")
            
            test "Fails if missing access_token" {
                let time = FakeTimeProvider()
                let json = defaultJson.Deserialize<JsonObject>()
                json.Remove("access_token") |> ignore
                let json = json.Deserialize<JsonDocument>()
                
                let token = Legendary.parseAccessToken time json
                
                Expect.isError token ""
            }
            
            testTheory "Fails if access_token is not a string"
                [ JsonValue.Create(1) :> JsonNode; JsonValue.Create(true)
                  JsonObject(); JsonArray() ] <| fun value ->
                let time = FakeTimeProvider()
                let json = defaultJson.Deserialize<JsonObject>()
                json["access_token"] <- value
                let json = json.Deserialize<JsonDocument>()
                
                let token = Legendary.parseAccessToken time json
                
                Expect.isError token ""
            
            test "Fails if missing expires_at" {
                let time = FakeTimeProvider()
                let json = defaultJson.Deserialize<JsonObject>()
                json.Remove("expires_at") |> ignore
                let json = json.Deserialize<JsonDocument>()
                
                let token = Legendary.parseAccessToken time json
                
                Expect.isError token ""
            }
            
            testTheory "Fails if expires_at is not a date time"
                [ JsonValue.Create(1) :> JsonNode; JsonValue.Create(true)
                  JsonValue.Create("abc"); JsonObject(); JsonArray(); ] <| fun value ->
                let time = FakeTimeProvider()
                let json = defaultJson.Deserialize<JsonObject>()
                json["expires_at"] <- value
                let json = json.Deserialize<JsonDocument>()
                
                let token = Legendary.parseAccessToken time json
                
                Expect.isError token ""
            
            test "Fails if access token is expired" {
                let now = DateTimeOffset(DateTime(2000, 1, 1))
                let time = FakeTimeProvider()
                time.SetUtcNow(now)
                let json = defaultJson.Deserialize<JsonObject>()
                json["expires_at"] <- now.Subtract(TimeSpan.FromSeconds(1)).ToString("o", CultureInfo.InvariantCulture)
                let json = json.Deserialize<JsonDocument>()
                
                let token = Legendary.parseAccessToken time json
                
                Expect.isError token ""
            }
            
            test "Can parse valid file" {
                let time = FakeTimeProvider()
                let accessToken = "test"
                let json = defaultJson.Deserialize<JsonObject>()
                json["access_token"] <- accessToken
                let json = json.Deserialize<JsonDocument>()
                
                let token = Legendary.parseAccessToken time json
                
                Expect.equal token (Ok(accessToken)) ""
            }
        ]
    ]