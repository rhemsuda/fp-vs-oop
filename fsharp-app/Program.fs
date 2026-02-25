open System
open Example1_2
open Example1_3
open Example3_1
open Example3_2
open Example3_3

runExample Example1_1.runExample1_1 "Example #1.1: Referential Transparency"

printfn "\n\nExample #1.2: Purity (F#)"
runExample1_2

printfn "\n\nExample #1.3: Immutability (F#)"
runExample1_3

printfn "\n\nExample #3.1: Composability (F#)"
runExample3_1

printfn "\n\nExample #3.2: Concurrency & Parallelism Safety (F#)"
runExample3_2

printfn "\n\nExample #3.3: Reasoning, Debugging, & Refactoring (F#)"
runExample3_3

printfn "\n\nExample #3.4: Testing (F#)"
runExample3_4


let runExample (action: unit -> unit) -> (label: string) : unit =
    printfn "\n\n%s" String('-', String.length label + 6)
    printfn "%s" label
    printfn "%s\n" String('-', String.length label + 6)
    action