module Example1_3

open System

type Position = {
    Ticker: string
    Quantity: decimal
    MarketValue: decimal
}

type Portfolio = {
    Name: string
    Positions: Position list
}

let updateMarketValues (getPrice: string -> decimal) (positions: Position list) : Position list =
    positions
    |> List.map (fun pos ->
        { pos with MarketValue = pos.Quantity * getPrice pos.Ticker })
    // Returns a NEW list of NEW position records — originals untouched

// --- Usage: each portfolio gets independent valuations ---

let getLivePrice (ticker: string) : decimal =
    match ticker with
    | "AAPL" -> 185.50m
    | "MSFT" -> 421.00m
    | _ -> 100.00m

let getDelayedPrice (ticker: string) : decimal =
    match ticker with
    | "AAPL" -> 184.00m
    | "MSFT" -> 419.50m
    | _ -> 99.00m

let sharedPositions = [
    { Ticker = "AAPL"; Quantity = 1000m; MarketValue = 0m }
    { Ticker = "MSFT"; Quantity = 500m; MarketValue = 0m }
]

let portfolioA = { Name = "Fund A"; Positions = sharedPositions }
let portfolioB = { Name = "Fund B"; Positions = sharedPositions }

// Each computation produces independent results
let portfolioAValued =
    async { return updateMarketValues getLivePrice portfolioA.Positions }
    |> Async.StartAsTask

let portfolioBValued =
    async { return updateMarketValues getDelayedPrice portfolioB.Positions }
    |> Async.StartAsTask

let fundAResults = portfolioAValued.Result
let fundBResults = portfolioBValued.Result

let runExample1_3 =
    // Original sharedPositions are untouched — still have MarketValue = 0
    for pos in sharedPositions do
        printfn $"Original {pos.Ticker}: MarketValue = {pos.MarketValue}"
    for pos in fundAResults do
        printfn $"Fund A {pos.Ticker}: MarketValue = {pos.MarketValue}"
    for pos in fundBResults do
        printfn $"Fund B {pos.Ticker}: MarketValue = {pos.MarketValue}"

// Even though portfolioA and portfolioB share position records,
// { pos with ... } creates a COPY. The original is never modified.
// No race condition is possible — each task produces an entirely
// independent result list.
// Attempting `pos.MarketValue <- newValue` is a compiler error.