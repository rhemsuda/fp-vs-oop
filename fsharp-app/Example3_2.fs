module Example3_2

open System.Threading.Tasks

type Trade = { Ticker: string; RealizedPnl: decimal }

type Desk = { Name: string; Trades: Trade list }

let calculateDeskPnl (trades: Trade list) : decimal =
    trades |> List.sumBy (fun t -> t.RealizedPnl)

let aggregatePnl (desks: Desk list) : Map<string, decimal> =
    desks
    |> List.map (fun desk ->
        async { return desk.Name, calculateDeskPnl desk.Trades })
    |> Async.Parallel     // safe: no shared mutable state
    |> Async.RunSynchronously
    |> Map.ofArray        // atomic: map is created all at once

let totalPnl (pnlByDesk: Map<string, decimal>) : decimal =
    pnlByDesk |> Map.toSeq |> Seq.sumBy snd

// --- Usage ---

let desks : Desk list = [
    { Name = "Equities"
      Trades = [
          { Ticker = "AAPL"; RealizedPnl = 50_000m }
          { Ticker = "MSFT"; RealizedPnl = -12_000m }
      ] }
    { Name = "Fixed Income"
      Trades = [
          { Ticker = "US10Y"; RealizedPnl = 200_000m }
          { Ticker = "US2Y"; RealizedPnl = -30_000m }
      ] }
    { Name = "Derivatives"
      Trades = [
          { Ticker = "SPY_PUT"; RealizedPnl = 80_000m }
          { Ticker = "VIX_CALL"; RealizedPnl = -5_000m }
      ] }
]


let runExample3_2 =
    let result = aggregatePnl desks

    printfn "P&L by desk:"
    result |> Map.iter (fun desk pnl -> printfn $"  {desk}: {pnl:C}")
    printfn $"Total P&L: {totalPnl result:C}"


