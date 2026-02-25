module Example3_3

open System

type FundHolding = {
    SecurityId: string
    Units: decimal
    MarketValue: decimal
    TargetWeight: decimal
}

type Fund = {
    FundId: string
    Holdings: FundHolding list
    TotalNav: decimal
    CashBalance: decimal
    MinCashReserve: decimal
    MinTradeSize: decimal
}

// Each stage produces a DISTINCT type — you can inspect any stage independently

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
    let sells, buys =
        trades
        |> List.partition (fun t -> t.RequiredTradeValue < 0m)

    let constrainedSells =
        sells |> List.map (fun t ->
            if abs t.RequiredTradeValue < minTradeSize then
                { SizedTrade = t; AdjustedTradeValue = 0m; Action = "SKIP" }
            else
                { SizedTrade = t; AdjustedTradeValue = t.RequiredTradeValue; Action = "SELL" })

    let totalSellProceeds = constrainedSells |> List.sumBy (fun o -> abs o.AdjustedTradeValue)
    let totalAvailableCash = availableCash + totalSellProceeds

    // Buys are proportionally scaled to available cash — ORDER INDEPENDENT
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

let rebalance (fund: Fund) : ConstrainedOrder list =
    let drifts = fund.Holdings |> List.map (calculateDrift fund.TotalNav)
    let trades = drifts |> List.map (sizeRequiredTrade fund.TotalNav)
    let availableCash = fund.CashBalance - fund.MinCashReserve
    applyConstraints fund.MinTradeSize availableCash trades

// --- Usage: debugging is local ---

let fund = {
    FundId = "MF-BALANCED-001"
    TotalNav = 10_000_000m
    CashBalance = 200_000m
    MinCashReserve = 100_000m
    MinTradeSize = 5_000m
    Holdings = [
        { SecurityId = "CDN-BOND-ETF"; Units = 50_000m; MarketValue = 3_200_000m; TargetWeight = 0.30m }
        { SecurityId = "CDN-EQ-ETF";   Units = 30_000m; MarketValue = 2_800_000m; TargetWeight = 0.30m }
        { SecurityId = "US-EQ-ETF";    Units = 20_000m; MarketValue = 2_500_000m; TargetWeight = 0.25m }
        { SecurityId = "INTL-EQ-ETF";  Units = 15_000m; MarketValue = 1_300_000m; TargetWeight = 0.15m }
    ]
}



let runExample3_3 =
    let orders = rebalance fund

    printfn "Rebalance orders:"
    for o in orders do
        printfn $"  {o.SizedTrade.DriftResult.Holding.SecurityId}: " +
                $"drift={o.SizedTrade.DriftResult.Drift:P2}, " +
                $"required={o.SizedTrade.RequiredTradeValue:C}, " +
                $"adjusted={o.AdjustedTradeValue:C} [{o.Action}]"

    // Debugging: if INTL-EQ-ETF adjusted value looks wrong, inspect each stage:
    printfn "\nDebug INTL-EQ-ETF:"
    let intlDrift = calculateDrift fund.TotalNav (fund.Holdings |> List.find (fun h -> h.SecurityId = "INTL-EQ-ETF"))
    printfn $"  Drift stage: currentWeight={intlDrift.CurrentWeight:P2}, drift={intlDrift.Drift:P2}"
    let intlTrade = sizeRequiredTrade fund.TotalNav intlDrift
    printfn $"  Sizing stage: requiredTradeValue={intlTrade.RequiredTradeValue:C}"
    // Each stage is a pure function. Call it with the same inputs, get the same output.
    // No need to trace mutations through the entire pipeline.

    // Order-independence: reorder holdings, get same results
    let reorderedFund = { fund with Holdings = List.rev fund.Holdings }
    let reorderedOrders = rebalance reorderedFund

    printfn "\nReordered holdings — same results:"
    let sortedOriginal = orders |> List.sortBy (fun o -> o.SizedTrade.DriftResult.Holding.SecurityId)
    let sortedReordered = reorderedOrders |> List.sortBy (fun o -> o.SizedTrade.DriftResult.Holding.SecurityId)
    for (o1, o2) in List.zip sortedOriginal sortedReordered do
        let name = o1.SizedTrade.DriftResult.Holding.SecurityId
        printfn $"  {name}: {o1.AdjustedTradeValue:C} vs {o2.AdjustedTradeValue:C} — match={o1.AdjustedTradeValue = o2.AdjustedTradeValue}"


(* 
Why F# prevents it: Each stage takes explicit inputs and produces a new typed output. 
calculateDrift takes decimal -> FundHolding -> DriftResult. You can call it in isolation,
inspect its output, and verify it without running the whole pipeline. The constraint logic 
is order-independent because buys are proportionally scaled rather than first-come-first-served 
via mutation. Reordering the holdings list produces identical results — something the C# version 
cannot guarantee because its CashBalance mutation inside the loop makes iteration order a hidden input. 
*)