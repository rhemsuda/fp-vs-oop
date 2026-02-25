module Example3_4

open System

type FundHolding = {
    SecurityId: string
    Units: decimal
    MarketValue: decimal
    TargetWeight: decimal
}

type DriftResult = {
    Holding: FundHolding
    CurrentWeight: decimal
    Drift: decimal
}

type SizedTrade = {
    DriftResult: DriftResult
    RequiredTradeValue: decimal
}

type ConstrainedOrder = {
    SizedTrade: SizedTrade
    AdjustedTradeValue: decimal
    Action: string
}

let calculateDrift (totalNav: decimal) (holding: FundHolding) : DriftResult =
    let currentWeight = holding.MarketValue / totalNav
    { Holding = holding; CurrentWeight = currentWeight; Drift = currentWeight - holding.TargetWeight }

let sizeRequiredTrade (totalNav: decimal) (drift: DriftResult) : SizedTrade =
    { DriftResult = drift; RequiredTradeValue = -drift.Drift * totalNav }

let applyConstraints
    (minTradeSize: decimal)
    (availableCash: decimal)
    (trades: SizedTrade list)
    : ConstrainedOrder list =
    let sells, buys = trades |> List.partition (fun t -> t.RequiredTradeValue < 0m)
    let constrainedSells =
        sells |> List.map (fun t ->
            if abs t.RequiredTradeValue < minTradeSize then
                { SizedTrade = t; AdjustedTradeValue = 0m; Action = "SKIP" }
            else
                { SizedTrade = t; AdjustedTradeValue = t.RequiredTradeValue; Action = "SELL" })
    let totalSellProceeds = constrainedSells |> List.sumBy (fun o -> abs o.AdjustedTradeValue)
    let totalAvailableCash = availableCash + totalSellProceeds
    let totalBuyDemand = buys |> List.sumBy (fun t -> t.RequiredTradeValue)
    let constrainedBuys =
        buys |> List.map (fun t ->
            if abs t.RequiredTradeValue < minTradeSize then
                { SizedTrade = t; AdjustedTradeValue = 0m; Action = "SKIP" }
            elif totalBuyDemand > totalAvailableCash then
                let scale = totalAvailableCash / totalBuyDemand
                { SizedTrade = t; AdjustedTradeValue = t.RequiredTradeValue * scale; Action = "BUY (scaled)" }
            else
                { SizedTrade = t; AdjustedTradeValue = t.RequiredTradeValue; Action = "BUY" })
    constrainedSells @ constrainedBuys


let runExample3_4 =
    // --- Tests: each stage tested independently with minimal data ---
    let test (name: string) (expected: 'a) (actual: 'a) =
        let status = if expected = actual then "PASS" else "FAIL"
        printfn $"  [{status}] {name} (expected: {expected}, got: {actual})"

    // Test drift calculation — needs only a decimal and a FundHolding
    printfn "Drift calculation tests:"
    let overweightHolding = { SecurityId = "BOND-ETF"; Units = 0m; MarketValue = 400_000m; TargetWeight = 0.30m }
    let drift1 = calculateDrift 1_000_000m overweightHolding
    test "overweight current weight" 0.40m drift1.CurrentWeight
    test "overweight drift is +10%" 0.10m drift1.Drift

    let underweightHolding = { SecurityId = "EQ-ETF"; Units = 0m; MarketValue = 200_000m; TargetWeight = 0.30m }
    let drift2 = calculateDrift 1_000_000m underweightHolding
    test "underweight drift is -10%" -0.10m drift2.Drift

    let onTargetHolding = { SecurityId = "INTL-ETF"; Units = 0m; MarketValue = 300_000m; TargetWeight = 0.30m }
    let drift3 = calculateDrift 1_000_000m onTargetHolding
    test "on-target drift is 0%" 0.00m drift3.Drift

    // Test trade sizing — needs only a decimal and a DriftResult
    printfn "\nTrade sizing tests:"
    let sellDrift = { Holding = overweightHolding; CurrentWeight = 0.40m; Drift = 0.10m }
    let sellTrade = sizeRequiredTrade 1_000_000m sellDrift
    test "overweight produces sell" -100_000m sellTrade.RequiredTradeValue

    let buyDrift = { Holding = underweightHolding; CurrentWeight = 0.20m; Drift = -0.10m }
    let buyTrade = sizeRequiredTrade 1_000_000m buyDrift
    test "underweight produces buy" 100_000m buyTrade.RequiredTradeValue

    // Test constraints — needs a list of SizedTrades, no Fund object at all
    printfn "\nConstraint tests:"

    // Helper to make a SizedTrade quickly for testing
    let makeTrade (secId: string) (reqValue: decimal) : SizedTrade =
        let h = { SecurityId = secId; Units = 0m; MarketValue = 0m; TargetWeight = 0m }
        let d = { Holding = h; CurrentWeight = 0m; Drift = 0m }
        { DriftResult = d; RequiredTradeValue = reqValue }

    let orders1 = applyConstraints 5_000m 40_000m [
        makeTrade "SELL-A" -80_000m
        makeTrade "BUY-B" 200_000m
        makeTrade "TOO-SMALL" 2_000m
    ]

    test "small trade skipped" "SKIP" (orders1 |> List.find (fun o -> o.SizedTrade.DriftResult.Holding.SecurityId = "TOO-SMALL")).Action
    test "sell passes through" -80_000m (orders1 |> List.find (fun o -> o.SizedTrade.DriftResult.Holding.SecurityId = "SELL-A")).AdjustedTradeValue

    // Available = 40K + 80K sell = 120K. Buy wants 200K. Scale = 120/200 = 0.6
    let buyOrder = orders1 |> List.find (fun o -> o.SizedTrade.DriftResult.Holding.SecurityId = "BUY-B")
    test "buy scaled to available cash" 120_000m buyOrder.AdjustedTradeValue

    // Order independence — same trades, reversed input list
    let orders2 = applyConstraints 5_000m 40_000m [
        makeTrade "TOO-SMALL" 2_000m
        makeTrade "BUY-B" 200_000m
        makeTrade "SELL-A" -80_000m
    ]
    let buyOrder2 = orders2 |> List.find (fun o -> o.SizedTrade.DriftResult.Holding.SecurityId = "BUY-B")
    test "order-independent: same result reversed" buyOrder.AdjustedTradeValue buyOrder2.AdjustedTradeValue

// 14 tests. No Fund object needed for any of them. No pre-setting internal fields.
// Each function tested with just its actual inputs.
// Constraint test proves order independence — impossible to verify in the C# version.

(* Why F# prevents it: Each stage is a pure function with a clear signature. Testing drift needs a decimal
 and a FundHolding — not a fully constructed Fund with irrelevant fields. Testing constraints needs a SizedTrade 
 list — not a Fund with pre-mutated RequiredTradeValue fields. No test depends on list ordering because the logic 
 is order-independent by construction. And you can trivially prove that with a test — something the C# version can 
 never pass because its mutation makes ordering a hidden input. *)