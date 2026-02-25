module Example1_2

open System

type BacktestDay = {
    Date: DateTime
    Price: decimal
    PortfolioValue: decimal
}

// Pure calculation — no way to sneak in I/O
let calculateLotSize (maxExposure: decimal) (price: decimal) (portfolioValue: decimal) : int =
    int (portfolioValue * maxExposure / price)

// Live trading: I/O dependency is explicit in the type signature
let calculateLotSizeLive
    (getRiskLimit: string -> Async<decimal>)
    (ticker: string)
    (price: decimal)
    (portfolioValue: decimal) : Async<int> =
    async {
        let! maxExposure = getRiskLimit ticker  // DB call — visible in return type
        return calculateLotSize maxExposure price portfolioValue
    }

// --- Usage ---

// Simulate a live risk limit DB lookup
let getLiveRiskLimit (ticker: string) : Async<decimal> =
    async {
        return
            match ticker with
            | "AAPL" -> 0.05m   // current limit — tightened post-2022
            | "TSLA" -> 0.02m   // current limit — tightened post-2022
            | _ -> 0.03m
    }

// Live trading uses the async version — I/O is explicit
let liveLots =
    calculateLotSizeLive getLiveRiskLimit "AAPL" 185.00m 10_000_000m
    |> Async.RunSynchronously
printfn $"Live lots: {liveLots}"

// Historical limits as data — loaded once, not fetched per iteration
let historicalLimits : Map<DateTime, decimal> =
    [ DateTime(2019, 6, 15), 0.08m   // pre-drawdown: 8% limit
      DateTime(2020, 3, 20), 0.08m
      DateTime(2021, 11, 1), 0.08m
      DateTime(2022, 6, 15), 0.04m   // mid-drawdown: limit tightened to 4%
      DateTime(2023, 1, 10), 0.02m ] // post-drawdown: tightened to 2%
    |> Map.ofList

let backtestDays : BacktestDay list = [
    { Date = DateTime(2019, 6, 15); Price = 50.00m; PortfolioValue = 10_000_000m }
    { Date = DateTime(2020, 3, 20); Price = 30.00m; PortfolioValue = 8_000_000m }
    { Date = DateTime(2021, 11, 1); Price = 150.00m; PortfolioValue = 15_000_000m }
    { Date = DateTime(2022, 6, 15); Price = 130.00m; PortfolioValue = 12_000_000m }
    { Date = DateTime(2023, 1, 10); Price = 140.00m; PortfolioValue = 11_000_000m }
]

// Backtesting uses the PURE function with historical limits as data
let backtestResults =
    backtestDays
    |> List.map (fun day ->
        let limit = historicalLimits |> Map.find day.Date
        let lots = calculateLotSize limit day.Price day.PortfolioValue
        printfn $"""{day.Date.ToString("yyyy-MM-dd")}: TSLA x {lots} lots (limit: {limit:P0})"""
        day.Date, lots)

let runExample1_2 =
    backtestResults

// The critical difference: a developer CANNOT accidentally use
// calculateLotSizeLive in a backtest loop without confronting its
// Async<int> return type. The compiler forces them to handle the
// async context, which immediately raises the question: "Why is
// my backtest doing async I/O?" That question leads directly to
// discovering the look-ahead bias.