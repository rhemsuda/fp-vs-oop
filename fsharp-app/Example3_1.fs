module Example3_1

type Trade = {
    TradeId: string
    Ticker: string
    Quantity: decimal
}

type PricedTrade = {
    Trade: Trade
    MarketPrice: decimal
}

type RiskCheckedTrade = {
    PricedTrade: PricedTrade
    Exposure: decimal
    Approved: bool
}

// Each function takes a DIFFERENT type and returns a DIFFERENT type.
// The pipeline stages are encoded in the type signatures.

let validate (trade: Trade) : Result<Trade, string> =
    if trade.Quantity > 0m && trade.Ticker <> ""
    then Ok trade
    else Error $"Invalid trade {trade.TradeId}: bad quantity or ticker"

// `price` takes Trade, not "any object with a Ticker property"
let price (getMarketPrice: string -> decimal) (trade: Trade) : PricedTrade =
    { Trade = trade; MarketPrice = getMarketPrice trade.Ticker }

// `checkRisk` takes PricedTrade, not Trade — you MUST price first
let checkRisk (maxExposure: decimal) (pricedTrade: PricedTrade) : RiskCheckedTrade =
    let exposure = pricedTrade.Trade.Quantity * pricedTrade.MarketPrice
    { PricedTrade = pricedTrade; Exposure = exposure; Approved = exposure < maxExposure }

// The composed pipeline: types enforce the ordering
let processTrade
    (getPrice: string -> decimal)
    (trade: Trade)
    : Result<RiskCheckedTrade, string> =
    match validate trade with
    | Error reason -> Error reason
    | Ok validTrade ->
        validTrade
        |> price getPrice
        |> checkRisk 1_000_000m
        |> Ok

// What the compiler PREVENTS:
//
// 1. Skipping validation:
//    trade |> price getPrice |> checkRisk 1_000_000m
//    This COMPILES — but only because price takes Trade, not ValidatedTrade.
//    If you want to enforce validation, wrap it:
//       type ValidatedTrade = ValidatedTrade of Trade
//    Now price must take ValidatedTrade, and the only way to get one is
//    through validate. F# makes this a 1-line type, not a ceremony.
//
// 2. Skipping pricing:
//    trade |> checkRisk 1_000_000m
//    COMPILER ERROR: checkRisk expects PricedTrade, got Trade.
//    You physically cannot risk-check an unpriced trade.
//
// 3. Wrong ordering:
//    trade |> checkRisk 1_000_000m |> price getPrice
//    COMPILER ERROR: checkRisk expects PricedTrade, price expects Trade.
//    Types don't align. This is not a warning — it won't compile.
//
// 4. Mutation:
//    { trade with Quantity = -100m }  -- creates a NEW record, original unchanged
//    trade.Quantity <- -100m          -- COMPILER ERROR: field is not mutable
//
// 5. Ignoring failure:
//    validate returns Result<Trade, string>. You MUST match on Ok/Error.
//    There is no null. There is no ! operator to suppress it.

// --- Usage ---

let getMarketPrice (ticker: string) : decimal =
    match ticker with
    | "AAPL" -> 185.00m
    | "MSFT" -> 420.00m
    | "RY.TO" -> 145.00m
    | _ -> 100.00m

let trades : Trade list = [
    { TradeId = "T-001"; Ticker = "AAPL"; Quantity = 100m }
    { TradeId = "T-002"; Ticker = "RY.TO"; Quantity = 5000m }
    { TradeId = "T-003"; Ticker = "MSFT"; Quantity = 5000m }    // exceeds risk limit
    { TradeId = "T-004"; Ticker = ""; Quantity = -50m }         // fails validation
]


// Each function is independently testable with zero setup:
//   validate { TradeId = "X"; Ticker = "AAPL"; Quantity = 100m }  -- returns Ok
//   validate { TradeId = "X"; Ticker = ""; Quantity = -1m }       -- returns Error
//   price getMarketPrice { TradeId = "X"; Ticker = "AAPL"; Quantity = 100m }
//      -- returns { Trade = ...; MarketPrice = 185.00m }
// No mocks. No interfaces. No DI container. Just values in, values out.
// . In F#, the types encode the pipeline stages (Trade -> PricedTrade -> RiskCheckedTrade), so the compiler rejects invalid orderings.

let runExample3_1 =
    let results = trades |> List.map (processTrade getMarketPrice)

    for result in results do
        match result with
        | Ok checked ->
            let status = if checked.Approved then "APPROVED" else "REJECTED (risk)"
            printfn $"{checked.PricedTrade.Trade.TradeId}: {status}, exposure = {checked.Exposure:C}"
        | Error reason ->
            printfn $"FAILED: {reason}"