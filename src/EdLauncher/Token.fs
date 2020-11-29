module EdLauncher.Token

open System
open System.Threading.Tasks
open System.Timers

type RefreshableToken =
        { Token: string
          TokenInterval: int
          RefreshToken: string }
        with static member Empty = { Token = ""; TokenInterval = Int32.MinValue; RefreshToken = "" }

type private RefreshableTokenMessage =
    | Get of replyChannel: AsyncReplyChannel<RefreshableToken>
    | Refresh of RefreshableToken

type RefreshableTokenManager(initialToken, refresh: (RefreshableToken -> Task<Result<RefreshableToken, string>>)) =
    let agent = MailboxProcessor.Start(fun inbox ->
        let rec loop token = async {
            match! inbox.Receive() with
            | Get channel ->
                channel.Reply token
                return! loop token
            | Refresh t ->
                return! loop t }
        loop initialToken)
    let onRefresh() = async {
        let currentToken = agent.PostAndReply Get
        Log.debug $"Refreshing token: %A{currentToken}"
        let! token = refresh currentToken |> Async.AwaitTask
        Log.debug $"Token refreshed: %A{token}"
        return token |> Result.map (fun t -> t |> Refresh |> agent.Post) |> Result.defaultValue () }
    
    let timer = new Timer((float initialToken.TokenInterval) * 1000.)
    do
        if initialToken.TokenInterval > 0 then
            timer.Elapsed.Add(fun _ -> onRefresh() |> Async.StartImmediate) // TODO: What's the proper way to add a task based async event handler in F#? 
            timer.Start()
    
    member this.Get() = agent.PostAndReply Get
    interface IDisposable with
        member _.Dispose() =
            timer.Dispose()
            
type AuthToken =
    | Expires of (unit -> RefreshableToken)
    | Permanent of string
    member this.GetAccessToken() =
           match this with
           | Expires t -> t().Token
           | Permanent t -> t
    member this.GetRefreshToken() =
           match this with
           | Expires t -> Some (t().RefreshToken)
           | Permanent _ -> None
