module MinEdLauncher.Tests.Extensions

open System
open Expecto

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