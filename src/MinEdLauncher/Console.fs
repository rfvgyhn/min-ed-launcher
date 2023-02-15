module MinEdLauncher.Console

open System.Diagnostics
open System.Threading
open MinEdLauncher.Http
open MinEdLauncher.Types
open System

let waitForQuit () =
    if not Console.IsInputRedirected then
        printfn "Press any key to quit..."
        Console.ReadKey() |> ignore

let readPassword () =
    let rec readMask pw =
        let k = Console.ReadKey(true)
        match k.Key with
        | ConsoleKey.Enter -> pw
        | ConsoleKey.Backspace ->
            match pw with
            | [] -> readMask []
            | _::t ->
                Console.Write "\b \b"
                readMask t
        | _ ->
            Console.Write "*"
            readMask (k.KeyChar::pw)
    let password = readMask [] |> Seq.rev |> String.Concat
    Console.WriteLine ()
    password

let promptForProductToPlay (products: ProductDetails array) (cancellationToken: CancellationToken) =
    printfn $"Select a product to launch (default=1):"
    products
    |> Array.indexed
    |> Array.iter (fun (i, product) -> printfn $"%i{i + 1}) %s{product.Name}")
        
    let rec readInput() =
        printf "Product: "
        let userInput = Console.ReadKey(true)
        printfn ""
        let couldParse, index =
            if userInput.Key = ConsoleKey.Enter then
                true, 1
            else
                Int32.TryParse(userInput.KeyChar.ToString())
        if cancellationToken.IsCancellationRequested then
            None
        else if couldParse && index > 0 && index < products.Length then
            let product = products.[index - 1]
            let filters = String.Join(", ", product.Filters)
            Log.debug $"User selected %s{product.Name} - %s{product.Sku} - %s{filters}"
            products.[index - 1] |> Some
        else
            printfn "Invalid selection"
            readInput()
    readInput()

let private consumeAvailableKeys () =
    while Console.KeyAvailable do
        Console.ReadKey() |> ignore

let cancelRestart timeout =
    let interval = 250
    let stopwatch = Stopwatch()
    consumeAvailableKeys()
    stopwatch.Start()
    while Console.KeyAvailable = false && stopwatch.ElapsedMilliseconds < timeout do
        Thread.Sleep interval
        let remaining = timeout / 1000L - stopwatch.ElapsedMilliseconds / 1000L
        Console.SetCursorPosition(0, Console.CursorTop)
        Console.Write($"Restarting in %i{remaining} seconds. Press <space> to start now. Press any other key to quit.")
        
    let left = Console.CursorLeft
    Console.SetCursorPosition(0, Console.CursorTop)
    if Console.KeyAvailable && Console.ReadKey().Key <> ConsoleKey.Spacebar then
        Console.WriteLine("Shutting down...".PadRight(left))
        true
    else
        Console.WriteLine("Restarting...".PadRight(left))
        false

let promptTwoFactorCode email =
    printf $"Enter verification code that was sent to %s{email}: "
    Console.ReadLine()
let promptUserPass() =
    printfn "Enter Frontier credentials"
    printf "Username (Email): "
    let username = Console.ReadLine()
    printf "Password: "
    let password = readPassword() |> Cobra.encrypt |> Result.defaultValue ""
    username, password

let promptForProductsToUpdate (products: ProductDetails array) =
    if products.Length > 0 then
        printfn $"Select product(s) to update (eg: \"1\", \"1 2 3\") (default=None):"
        products
        |> Array.indexed
        |> Array.iter (fun (i, product) -> printfn $"%i{i + 1}) %s{product.Name}")
            
        let rec readInput() =
            let userInput = Console.ReadLine()
            
            if String.IsNullOrWhiteSpace(userInput) then
                [||]
            else
                let selection =
                    userInput
                    |> Regex.split @"\D+"
                    |> Array.choose (fun d ->
                        if String.IsNullOrEmpty(d) then
                            None
                        else
                            match Int32.Parse(d) with
                            | n when n > 0 && n < products.Length -> Some n
                            | _ -> None)
                    |> Array.map (fun i -> products.[i - 1])
                if selection.Length > 0 then
                    selection
                else
                    printfn "Invalid selection"
                    readInput()
        readInput()
    else
        [||]
        
let availableProductsDisplay (products: Product list) =
    let max (f: ProductDetails -> string) =
        if products.Length = 0 then
            0
        else
            products
            |> List.map (function | Playable p | RequiresUpdate p -> (f(p)).Length | _ -> 0)
            |> List.max
    let maxName = max (fun p -> p.Name)
    let maxSku = max (fun p -> p.Sku)
    let map msg (p: ProductDetails) = $"{p.Name.PadRight(maxName)} {p.Sku.PadRight(maxSku)} %s{msg}" 
    let availableProducts =
        products
        |> List.choose (function | Playable p -> map "Up to Date" p |> Some
                                 | RequiresUpdate p -> map "Requires Update" p |> Some
                                 | Missing _ | Product.Unknown _ -> None)
    match availableProducts with
    | [] -> "None"
    | p -> String.Join(Environment.NewLine + "\t", p)
    
let productDownloadIndicator barLength (p: DownloadProgress) =
    let total = p.TotalBytes |> Int64.toFriendlyByteString
    let speed =  (p.BytesSoFar / (int64 p.Elapsed.TotalMilliseconds) * 1000L |> Int64.toFriendlyByteString).PadLeft(6)
    let percent = float p.BytesSoFar / float p.TotalBytes
    let blocks = int (float barLength * percent)
    let bar = String.replicate blocks "#" + String.replicate (barLength - blocks) "-"
    Console.Write($"\r\tDownloading %s{total} %s{speed}/s [%s{bar}] {percent:P0}")
    
let productHashIndicator barLength maxDigits totalFiles (p: int) =
    let percent = float p / float totalFiles
    let blocks = int (float barLength * percent)
    let bar = String.replicate blocks "#" + String.replicate (barLength - blocks) "-"
    let file = p.ToString().PadLeft(maxDigits)
    Console.Write $"\r\tChecking file %s{file} of %i{totalFiles} [%s{bar}] {percent:P0}"
    
[<RequireQualifiedAccess>]
type Progress<'T>(handler: 'T -> unit) =
    inherit SampledProgress<'T>(TimeSpan.FromMilliseconds(500), handler, fun () -> Console.WriteLine())