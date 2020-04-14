module Tests.EventLog

open System
open System.Collections
open Expecto
open EdLauncher

type Entry =
    | Empty
    | String of str: string
    | Date of theDate: DateTime
    | List of theList: string list
    | Collection of col: ICollection

[<Tests>]
let tests =
    let now = DateTime(2020, 01, 01, 10, 10, 10)
    let getNow = fun () -> now 
    let line action other = sprintf @"20200101/101010: {""action"":""%s"",%s""date"":""20200101"",""time"":""101010""} ;%s" action other Environment.NewLine
    
    testList "Event Log" [
        test "Entry of nothing to string produces correct value" {
            let entry = Entry.Empty
            let expected = line "Empty" ""
            let actual = EventLog.entryToString getNow entry
            
            Expect.equal actual expected ""
        }
        test "Entry of string to string produces correct value" {
            let entry = Entry.String ("test")
            let expected = line "String" @"""str"":""test"","
            let actual = EventLog.entryToString getNow entry
            
            Expect.equal actual expected ""
        }
        test "Entry of datetime to string produces correct value" {
            let entry = Entry.Date (now.AddDays(1.))
            let expected = line "Date" @"""theDate"":""1/2/2020 10:10:10 AM"","
            let actual = EventLog.entryToString getNow entry
            
            Expect.equal actual expected ""
        }
        test "Entry of list to string produces correct value" {
            let entry = Entry.List [ "asdf"; "fdsa" ]
            let expected = line "List" @"""theList"":""[asdf, fdsa]"","
            let actual = EventLog.entryToString getNow entry
            
            Expect.equal actual expected ""
        }
        test "Entry of collection to string produces correct value" {
            let entry = Entry.Collection [| "asdf"; "fdsa" |]
            let expected = line "Collection" @"""col"":""[asdf, fdsa]"","
            let actual = EventLog.entryToString getNow entry
            
            Expect.equal actual expected ""
        }
    ]

