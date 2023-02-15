module MinEdLauncher.Tests.Extensions

open System
open Expecto
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Reflection

module Expect =
    let notStringContains (subject : string) (substring : string) message =
        if (subject.Contains(substring)) then
            failtest $"%s{message}. Expected subject string '%s{subject}' to not contain substring '%s{substring}'."
            
    let stringEqual (actual: string) (expected: string) comparisonType message =
        if not (String.Equals(actual, expected, comparisonType)) then
            failtest $"%s{message}. Actual value was %s{actual} but had expected it to be %s{expected}."

    let notContainsString collection (substr: string) comparer message =
        if Seq.exists (fun (x: string) -> x.Contains(substr, comparer)) collection then
            failtest $"%s{message}. Expected %A{collection} to not contain match for %s{substr}."
    
    let isUnionCase (actual: 'T) expr message =
        // https://stackoverflow.com/a/3365393
        let isCase (c : Expr<_ -> 'T>)  = 
            match c with
            | Lambda (_, NewUnionCase(uci, _)) ->
                let tagReader = FSharpValue.PreComputeUnionTagReader(uci.DeclaringType)
                fun (v : 'T) -> (tagReader v) = uci.Tag
            | _ -> failwith "Invalid expression"
        
        if not (isCase expr actual) then
            let name =
                match expr with
                | Lambda (_, NewUnionCase(uci, _)) -> uci.Name
                | _ -> failwith "Invalid expression"
            failtest $"%s{message}. Expected %A{actual} to be of type '%s{name}'."