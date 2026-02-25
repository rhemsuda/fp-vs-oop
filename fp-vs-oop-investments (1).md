# F# vs C# in Investments: Where OOP's Emergent Behaviour Bites

Real-world investment domain examples demonstrating how C#'s OOP permits subtle, production-breaking bugs that F#'s compiler structurally prevents.

---

## 1. Core Principles

### 1.1 Referential Transparency

**Scenario:** Portfolio valuation used in both risk reporting and order sizing.

#### C# — Hidden State Mutation Breaks Substitutability

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

public interface IPricingService
{
    decimal GetPrice(string ticker);
}

public class LivePricingService : IPricingService
{
    private readonly Random _jitter = new();

    public decimal GetPrice(string ticker)
    {
        // Simulates live price that moves between calls
        return ticker switch
        {
            "AAPL" => 185.00m + _jitter.Next(-200, 200) * 0.01m,
            "MSFT" => 420.00m + _jitter.Next(-300, 300) * 0.01m,
            _ => 100.00m
        };
    }
}

public class Position
{
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
}

public class Portfolio
{
    public List<Position> Positions { get; set; } = new();
}

public class PortfolioValuator
{
    private DateTime _lastPricedAt;
    private decimal _cachedNav;
    private readonly IPricingService _pricingService;

    public PortfolioValuator(IPricingService pricingService)
    {
        _pricingService = pricingService;
    }

    // Looks like a pure query — but it isn't.
    public decimal GetNetAssetValue(Portfolio portfolio)
    {
        var nav = portfolio.Positions
            .Sum(p => p.Quantity * _pricingService.GetPrice(p.Ticker));

        // Side effect: mutates internal state
        _lastPricedAt = DateTime.UtcNow;
        _cachedNav = nav;
        return nav;
    }

    // Another method reads that mutated state
    public bool IsStale() => (DateTime.UtcNow - _lastPricedAt).TotalMinutes > 5;
}

// --- Usage showing the bug ---
public class ReferentialTransparencyBug
{
    public static void Main()
    {
        var portfolio = new Portfolio
        {
            Positions = new List<Position>
            {
                new() { Ticker = "AAPL", Quantity = 1000 },
                new() { Ticker = "MSFT", Quantity = 500 }
            }
        };

        var valuator = new PortfolioValuator(new LivePricingService());

        var nav1 = valuator.GetNetAssetValue(portfolio);  // sets _lastPricedAt
        Console.WriteLine($"NAV: {nav1:C}");

        Thread.Sleep(6 * 60 * 1000); // Wait 6 minutes

        // Developer assumes IsStale() reflects their last explicit check.
        // But _lastPricedAt was silently set by nav1's call above.
        if (valuator.IsStale())
        {
            // Triggers unnecessary repricing, costing API credits and latency
            var nav2 = valuator.GetNetAssetValue(portfolio);
            Console.WriteLine($"Repriced NAV: {nav2:C}");
        }
    }
}
```

**The emergent problem:** `GetNetAssetValue` violates RT — you can't substitute its call with its return value because it also mutates `_lastPricedAt`. A developer reading only the call site has no idea ordering matters. In a trading system, this means risk limits computed with a "stale" flag that's actually reflecting a *different* valuation call's timestamp.

#### F# — RT by Construction

```fsharp
open System

type Position = { Ticker: string; Quantity: decimal }

type Portfolio = { Positions: Position list }

type ValuationResult = {
    Nav: decimal
    PricedAt: DateTime
    Prices: Map<string, decimal>
}

let getNetAssetValue (getPrice: string -> decimal) (portfolio: Portfolio) : ValuationResult =
    let prices =
        portfolio.Positions
        |> List.map (fun p -> p.Ticker, getPrice p.Ticker)
        |> Map.ofList

    let nav =
        prices
        |> Map.toSeq
        |> Seq.sumBy (fun (ticker, price) ->
            let pos = portfolio.Positions |> List.find (fun p -> p.Ticker = ticker)
            pos.Quantity * price)

    { Nav = nav; PricedAt = DateTime.UtcNow; Prices = prices }

let isStale (result: ValuationResult) =
    (DateTime.UtcNow - result.PricedAt).TotalMinutes > 5.0

// --- Usage: ValuationResult is a value. No hidden coupling. ---
let mockGetPrice (ticker: string) : decimal =
    match ticker with
    | "AAPL" -> 185.00m
    | "MSFT" -> 420.00m
    | _ -> 100.00m

let portfolio = {
    Positions = [
        { Ticker = "AAPL"; Quantity = 1000m }
        { Ticker = "MSFT"; Quantity = 500m }
    ]
}

let result = getNetAssetValue mockGetPrice portfolio
printfn $"NAV: {result.Nav:C}, Priced at: {result.PricedAt}"

// ... later ...
if isStale result then
    let freshResult = getNetAssetValue mockGetPrice portfolio
    // freshResult is a completely independent value — no shared state
    printfn $"Fresh NAV: {freshResult.Nav:C}, Priced at: {freshResult.PricedAt}"
```

**Why F# prevents it:** There's no mutable `_lastPricedAt` to silently couple two unrelated operations. The timestamp travels *with* the valuation as data. A developer can't accidentally create ordering dependencies because there's no hidden state to order against.

---

### 1.2 Purity

**Scenario:** Position sizing function used in both live trading and historical backtesting.

#### C# — Impure "Calculator" With Hidden Database Dependency

```csharp
using System;
using System.Collections.Generic;

public interface IRiskLimits
{
    decimal GetMaxExposure(string ticker); // Hits a live database
}

public class LiveRiskLimits : IRiskLimits
{
    public decimal GetMaxExposure(string ticker)
    {
        // In production, this queries a database.
        // Returns current limits — which were tightened after the 2022 drawdown.
        return ticker switch
        {
            "AAPL" => 0.05m,  // 5% max exposure — tightened from 10% post-2022
            "TSLA" => 0.02m,  // 2% max exposure — tightened from 8% post-2022
            _ => 0.03m
        };
    }
}

public class PositionSizer
{
    private readonly IRiskLimits _riskLimits;

    public PositionSizer(IRiskLimits riskLimits)
    {
        _riskLimits = riskLimits;
    }

    public int CalculateLotSize(string ticker, decimal price, decimal portfolioValue)
    {
        var maxExposure = _riskLimits.GetMaxExposure(ticker); // DB call — fetches LIVE limits
        var lots = (int)(portfolioValue * maxExposure / price);
        return lots;
    }
}

public struct BacktestDay
{
    public DateTime Date { get; set; }
    public decimal Price { get; set; }
    public decimal PortfolioValue { get; set; }
}

public class BacktestSimulation
{
    private readonly List<(string Ticker, int Lots, DateTime Date)> _positions = new();

    public void RecordPosition(string ticker, int lots, DateTime date)
    {
        _positions.Add((ticker, lots, date));
    }

    public void PrintSummary()
    {
        foreach (var (ticker, lots, date) in _positions)
            Console.WriteLine($"{date:yyyy-MM-dd}: {ticker} x {lots} lots");
    }
}

// --- Usage showing the look-ahead bias bug ---
public class PurityBug
{
    public static void Main()
    {
        // Live trading: works correctly. GetMaxExposure returns today's limits.
        var sizer = new PositionSizer(new LiveRiskLimits());
        var liveLots = sizer.CalculateLotSize("AAPL", 185.00m, 10_000_000m);
        Console.WriteLine($"Live lots: {liveLots}");

        // Backtesting: silently produces wrong results.
        // A quant reuses the same PositionSizer to simulate 2019–2023 performance.
        var backtestDays = new List<BacktestDay>
        {
            new() { Date = new DateTime(2019, 6, 15), Price = 50.00m, PortfolioValue = 10_000_000m },
            new() { Date = new DateTime(2020, 3, 20), Price = 30.00m, PortfolioValue = 8_000_000m },
            new() { Date = new DateTime(2021, 11, 1), Price = 150.00m, PortfolioValue = 15_000_000m },
            new() { Date = new DateTime(2022, 6, 15), Price = 130.00m, PortfolioValue = 12_000_000m },
            new() { Date = new DateTime(2023, 1, 10), Price = 140.00m, PortfolioValue = 11_000_000m },
        };

        var simulation = new BacktestSimulation();

        foreach (var day in backtestDays)
        {
            var lots = sizer.CalculateLotSize("TSLA", day.Price, day.PortfolioValue);
            simulation.RecordPosition("TSLA", lots, day.Date);
        }

        simulation.PrintSummary();

        // The method signature is: int CalculateLotSize(string, decimal, decimal)
        // Nothing tells the caller it reaches into a database.
        //
        // The bug: GetMaxExposure returns TODAY's risk limits — which were
        // tightened in 2022 after the fund took a large drawdown. The backtest
        // is applying post-drawdown limits (2% for TSLA) to pre-drawdown data.
        //
        // In 2019 the actual limit was 8%, so position sizes should be 4x larger.
        // The simulation shows the strategy would have AVOIDED the 2022 drawdown.
        // The quant presents this as evidence the strategy is robust.
        // In reality, it only "avoided" the drawdown because limits tightened
        // AFTER it happened. Look-ahead bias in every single position size.
    }
}
```

**The emergent problem:** The method *looks* like arithmetic but performs I/O. The C# compiler treats `decimal GetMaxExposure(...)` identically to pure computation. The method signature `int CalculateLotSize(string, decimal, decimal)` gives zero indication that calling it fetches external state — so a developer reusing it for backtesting has no reason to suspect the results are contaminated with future knowledge.

#### F# — Effects Are Visible in Types

```fsharp
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
        printfn $"{day.Date:yyyy-MM-dd}: TSLA x {lots} lots (limit: {limit:P0})"
        day.Date, lots)

// The critical difference: a developer CANNOT accidentally use
// calculateLotSizeLive in a backtest loop without confronting its
// Async<int> return type. The compiler forces them to handle the
// async context, which immediately raises the question: "Why is
// my backtest doing async I/O?" That question leads directly to
// discovering the look-ahead bias.
```

**Why F# prevents it:** The pure function `calculateLotSize` takes `decimal -> decimal -> decimal -> int`. You literally *cannot* perform I/O inside it without changing the return type to `Async<int>`, which the compiler would flag at every call site. The effectful version's type signature (`Async<int>`) screams "I do I/O" — a developer trying to use it in a tight backtest loop is forced to confront the async machinery, which makes the hidden database dependency impossible to overlook. More importantly, the pure version *requires* `maxExposure` as an explicit parameter, turning the data source from a hidden implementation detail into a conscious architectural choice at every call site.

---

### 1.3 Immutability

**Scenario:** Concurrent risk aggregation across multiple portfolios sharing position references.

#### C# — Shared Mutable Position Objects

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public interface IPricingService
{
    decimal GetPrice(string ticker);
}

public class LivePricer : IPricingService
{
    public decimal GetPrice(string ticker) => ticker switch
    {
        "AAPL" => 185.50m,
        "MSFT" => 421.00m,
        _ => 100.00m
    };
}

public class DelayedPricer : IPricingService
{
    public decimal GetPrice(string ticker) => ticker switch
    {
        "AAPL" => 184.00m,  // 15-minute delayed
        "MSFT" => 419.50m,
        _ => 99.00m
    };
}

public class Position
{
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal MarketValue { get; set; }  // mutable!
}

public class Portfolio
{
    public string Name { get; set; } = "";
    public List<Position> Positions { get; set; } = new();
}

public class RiskAggregator
{
    public void UpdateMarketValues(List<Position> positions, IPricingService pricer)
    {
        Parallel.ForEach(positions, pos =>
        {
            pos.MarketValue = pos.Quantity * pricer.GetPrice(pos.Ticker);
            // Mutates the object in place
        });
    }
}

// --- Usage showing the race condition bug ---
public class ImmutabilityBug
{
    public static void Main()
    {
        // Portfolio A and Portfolio B share Position objects
        // (common when a fund has sub-portfolios viewing the same book)
        var sharedAAPL = new Position { Ticker = "AAPL", Quantity = 1000 };
        var sharedMSFT = new Position { Ticker = "MSFT", Quantity = 500 };

        var portfolioA = new Portfolio
        {
            Name = "Fund A",
            Positions = new List<Position> { sharedAAPL, sharedMSFT }
        };

        var portfolioB = new Portfolio
        {
            Name = "Fund B",
            Positions = new List<Position> { sharedAAPL, sharedMSFT }  // same references!
        };

        var riskAggregator = new RiskAggregator();
        var livePricer = new LivePricer();
        var delayedPricer = new DelayedPricer();

        // Two risk threads run simultaneously
        var taskA = Task.Run(() =>
            riskAggregator.UpdateMarketValues(portfolioA.Positions, livePricer));
        var taskB = Task.Run(() =>
            riskAggregator.UpdateMarketValues(portfolioB.Positions, delayedPricer));

        Task.WaitAll(taskA, taskB);

        // sharedAAPL.MarketValue is now a race condition:
        // - Could reflect live price 185.50 * 1000 = 185,500 (from Fund A's thread)
        // - Could reflect delayed price 184.00 * 1000 = 184,000 (from Fund B's thread)
        // - Could reflect a torn read (partially written decimal)

        Console.WriteLine($"AAPL MarketValue: {sharedAAPL.MarketValue}");
        Console.WriteLine($"MSFT MarketValue: {sharedMSFT.MarketValue}");

        // Risk report shows Fund A with delayed prices while Fund B shows
        // live prices. Compliance flags a $1,500 discrepancy that doesn't
        // actually exist — it's just a race condition artifact.
    }
}
```

**The emergent problem:** The `set` accessor on `MarketValue` means any thread with a reference can mutate the object. The shared reference between portfolios is invisible at the type level — nothing stops two risk calculations from stomping on each other's results through the same object.

#### F# — Immutable Records Force Explicit Data Flow

```fsharp
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
```

**Why F# prevents it:** Record fields are immutable by default. `{ pos with MarketValue = ... }` is syntactic sugar for creating a new record — the original is structurally frozen. You can't create a race condition on data that no thread can write to. The compiler error on mutation isn't a lint warning you can suppress — it's a hard type error.

---

## 2. Language Features

### 2.1 Higher-Order Functions

**Scenario:** Building a trade filtering and transformation pipeline for order routing.

#### C# — Lambda Closures Capturing Mutable State

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public class Trade
{
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }
}

public class Order
{
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal RunningExposure { get; set; }
}

public class OrderRouter
{
    private decimal _totalExposure = 0m;  // accumulator

    public List<Order> ProcessTrades(List<Trade> trades)
    {
        var orders = trades
            .Where(t => t.Value > 10_000m)
            .Select(t =>
            {
                // Lambda closes over mutable field
                _totalExposure += t.Value;  // SIDE EFFECT in Select!
                return new Order
                {
                    Ticker = t.Ticker,
                    Quantity = t.Quantity,
                    RunningExposure = _totalExposure  // depends on execution order
                };
            })
            .Where(o => o.RunningExposure < 1_000_000m)
            .ToList();

        return orders;
    }
}

// --- Usage showing the bug ---
public class HigherOrderBug
{
    public static void Main()
    {
        var router = new OrderRouter();

        var morningTrades = new List<Trade>
        {
            new() { Ticker = "AAPL", Quantity = 100, Value = 50_000m },
            new() { Ticker = "MSFT", Quantity = 200, Value = 150_000m },
            new() { Ticker = "GOOG", Quantity = 50, Value = 300_000m },
        };

        var afternoonTrades = new List<Trade>
        {
            new() { Ticker = "TSLA", Quantity = 75, Value = 400_000m },
            new() { Ticker = "AMZN", Quantity = 120, Value = 250_000m },
        };

        var batch1 = router.ProcessTrades(morningTrades);
        Console.WriteLine($"Batch 1: {batch1.Count} orders, " +
            $"exposure after batch 1: {batch1.LastOrDefault()?.RunningExposure ?? 0:C}");

        // Bug: _totalExposure persists from batch 1!
        var batch2 = router.ProcessTrades(afternoonTrades);
        Console.WriteLine($"Batch 2: {batch2.Count} orders, " +
            $"exposure after batch 2: {batch2.LastOrDefault()?.RunningExposure ?? 0:C}");

        // Afternoon trades incorrectly hit the exposure limit because
        // _totalExposure carried over from the morning batch.
        // Also: if you add a .Count() call before .ToList() in ProcessTrades,
        // _totalExposure doubles because LINQ re-evaluates the Select.
    }
}
```

**The emergent problem:** C# allows side effects inside LINQ lambdas with no compiler warning. The `Select` is supposed to be a pure mapping operation, but the closure over `_totalExposure` makes it stateful. Lazy evaluation, multiple enumeration, and parallel execution all produce different results from the same input.

#### F# — Pipeline Composition Without Hidden State

```fsharp
type Trade = {
    Ticker: string
    Quantity: decimal
    Value: decimal
}

type Order = {
    Ticker: string
    Quantity: decimal
    RunningExposure: decimal
}

type ExposureState = { TotalExposure: decimal; Orders: Order list }

let processTradesWithExposure (trades: Trade list) : Order list =
    trades
    |> List.filter (fun t -> t.Value > 10_000m)
    |> List.fold (fun state trade ->
        let newExposure = state.TotalExposure + trade.Value
        if newExposure < 1_000_000m then
            let order = {
                Ticker = trade.Ticker
                Quantity = trade.Quantity
                RunningExposure = newExposure
            }
            { TotalExposure = newExposure; Orders = order :: state.Orders }
        else
            state  // skip trade, exposure unchanged
    ) { TotalExposure = 0m; Orders = [] }
    |> fun state -> List.rev state.Orders

// --- Usage: each batch is independent ---

let morningTrades : Trade list = [
    { Ticker = "AAPL"; Quantity = 100m; Value = 50_000m }
    { Ticker = "MSFT"; Quantity = 200m; Value = 150_000m }
    { Ticker = "GOOG"; Quantity = 50m; Value = 300_000m }
]

let afternoonTrades : Trade list = [
    { Ticker = "TSLA"; Quantity = 75m; Value = 400_000m }
    { Ticker = "AMZN"; Quantity = 120m; Value = 250_000m }
]

// No mutable variable to accidentally persist between batches.
// List.fold has defined left-to-right ordering — no ambiguity.
let batch1 = processTradesWithExposure morningTrades
let batch2 = processTradesWithExposure afternoonTrades  // starts fresh at 0m

printfn $"Batch 1: {batch1.Length} orders"
for o in batch1 do printfn $"  {o.Ticker}: exposure {o.RunningExposure:C}"

printfn $"Batch 2: {batch2.Length} orders"
for o in batch2 do printfn $"  {o.Ticker}: exposure {o.RunningExposure:C}"
```

**Why F# prevents it:** `List.map` in F# returns a new list — attempting to mutate a captured variable inside it requires a `mutable` binding, which the compiler forces you to declare explicitly. The idiomatic approach (fold) makes the state threading *visible in the function signature*. There's no implicit accumulator hiding in a closure.

---

### 2.2 Algebraic Data Types

**Scenario:** Modeling order execution states in a trading system.

#### C# — Null-Based State With Incomplete Hierarchies

```csharp
using System;

public class ExecutionReport
{
    public string OrderId { get; set; } = "";
    public string Status { get; set; } = "";       // "Filled", "Partial", "Rejected"
    public decimal? FilledQuantity { get; set; }    // null if rejected
    public decimal? FilledPrice { get; set; }       // null if rejected
    public string? RejectionReason { get; set; }    // null if filled
    public decimal? RemainingQuantity { get; set; } // null if fully filled
}

public class SlippageCalculator
{
    public decimal CalculateSlippage(ExecutionReport report, decimal expectedPrice)
    {
        // Developer checks Status but forgets a case
        if (report.Status == "Filled")
        {
            return report.FilledPrice!.Value - expectedPrice;  // .Value can throw!
        }
        else if (report.Status == "Partial")
        {
            return report.FilledPrice!.Value - expectedPrice;
        }
        // Forgot "Rejected" — falls through, returns 0
        // Also: what about "PartiallyRejected"? "Cancelled"? "Expired"?
        return 0m;
    }
}

// --- Usage showing the bugs ---
public class AdtBug
{
    public static void Main()
    {
        var calculator = new SlippageCalculator();

        // Bug 1: Status typo compiles fine
        var typoReport = new ExecutionReport
        {
            OrderId = "ORD-001",
            Status = "Fileld",  // typo! No compiler error.
            FilledPrice = 185.50m,
            FilledQuantity = 1000
        };
        var slippage1 = calculator.CalculateSlippage(typoReport, 185.00m);
        Console.WriteLine($"Typo report slippage: {slippage1}");  // 0 — silently wrong

        // Bug 2: Illegal state combination — Filled but null price
        var brokenReport = new ExecutionReport
        {
            OrderId = "ORD-002",
            Status = "Filled",
            FilledPrice = null,  // should be impossible for Filled, but compiles
            FilledQuantity = 1000
        };
        try
        {
            var slippage2 = calculator.CalculateSlippage(brokenReport, 185.00m);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Runtime crash: {ex.Message}");  // Nullable has no value
        }

        // Bug 3: New status added later — no compiler warnings anywhere
        var expiredReport = new ExecutionReport
        {
            OrderId = "ORD-003",
            Status = "Expired"  // added by another developer
        };
        var slippage3 = calculator.CalculateSlippage(expiredReport, 185.00m);
        Console.WriteLine($"Expired report slippage: {slippage3}");  // 0 — silently wrong
    }
}
```

**The emergent problem:** The flat class with nullable fields allows *illegal state combinations* — e.g., `Status = "Filled"` with `FilledPrice = null`. The compiler can't verify that all states are handled or that field access is safe for a given state. New states added later silently fall through existing switches.

#### F# — Discriminated Unions Make Illegal States Unrepresentable

```fsharp
type ExecutionReport =
    | Filled of orderId: string * filledQty: decimal * filledPrice: decimal
    | PartialFill of orderId: string * filledQty: decimal * filledPrice: decimal * remainingQty: decimal
    | Rejected of orderId: string * reason: string
    | Expired of orderId: string

let calculateSlippage (expectedPrice: decimal) (report: ExecutionReport) : decimal option =
    match report with
    | Filled (_, _, filledPrice) -> Some (filledPrice - expectedPrice)
    | PartialFill (_, _, filledPrice, _) -> Some (filledPrice - expectedPrice)
    | Rejected _ -> None  // no slippage concept for rejections
    | Expired _ -> None
    // If you add a new case to ExecutionReport, THIS function
    // gets a compiler warning: "Incomplete pattern match"
    // You literally cannot forget to handle it.

// --- Usage: illegal states are impossible ---

let filledReport = Filled ("ORD-001", 1000m, 185.50m)
let partialReport = PartialFill ("ORD-002", 500m, 185.25m, 500m)
let rejectedReport = Rejected ("ORD-003", "Insufficient margin")
let expiredReport = Expired "ORD-004"

let reports = [ filledReport; partialReport; rejectedReport; expiredReport ]

for report in reports do
    match calculateSlippage 185.00m report with
    | Some slip -> printfn $"Slippage: {slip:F2}"
    | None -> printfn "No slippage (not a fill)"

// Cannot construct:
// - Filled with null price: decimal is not nullable
// - Filled without a price: the case requires all three fields
// - A misspelled status: DU cases are compiler-checked identifiers
// - Filled("ORD-001", 1000m, ???): must provide all fields or compiler error
```

**Why F# prevents it:** Each DU case carries exactly the data relevant to that state. There's no "Filled with null price" because the `Filled` case *requires* a `decimal`. The exhaustiveness check is a compiler warning (promotable to error with `<TreatWarningsAsErrors>`) — adding `| Cancelled of orderId: string * reason: string` immediately surfaces every `match` that needs updating.

---

### 2.3 Pattern Matching

**Scenario:** Routing orders to different execution venues based on instrument characteristics.

#### C# — Switch With Silent Fallthrough and No Exhaustiveness

```csharp
using System;

public enum AssetClass { Equity, FixedIncome, Derivative, Crypto }

public class Order
{
    public string Ticker { get; set; } = "";
    public decimal Value { get; set; }
    public AssetClass AssetClass { get; set; }
}

public class VenueRouter
{
    public string RouteOrder(Order order)
    {
        return order.AssetClass switch
        {
            AssetClass.Equity => "NYSE_ARCA",
            AssetClass.FixedIncome => "BOND_DESK",
            AssetClass.Derivative => "CBOE",
            // Forgot Crypto — compiler allows it with a default/discard
            _ => "DEFAULT_VENUE"  // silently catches everything else
        };
    }

    public decimal CalculateFee(Order order)
    {
        if (order.AssetClass == AssetClass.Equity && order.Value > 100_000)
            return order.Value * 0.001m;
        else if (order.AssetClass == AssetClass.Equity)
            return order.Value * 0.002m;
        else if (order.AssetClass == AssetClass.FixedIncome)
            return order.Value * 0.0005m;
        // Derivative and Crypto both fall through to...
        return 0m;  // FREE TRADES! Bug: derivatives trade at zero commission
    }
}

// --- Usage showing the bugs ---
public class PatternMatchingBug
{
    public static void Main()
    {
        var router = new VenueRouter();

        var cryptoOrder = new Order
        {
            Ticker = "BTC",
            Value = 500_000m,
            AssetClass = AssetClass.Crypto
        };

        // Bug 1: Crypto routes to DEFAULT_VENUE (charges 5x the spread)
        var venue = router.RouteOrder(cryptoOrder);
        Console.WriteLine($"BTC routed to: {venue}");  // DEFAULT_VENUE

        // Bug 2: Derivatives trade for free
        var derivativeOrder = new Order
        {
            Ticker = "SPY_CALL_450",
            Value = 250_000m,
            AssetClass = AssetClass.Derivative
        };
        var fee = router.CalculateFee(derivativeOrder);
        Console.WriteLine($"Derivative fee: {fee:C}");  // $0.00

        // Six months later, someone adds AssetClass.FX to the enum.
        // No compiler error anywhere. FX orders silently route to DEFAULT_VENUE
        // and trade at zero commission. Nobody notices for weeks.
    }
}
```

**The emergent problem:** The `_ => ...` discard pattern and the implicit `return 0m` at the end of if-else chains silently swallow unhandled cases. C# doesn't warn on non-exhaustive enum switches unless you enable specific analyzers — and even then, the `_` discard suppresses it.

#### F# — Exhaustive Matching With Destructuring

```fsharp
open System

type OptionType = Call | Put

type InstrumentDetails =
    | EquityDetails of exchange: string * sector: string
    | BondDetails of maturity: DateTime * couponRate: decimal
    | DerivativeDetails of underlying: string * expiry: DateTime * optionType: OptionType
    | CryptoDetails of chain: string * dex: bool

type Order = {
    Ticker: string
    Value: decimal
    Details: InstrumentDetails
}

let routeOrder (order: Order) : string =
    match order.Details with
    | EquityDetails (exchange, _) when exchange = "TSX" -> "ALPHA_VENUE"
    | EquityDetails _ -> "NYSE_ARCA"
    | BondDetails (maturity, _) when maturity < DateTime.UtcNow.AddYears(2) -> "SHORT_TERM_VENUE"
    | BondDetails _ -> "BOND_DESK"
    | DerivativeDetails (_, expiry, Call) when expiry < DateTime.UtcNow.AddDays(7) -> "FAST_OPTIONS"
    | DerivativeDetails _ -> "CBOE"
    | CryptoDetails (_, true) -> "DEX_ROUTER"
    | CryptoDetails _ -> "CENTRALIZED_EXCHANGE"
    // Adding | FXDetails of pair: string to InstrumentDetails
    // immediately produces: warning FS0025: Incomplete pattern matches.

let calculateFee (order: Order) : decimal =
    match order.Details, order.Value with
    | EquityDetails _, v when v > 100_000m -> v * 0.001m
    | EquityDetails _, v -> v * 0.002m
    | BondDetails _, v -> v * 0.0005m
    | DerivativeDetails _, v -> v * 0.0015m
    | CryptoDetails _, v -> v * 0.003m
    // Every case accounted for. No implicit zero. No silent fallthrough.

// --- Usage ---

let orders : Order list = [
    { Ticker = "AAPL"; Value = 150_000m; Details = EquityDetails ("NYSE", "Tech") }
    { Ticker = "RY"; Value = 200_000m; Details = EquityDetails ("TSX", "Finance") }
    { Ticker = "US10Y"; Value = 1_000_000m;
      Details = BondDetails (DateTime(2025, 6, 15), 0.04m) }
    { Ticker = "SPY_CALL"; Value = 50_000m;
      Details = DerivativeDetails ("SPY", DateTime(2025, 3, 21), Call) }
    { Ticker = "BTC"; Value = 500_000m; Details = CryptoDetails ("ethereum", false) }
]

for order in orders do
    let venue = routeOrder order
    let fee = calculateFee order
    printfn $"{order.Ticker}: routed to {venue}, fee = {fee:C}"
```

**Why F# prevents it:** F# pattern matching on DUs is exhaustive by default. There's no `_` needed unless you explicitly want a catch-all — and the idiomatic practice is to list all cases. When you add a new case to the DU, the compiler tells you every function that needs updating. You physically can't add `FXDetails` without touching every `match` in the codebase.

---

### 2.4 Type Inference and Advanced Type Systems

**Scenario:** Currency conversion errors in a multi-currency portfolio.

#### C# — Decimals Are Just Decimals

```csharp
using System;

public class FxConverter
{
    public static decimal GetRate(string fromCurrency, string toCurrency)
    {
        // Simplified: USD/JPY rate
        if (fromCurrency == "USD" && toCurrency == "JPY") return 150.00m;
        if (fromCurrency == "JPY" && toCurrency == "USD") return 1.0m / 150.00m;
        if (fromCurrency == "USD" && toCurrency == "CAD") return 1.36m;
        if (fromCurrency == "CAD" && toCurrency == "USD") return 1.0m / 1.36m;
        return 1.0m;  // same currency
    }
}

public class TypeInferenceBug
{
    public static void Main()
    {
        decimal usdPosition = 1_000_000m;
        decimal jpyPosition = 150_000_000m;  // 150M yen

        // Bug 1: This compiles and runs — adding USD to JPY
        decimal totalNAV = usdPosition + jpyPosition;
        Console.WriteLine($"Nonsense NAV: {totalNAV:N2}");  // 151,000,000 — meaningless

        // Bug 2: Applying the wrong conversion direction
        decimal rate = FxConverter.GetRate("USD", "JPY");  // ~150
        decimal converted = jpyPosition * rate;  // multiplied JPY by USD/JPY rate!
        Console.WriteLine($"Wrong conversion: {converted:N2}");  // 22,500,000,000

        // Should have DIVIDED: jpyPosition / rate = 1,000,000 USD
        decimal correct = jpyPosition / rate;
        Console.WriteLine($"Correct conversion: {correct:N2}");  // 1,000,000

        // The wrong value (22.5B) showed up as a position in the risk report.
        // The PM called the CRO at midnight.
        // Nothing in the type system prevented any of this.
    }
}
```

**The emergent problem:** `decimal` carries no semantic meaning about *what* it represents. A JPY amount and a USD amount have the same type. The compiler can't distinguish between "multiply by rate" and "divide by rate" because both are `decimal * decimal -> decimal`.

#### F# — Units of Measure Catch Currency Errors at Compile Time

```fsharp
[<Measure>] type USD
[<Measure>] type JPY
[<Measure>] type CAD

// Amounts carry their currency in the type
let usdPosition : decimal<USD> = 1_000_000m<USD>
let jpyPosition : decimal<JPY> = 150_000_000m<JPY>

// COMPILER ERROR: The unit of measure 'USD' does not match the unit of measure 'JPY'
// let totalNAV = usdPosition + jpyPosition

// FX rates are typed as ratios
let usdJpyRate : decimal<JPY/USD> = 150m<JPY/USD>

// Correct conversion: USD * (JPY/USD) = JPY  ✓
let usdInJpy : decimal<JPY> = usdPosition * usdJpyRate
printfn $"USD in JPY: {usdInJpy}"  // 150,000,000 JPY

// Wrong direction won't compile:
// let wrong = jpyPosition * usdJpyRate
// Error: expected decimal<JPY * JPY/USD> — units don't cancel

// Correct: JPY / (JPY/USD) = USD  ✓
let jpyInUsd : decimal<USD> = jpyPosition / usdJpyRate
printfn $"JPY in USD: {jpyInUsd}"  // 1,000,000 USD

// Now you can add them — type-safe addition
let totalNAV : decimal<USD> = usdPosition + jpyInUsd
printfn $"Total NAV: {totalNAV} USD"  // 2,000,000 USD

// Multi-currency portfolio
let cadUsdRate : decimal<USD/CAD> = 0.735m<USD/CAD>
let cadPosition : decimal<CAD> = 500_000m<CAD>
let cadInUsd : decimal<USD> = cadPosition * cadUsdRate

let fullNAV : decimal<USD> = totalNAV + cadInUsd
printfn $"Full NAV: {fullNAV} USD"
```

**Why F# prevents it:** Units of measure are erased at runtime (zero performance cost) but checked at compile time. The type system literally does dimensional analysis — if the units don't cancel correctly, the code won't compile. You can't accidentally add USD to JPY, and you can't apply a conversion rate in the wrong direction. This is the kind of bug that has actually caused eight-figure losses at real trading firms.

---

## 3. Practical Benefits

### 3.1 Composability

**Scenario:** Building a pricing pipeline that validates, enriches, prices, and risk-checks trades.

#### C# — Interface Composition With Emergent State Interactions

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public class Trade
{
    public string TradeId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public bool IsValidated { get; set; }
}

public class RateLimitException : Exception
{
    public RateLimitException(string message) : base(message) { }
}

public interface ITradeProcessor
{
    Trade Process(Trade trade);
}

public class Validator : ITradeProcessor
{
    private int _validationCount = 0;  // hidden state

    public Trade Process(Trade trade)
    {
        _validationCount++;
        if (_validationCount > 1000)  // rate limiting — but hidden!
            throw new RateLimitException("Validation rate limit exceeded");
        trade.IsValidated = true;  // mutates input!
        return trade;
    }
}

public class PricingEnricher : ITradeProcessor
{
    public Trade Process(Trade trade)
    {
        if (!trade.IsValidated)  // depends on Validator having run first
            throw new InvalidOperationException("Must validate first");
        trade.Price = GetMarketPrice(trade.Ticker);  // mutates input
        return trade;
    }

    private decimal GetMarketPrice(string ticker) => ticker switch
    {
        "AAPL" => 185.00m,
        "MSFT" => 420.00m,
        _ => 100.00m
    };
}

public class RiskChecker : ITradeProcessor
{
    public Trade Process(Trade trade)
    {
        if (trade.Quantity * trade.Price > 1_000_000m)
            throw new InvalidOperationException($"Trade {trade.TradeId} exceeds risk limit");
        return trade;
    }
}

// --- Usage showing the bugs ---
public class ComposabilityBug
{
    public static void Main()
    {
        var validator = new Validator();
        var enricher = new PricingEnricher();
        var riskChecker = new RiskChecker();

        var pipeline = new List<ITradeProcessor> { validator, enricher, riskChecker };

        var trade = new Trade { TradeId = "T-001", Ticker = "AAPL", Quantity = 100 };

        // Pipeline LOOKS composable:
        var result = pipeline.Aggregate(trade, (t, processor) => processor.Process(t));
        Console.WriteLine($"Trade {result.TradeId}: price = {result.Price}, validated = {result.IsValidated}");

        // Bug 1: Reordering processors breaks things silently
        // new List<ITradeProcessor> { enricher, validator, riskChecker }
        // -> enricher throws because IsValidated is false

        // Bug 2: Running the pipeline twice on the same trade object
        trade.Price = 0;  // "reset" — but IsValidated is still true from first pass
        var result2 = pipeline.Aggregate(trade, (t, processor) => processor.Process(t));
        // Second pass skips validation logic because IsValidated was already true

        // Bug 3: Validator's _validationCount persists across batches
        for (int i = 0; i < 1001; i++)
        {
            try
            {
                var t = new Trade { TradeId = $"T-{i}", Ticker = "AAPL", Quantity = 10 };
                pipeline.Aggregate(t, (tr, p) => p.Process(tr));
            }
            catch (RateLimitException ex)
            {
                Console.WriteLine($"Rate limited at trade {i}: {ex.Message}");
                break;
            }
        }
    }
}
```

**The emergent problem:** Each `ITradeProcessor` can have internal state and can mutate its input. "Composition" here is really sequential mutation of a shared object, with implicit ordering requirements and hidden rate limits. Reordering processors or reusing the pipeline produces completely different behaviors — none of which the type system catches.

#### F# — Function Composition With Explicit Data Flow

```fsharp
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

let validate (trade: Trade) : Result<Trade, string> =
    if trade.Quantity > 0m && trade.Ticker <> ""
    then Ok trade
    else Error $"Invalid trade {trade.TradeId}: bad quantity or ticker"

let price (getMarketPrice: string -> decimal) (trade: Trade) : PricedTrade =
    { Trade = trade; MarketPrice = getMarketPrice trade.Ticker }

let checkRisk (maxExposure: decimal) (pricedTrade: PricedTrade) : RiskCheckedTrade =
    let exposure = pricedTrade.Trade.Quantity * pricedTrade.MarketPrice
    { PricedTrade = pricedTrade; Exposure = exposure; Approved = exposure < maxExposure }

// Types enforce ordering: Trade -> PricedTrade -> RiskCheckedTrade
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

// --- Usage ---

let getMarketPrice (ticker: string) : decimal =
    match ticker with
    | "AAPL" -> 185.00m
    | "MSFT" -> 420.00m
    | _ -> 100.00m

let trades : Trade list = [
    { TradeId = "T-001"; Ticker = "AAPL"; Quantity = 100m }
    { TradeId = "T-002"; Ticker = "MSFT"; Quantity = 5000m }  // will exceed risk limit
    { TradeId = "T-003"; Ticker = ""; Quantity = 50m }        // will fail validation
]

let results = trades |> List.map (processTrade getMarketPrice)

for result in results do
    match result with
    | Ok checked ->
        let status = if checked.Approved then "APPROVED" else "REJECTED (risk)"
        printfn $"{checked.PricedTrade.Trade.TradeId}: {status}, exposure = {checked.Exposure:C}"
    | Error reason ->
        printfn $"FAILED: {reason}"

// You CAN'T call `price` before `validate` with the wrong type.
// You CAN'T call `checkRisk` on an unpriced trade — it needs PricedTrade.
// No shared mutable trade object. No hidden counters. No ordering ambiguity.
// Each function is independently testable with zero setup.
```

**Why F# prevents it:** The types form a pipeline: `Trade -> PricedTrade -> RiskCheckedTrade`. You physically can't reorder the stages because the types won't align. Each function is pure — no internal counters, no mutation. Composition is mathematical: `f >> g >> h` always behaves the same regardless of how many times you call it.

---

### 3.2 Concurrency and Parallelism Safety

**Scenario:** Real-time P&L aggregation across multiple trading desks.

#### C# — Shared Mutable Dictionary Under Concurrent Access

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class Trade
{
    public string Ticker { get; set; } = "";
    public decimal RealizedPnl { get; set; }
}

public class Desk
{
    public string Name { get; set; } = "";
    public List<Trade> Trades { get; set; } = new();
}

public class PnlAggregator
{
    private readonly Dictionary<string, decimal> _deskPnl = new();

    public void UpdateDeskPnl(string desk, List<Trade> trades)
    {
        var pnl = trades.Sum(t => t.RealizedPnl);

        // Race condition: read-modify-write is not atomic
        if (_deskPnl.ContainsKey(desk))
            _deskPnl[desk] += pnl;
        else
            _deskPnl[desk] = pnl;
    }

    public decimal GetTotalPnl() => _deskPnl.Values.Sum();

    public void PrintPnl()
    {
        foreach (var kvp in _deskPnl)
            Console.WriteLine($"  {kvp.Key}: {kvp.Value:C}");
    }
}

// --- Usage showing the race condition ---
public class ConcurrencyBug
{
    public static void Main()
    {
        var desks = new List<Desk>
        {
            new()
            {
                Name = "Equities",
                Trades = new List<Trade>
                {
                    new() { Ticker = "AAPL", RealizedPnl = 50_000m },
                    new() { Ticker = "MSFT", RealizedPnl = -12_000m },
                }
            },
            new()
            {
                Name = "Fixed Income",
                Trades = new List<Trade>
                {
                    new() { Ticker = "US10Y", RealizedPnl = 200_000m },
                    new() { Ticker = "US2Y", RealizedPnl = -30_000m },
                }
            },
            new()
            {
                Name = "Derivatives",
                Trades = new List<Trade>
                {
                    new() { Ticker = "SPY_PUT", RealizedPnl = 80_000m },
                    new() { Ticker = "VIX_CALL", RealizedPnl = -5_000m },
                }
            }
        };

        var aggregator = new PnlAggregator();

        // Multiple desks update simultaneously — race condition on _deskPnl
        Parallel.ForEach(desks, desk =>
        {
            aggregator.UpdateDeskPnl(desk.Name, desk.Trades);
        });

        Console.WriteLine("P&L by desk:");
        aggregator.PrintPnl();
        Console.WriteLine($"Total P&L: {aggregator.GetTotalPnl():C}");

        // Bugs:
        // 1. Dictionary is not thread-safe — concurrent writes can corrupt
        //    the internal hash table, causing lost updates or exceptions.
        // 2. GetTotalPnl() called while updates are running reads a
        //    partially-updated state.
        // 3. The compiler gives zero warnings about any of this.
    }
}
```

#### F# — Immutable Aggregation With Atomic Results

```fsharp
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

let result = aggregatePnl desks

printfn "P&L by desk:"
result |> Map.iter (fun desk pnl -> printfn $"  {desk}: {pnl:C}")
printfn $"Total P&L: {totalPnl result:C}"

// The result is an immutable Map. You either have the complete,
// consistent result or you don't have it yet. No race conditions.
// No "partially updated" state. No locks needed.
```

**Why F# prevents it:** There's no shared mutable dictionary. Each desk's P&L is computed independently in a pure function, and the results are combined into an immutable `Map` atomically. You can't observe an intermediate state because there isn't one — the `Map` only exists once all computations complete.

---

### 3.3 Easier Reasoning, Debugging, and Refactoring

**Scenario:** Debugging why a portfolio's Greeks (delta, gamma) are wrong.

#### C# — Mutation Chain Makes Root Cause Non-Local

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public class Position
{
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal Delta { get; set; }
    public decimal Gamma { get; set; }
    public decimal AdjustedQuantity { get; set; }
}

public class Portfolio
{
    public List<Position> Positions { get; set; } = new();
}

public class GreeksCalculator
{
    private Portfolio? _portfolio;

    public void LoadPortfolio(Portfolio p) => _portfolio = p;

    private static decimal ComputeDelta(Position pos) =>
        pos.Price > 100 ? 0.65m : 0.45m;  // simplified

    private static decimal ComputeGamma(Position pos) =>
        pos.Price > 100 ? 0.03m : 0.05m;  // simplified

    public void CalculateDelta()
    {
        foreach (var pos in _portfolio!.Positions)
        {
            pos.Delta = ComputeDelta(pos);
            pos.AdjustedQuantity = pos.Quantity * pos.Delta;  // mutation
        }
    }

    public void CalculateGamma()
    {
        foreach (var pos in _portfolio!.Positions)
        {
            // Bug: uses AdjustedQuantity (set by CalculateDelta) instead of Quantity.
            // If called independently, pos.AdjustedQuantity is 0 (default),
            // and gamma silently becomes 0 for all positions.
            pos.Gamma = ComputeGamma(pos) * pos.AdjustedQuantity;
        }
    }

    public void CalculateAllGreeks()
    {
        CalculateDelta();
        CalculateGamma();
        // Works. But a developer refactors to parallelize:
        // Task.Run(() => CalculateDelta());
        // Task.Run(() => CalculateGamma());
        // Now Gamma sometimes reads AdjustedQuantity before Delta writes it.
    }
}

// --- Usage showing the bug ---
public class ReasoningBug
{
    public static void Main()
    {
        var portfolio = new Portfolio
        {
            Positions = new List<Position>
            {
                new() { Ticker = "AAPL", Quantity = 1000, Price = 185.00m },
                new() { Ticker = "MSFT", Quantity = 500, Price = 420.00m },
            }
        };

        var calc = new GreeksCalculator();
        calc.LoadPortfolio(portfolio);

        // Correct order: works
        calc.CalculateAllGreeks();
        foreach (var pos in portfolio.Positions)
            Console.WriteLine($"{pos.Ticker}: delta={pos.Delta:F2}, gamma={pos.Gamma:F4}, adjQty={pos.AdjustedQuantity:F0}");

        // Bug: calling CalculateGamma alone on a fresh portfolio
        var freshPortfolio = new Portfolio
        {
            Positions = new List<Position>
            {
                new() { Ticker = "AAPL", Quantity = 1000, Price = 185.00m },
            }
        };
        var calc2 = new GreeksCalculator();
        calc2.LoadPortfolio(freshPortfolio);
        calc2.CalculateGamma();  // AdjustedQuantity is 0 -> gamma is 0!

        foreach (var pos in freshPortfolio.Positions)
            Console.WriteLine($"{pos.Ticker}: gamma={pos.Gamma:F4} (should not be 0!)");

        // To debug wrong gamma values, you need to verify:
        // 1. Was CalculateDelta called before CalculateGamma?
        // 2. Did anything else modify AdjustedQuantity between calls?
        // 3. Is _portfolio the same reference that was loaded?
        // None of these are visible at the call site.
    }
}
```

#### F# — Data Flow Makes Dependencies Explicit

```fsharp
type Position = {
    Ticker: string
    Quantity: decimal
    Price: decimal
}

type PositionWithDelta = {
    Position: Position
    Delta: decimal
    AdjustedQuantity: decimal
}

type PositionWithGreeks = {
    PositionWithDelta: PositionWithDelta
    Gamma: decimal
}

let computeDelta (pos: Position) : decimal =
    if pos.Price > 100m then 0.65m else 0.45m

let computeGamma (pos: Position) : decimal =
    if pos.Price > 100m then 0.03m else 0.05m

let calculateDeltas (positions: Position list) : PositionWithDelta list =
    positions |> List.map (fun pos ->
        let delta = computeDelta pos
        { Position = pos; Delta = delta; AdjustedQuantity = pos.Quantity * delta })

let calculateGammas (positionsWithDelta: PositionWithDelta list) : PositionWithGreeks list =
    positionsWithDelta |> List.map (fun pwd ->
        let gamma = computeGamma pwd.Position * pwd.AdjustedQuantity
        { PositionWithDelta = pwd; Gamma = gamma })

let calculateAllGreeks (positions: Position list) : PositionWithGreeks list =
    positions
    |> calculateDeltas   // must happen first — enforced by types
    |> calculateGammas   // takes PositionWithDelta list, not Position list

// --- Usage ---

let portfolio : Position list = [
    { Ticker = "AAPL"; Quantity = 1000m; Price = 185.00m }
    { Ticker = "MSFT"; Quantity = 500m; Price = 420.00m }
]

let results = calculateAllGreeks portfolio

for r in results do
    let pos = r.PositionWithDelta.Position
    printfn $"{pos.Ticker}: delta={r.PositionWithDelta.Delta:F2}, gamma={r.Gamma:F4}, adjQty={r.PositionWithDelta.AdjustedQuantity:F0}"

// You CANNOT call calculateGammas with raw Position list — compiler error.
// The type signature tells you deltas were already calculated.
// Debugging is local: only look at the function's inputs.
```

**Why F# prevents it:** The dependency between delta and gamma calculations is encoded in the types. `calculateGammas` requires `PositionWithDelta list`, not `Position list` — so it's structurally impossible to call it without deltas having been computed first. Debugging is local: you only need to look at the function's inputs, not at the entire history of mutations across a shared object graph.

---

### 3.4 Improved Testing

**Scenario:** Testing a margin call detection system.

#### C# — Testing Requires Complex Setup and Mocking

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public interface IAccountService
{
    Account GetAccount(string accountId);
    List<Position> GetPositions(string accountId);
}

public interface IPricingService
{
    decimal GetPrice(string ticker);
}

public interface INotificationService
{
    void SendMarginCall(string accountId, decimal marginRatio);
}

public interface IAuditLog
{
    void Record(string message);
}

public class Account
{
    public string AccountId { get; set; } = "";
    public decimal LoanAmount { get; set; }
    public decimal MaintenanceMargin { get; set; }
}

public class Position
{
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
}

public class MarginCallDetector
{
    private readonly IAccountService _accountService;
    private readonly IPricingService _pricingService;
    private readonly INotificationService _notificationService;
    private readonly IAuditLog _auditLog;

    public MarginCallDetector(
        IAccountService accountService,
        IPricingService pricingService,
        INotificationService notificationService,
        IAuditLog auditLog)
    {
        _accountService = accountService;
        _pricingService = pricingService;
        _notificationService = notificationService;
        _auditLog = auditLog;
    }

    public bool CheckMarginCall(string accountId)
    {
        var account = _accountService.GetAccount(accountId);         // DB call
        var positions = _accountService.GetPositions(accountId);     // DB call

        var marketValue = positions
            .Sum(p => p.Quantity * _pricingService.GetPrice(p.Ticker));  // API call

        var marginRatio = marketValue / account.LoanAmount;

        if (marginRatio < account.MaintenanceMargin)
        {
            _notificationService.SendMarginCall(accountId, marginRatio);  // email
            _auditLog.Record($"Margin call: {accountId}, ratio: {marginRatio}");  // DB write
            return true;
        }
        return false;
    }
}

// --- Testing requires mocking 4 interfaces ---
// Using manual test doubles since this should compile without Moq
public class MockAccountService : IAccountService
{
    public Account GetAccount(string accountId) => new()
    {
        AccountId = accountId,
        LoanAmount = 100_000m,
        MaintenanceMargin = 0.3m
    };

    public List<Position> GetPositions(string accountId) => new()
    {
        new() { Ticker = "AAPL", Quantity = 100 }
    };
}

public class MockPricingService : IPricingService
{
    public decimal GetPrice(string ticker) => 200m;
}

public class MockNotificationService : INotificationService
{
    public int CallCount { get; private set; }
    public void SendMarginCall(string accountId, decimal marginRatio) => CallCount++;
}

public class MockAuditLog : IAuditLog
{
    public int CallCount { get; private set; }
    public void Record(string message) => CallCount++;
}

public class TestingExample
{
    public static void Main()
    {
        // 4 mock implementations for 1 test
        var mockAccounts = new MockAccountService();
        var mockPricing = new MockPricingService();
        var mockNotifier = new MockNotificationService();
        var mockAudit = new MockAuditLog();

        var detector = new MarginCallDetector(
            mockAccounts, mockPricing, mockNotifier, mockAudit);

        var result = detector.CheckMarginCall("ACC1");

        // marketValue = 100 * 200 = 20,000
        // marginRatio = 20,000 / 100,000 = 0.2
        // 0.2 < 0.3 (maintenance margin) => margin call triggered
        Console.WriteLine($"Margin call triggered: {result}");  // true
        Console.WriteLine($"Notification sent: {mockNotifier.CallCount > 0}");
        Console.WriteLine($"Audit logged: {mockAudit.CallCount > 0}");

        // 30+ lines of setup to test: "is 0.2 < 0.3?"
        // We're testing WIRING, not LOGIC.
    }
}
```

#### F# — Pure Logic Tested Directly, Effects Tested Separately

```fsharp
// Pure logic — zero dependencies
let calculateMarginRatio
    (positions: (decimal * decimal) list)
    (loanAmount: decimal)
    : decimal =
    let marketValue = positions |> List.sumBy (fun (qty, price) -> qty * price)
    marketValue / loanAmount

let isMarginCall (maintenanceMargin: decimal) (marginRatio: decimal) : bool =
    marginRatio < maintenanceMargin

// --- Tests are trivial — just call functions with values ---

let test (name: string) (expected: bool) (actual: bool) =
    let status = if expected = actual then "PASS" else "FAIL"
    printfn $"  [{status}] {name}"

printfn "Margin call tests:"

// Test 1: margin call triggered when ratio below maintenance
let ratio1 = calculateMarginRatio [ (100m, 200m) ] 100_000m  // = 0.2
test "triggered when ratio below maintenance" true (isMarginCall 0.3m ratio1)

// Test 2: no margin call when ratio above maintenance
let ratio2 = calculateMarginRatio [ (100m, 500m) ] 100_000m  // = 0.5
test "not triggered when ratio above maintenance" false (isMarginCall 0.3m ratio2)

// Test 3: multi-position margin calculation
let ratio3 = calculateMarginRatio [ (100m, 200m); (50m, 300m) ] 100_000m  // = 0.35
test "multi-position calculation" false (isMarginCall 0.3m ratio3)

// Test 4: edge case — exactly at maintenance margin
let ratio4 = calculateMarginRatio [ (100m, 300m) ] 100_000m  // = 0.3
test "exact maintenance margin is not a call" false (isMarginCall 0.3m ratio4)

// Test 5: edge case — zero positions
let ratio5 = calculateMarginRatio [] 100_000m  // = 0.0
test "zero positions triggers margin call" true (isMarginCall 0.3m ratio5)

// 5 tests, ~10 lines total, testing actual business logic.
// No mocks. No interfaces. No setup. No teardown.
```

**Why F# prevents it:** By separating pure calculation from effectful orchestration, the business logic (`calculateMarginRatio`, `isMarginCall`) becomes plain functions from values to values. You don't need to mock a pricing service to test margin math — you just pass `(100m, 200m)` as a position. The effectful wiring (fetching prices, sending emails) is a thin layer at the boundary, tested separately if needed.

---

### 3.5 Formal Verification and Correctness

**Scenario:** Ensuring trade settlement amounts are computed correctly across FX conversions.

#### C# — Runtime Assertion Is the Best You Can Do

```csharp
using System;
using System.Diagnostics;

public class Trade
{
    public string TradeId { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

public class FxConverter
{
    public static decimal? GetRate(string fromCurrency, string toCurrency)
    {
        if (fromCurrency == "CAD" && toCurrency == "USD") return 0.735m;
        if (fromCurrency == "JPY" && toCurrency == "USD") return 0.0067m;
        if (fromCurrency == "USD" && toCurrency == "JPY") return 150.00m;
        if (fromCurrency == "USD" && toCurrency == "CAD") return 1.36m;
        if (fromCurrency == toCurrency) return 1.0m;
        return null;
    }
}

public class SettlementCalculator
{
    public decimal CalculateSettlement(Trade trade, string baseCurrency)
    {
        // Developer must manually ensure correctness
        Debug.Assert(trade.Quantity > 0, "Quantity must be positive");

        var localAmount = trade.Quantity * trade.Price;

        if (trade.Currency == baseCurrency)
            return localAmount;

        var rate = FxConverter.GetRate(trade.Currency, baseCurrency);
        Debug.Assert(rate.HasValue, $"No FX rate for {trade.Currency}/{baseCurrency}");
        Debug.Assert(rate!.Value > 0, "FX rate must be positive");

        // Is this right? Multiply or divide?
        // Depends on how the rate is quoted — nothing in the type tells you
        return localAmount * rate.Value;
    }
}

// --- Usage showing the bugs ---
public class FormalVerificationBug
{
    public static void Main()
    {
        var calculator = new SettlementCalculator();

        // Correct case
        var trade1 = new Trade { TradeId = "T-001", Currency = "CAD", Quantity = 1000, Price = 50.00m };
        var settlement1 = calculator.CalculateSettlement(trade1, "USD");
        Console.WriteLine($"CAD trade in USD: {settlement1:C}");  // $36,750.00 — correct

        // Bug 1: Negative quantity — Debug.Assert stripped in Release builds!
        var trade2 = new Trade { TradeId = "T-002", Currency = "USD", Quantity = -500, Price = 100.00m };
        var settlement2 = calculator.CalculateSettlement(trade2, "USD");
        Console.WriteLine($"Negative qty settlement: {settlement2:C}");  // -$50,000.00 — no error in Release

        // Bug 2: Wrong rate direction
        var trade3 = new Trade { TradeId = "T-003", Currency = "JPY", Quantity = 10000, Price = 1500m };
        // JPY -> USD should divide by 150, but we're multiplying by 0.0067
        // These give different results due to floating point and rate source
        var settlement3 = calculator.CalculateSettlement(trade3, "USD");
        Console.WriteLine($"JPY trade in USD: {settlement3:C}");
        // Developer can't tell from the types whether multiply or divide is correct

        // Bug 3: Missing rate returns null — but Debug.Assert is stripped
        var trade4 = new Trade { TradeId = "T-004", Currency = "GBP", Quantity = 100, Price = 200.00m };
        try
        {
            var settlement4 = calculator.CalculateSettlement(trade4, "USD");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("Runtime crash: no rate for GBP/USD");
        }
    }
}
```

#### F# — Type-Level Proofs With Units and Constrained Types

```fsharp
[<Measure>] type USD
[<Measure>] type CAD
[<Measure>] type shares

// Positive quantity enforced by smart constructor
type PositiveDecimal<[<Measure>] 'u> = private PositiveDecimal of decimal<'u>

module PositiveDecimal =
    let create (value: decimal<'u>) : Result<PositiveDecimal<'u>, string> =
        if value > 0m<_> then Ok (PositiveDecimal value)
        else Error $"Value must be positive, got {value}"

    let value (PositiveDecimal v) = v

let calculateSettlement
    (qty: PositiveDecimal<shares>)
    (price: decimal<CAD/shares>)
    (fxRate: decimal<USD/CAD>)
    : decimal<USD> =
    let localAmount : decimal<CAD> = (PositiveDecimal.value qty) * price
    // CAD * (USD/CAD) = USD — compiler verifies unit cancellation
    localAmount * fxRate

// --- Usage ---

let price : decimal<CAD/shares> = 50.00m<CAD/shares>
let rate : decimal<USD/CAD> = 0.735m<USD/CAD>

match PositiveDecimal.create 1000m<shares> with
| Ok qty ->
    let settlement = calculateSettlement qty price rate
    printfn $"Settlement: {settlement} USD"  // 36,750 USD
| Error msg ->
    printfn $"Invalid: {msg}"

// Compile-time guarantees:
// 1. qty is always positive (can't construct PositiveDecimal with negative)
match PositiveDecimal.create -500m<shares> with
| Ok _ -> printfn "This won't happen"
| Error msg -> printfn $"Caught at construction: {msg}"

// 2. Wrong rate direction won't compile:
// let wrong = (PositiveDecimal.value qty) * price * (1.36m<CAD/USD>)
// Error: units would produce CAD * CAD/USD, not USD

// 3. Can't add incompatible currencies:
// let cadAmount = 100m<CAD>
// let usdAmount = 100m<USD>
// let nonsense = cadAmount + usdAmount  // compiler error

// 4. None of this exists at runtime — zero performance cost
```

**Why F# prevents it:** The type system acts as a proof assistant. The compiler verifies that `shares * (CAD/shares) * (USD/CAD) = USD` through dimensional analysis. A developer can't accidentally multiply when they should divide, because the units would produce a nonsensical type like `CAD²/shares` instead of `USD`. The `PositiveDecimal` smart constructor makes negative quantities *unrepresentable* — not "checked at runtime," but structurally impossible.

---

### 3.6 Expressiveness and Conciseness

**Scenario:** Processing a batch of trade confirmations and generating settlement instructions.

#### C# — Ceremony-Heavy Imperative Processing

```csharp
using System;
using System.Collections.Generic;

public enum ConfirmationStatus { Pending, Confirmed, Rejected }
public enum ApprovalLevel { None, Standard, Senior }

public class TradeConfirmation
{
    public string TradeId { get; set; } = "";
    public ConfirmationStatus Status { get; set; }
    public DateTime SettlementDate { get; set; }
    public string Currency { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

public class SettlementInstruction
{
    public string TradeId { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal SettlementAmount { get; set; }
    public string Currency { get; set; } = "";
    public bool RequiresApproval { get; set; }
    public ApprovalLevel ApprovalLevel { get; set; }
}

public class FxService
{
    public static decimal? GetFxRate(string fromCurrency, string toCurrency)
    {
        if (fromCurrency == "CAD" && toCurrency == "USD") return 0.735m;
        if (fromCurrency == "EUR" && toCurrency == "USD") return 1.08m;
        if (fromCurrency == toCurrency) return 1.0m;
        return null;
    }
}

public class SettlementProcessor
{
    public List<SettlementInstruction> ProcessConfirmations(
        List<TradeConfirmation> confirmations)
    {
        var instructions = new List<SettlementInstruction>();

        foreach (var conf in confirmations)
        {
            if (conf == null) continue;
            if (conf.Status != ConfirmationStatus.Confirmed) continue;
            if (conf.SettlementDate < DateTime.Today) continue;

            var instruction = new SettlementInstruction();
            instruction.TradeId = conf.TradeId;
            instruction.Amount = conf.Quantity * conf.Price;

            if (conf.Currency != "USD")
            {
                var rate = FxService.GetFxRate(conf.Currency, "USD");
                if (rate == null)
                {
                    // Log and skip? Throw? Return partial results?
                    // Developer chose to silently skip.
                    continue;
                }
                instruction.SettlementAmount = instruction.Amount * rate.Value;
                instruction.Currency = "USD";
            }
            else
            {
                instruction.SettlementAmount = instruction.Amount;
                instruction.Currency = "USD";
            }

            if (instruction.SettlementAmount > 1_000_000)
            {
                instruction.RequiresApproval = true;
                instruction.ApprovalLevel = instruction.SettlementAmount > 10_000_000
                    ? ApprovalLevel.Senior
                    : ApprovalLevel.Standard;
            }

            instructions.Add(instruction);
        }

        return instructions;
    }
}

// --- Usage ---
public class ExpressivenessExample
{
    public static void Main()
    {
        var processor = new SettlementProcessor();

        var confirmations = new List<TradeConfirmation>
        {
            new() { TradeId = "T-001", Status = ConfirmationStatus.Confirmed,
                    SettlementDate = DateTime.Today.AddDays(2), Currency = "USD",
                    Quantity = 10_000, Price = 185.00m },
            new() { TradeId = "T-002", Status = ConfirmationStatus.Confirmed,
                    SettlementDate = DateTime.Today.AddDays(1), Currency = "CAD",
                    Quantity = 5_000, Price = 50.00m },
            new() { TradeId = "T-003", Status = ConfirmationStatus.Rejected,
                    SettlementDate = DateTime.Today.AddDays(2), Currency = "USD",
                    Quantity = 100, Price = 420.00m },
            new() { TradeId = "T-004", Status = ConfirmationStatus.Confirmed,
                    SettlementDate = DateTime.Today.AddDays(3), Currency = "GBP",
                    Quantity = 1_000, Price = 200.00m },  // no GBP rate — silently skipped!
        };

        var instructions = processor.ProcessConfirmations(confirmations);

        Console.WriteLine($"Processed {instructions.Count} of {confirmations.Count} confirmations:");
        foreach (var inst in instructions)
        {
            Console.WriteLine($"  {inst.TradeId}: {inst.SettlementAmount:C} " +
                $"(approval: {inst.ApprovalLevel})");
        }
        // T-004 was silently dropped — no error, no indication.
        // 40 lines of code. Multiple mutation points. Mutable instruction
        // built up incrementally. Easy to add a field and forget to set it.
    }
}
```

#### F# — Declarative Pipeline

```fsharp
open System

type ConfirmationStatus = Pending | Confirmed | Rejected

type ApprovalRequirement =
    | NoApproval
    | StandardApproval
    | SeniorApproval

type TradeConfirmation = {
    TradeId: string
    Status: ConfirmationStatus
    SettlementDate: DateTime
    Currency: string
    Quantity: decimal
    Price: decimal
}

type SettlementInstruction = {
    TradeId: string
    Amount: decimal
    SettlementAmount: decimal
    Currency: string
    Approval: ApprovalRequirement
}

let getFxRate (fromCurrency: string) (toCurrency: string) : decimal option =
    match fromCurrency, toCurrency with
    | "CAD", "USD" -> Some 0.735m
    | "EUR", "USD" -> Some 1.08m
    | c1, c2 when c1 = c2 -> Some 1.0m
    | _ -> None

let optionToResult (errorMsg: string) (opt: 'a option) : Result<'a, string> =
    match opt with
    | Some v -> Ok v
    | None -> Error errorMsg

let processConfirmations
    (confirmations: TradeConfirmation list)
    : Result<SettlementInstruction, string> list =
    confirmations
    |> List.filter (fun c -> c.Status = Confirmed && c.SettlementDate >= DateTime.Today)
    |> List.map (fun conf ->
        let amount = conf.Quantity * conf.Price
        let converted =
            if conf.Currency = "USD" then Ok amount
            else
                getFxRate conf.Currency "USD"
                |> optionToResult $"No FX rate for {conf.Currency}/USD on trade {conf.TradeId}"
                |> Result.map (fun rate -> amount * rate)

        converted |> Result.map (fun settlementAmount ->
            { TradeId = conf.TradeId
              Amount = amount
              SettlementAmount = settlementAmount
              Currency = "USD"
              Approval =
                  if settlementAmount > 10_000_000m then SeniorApproval
                  elif settlementAmount > 1_000_000m then StandardApproval
                  else NoApproval }))

// --- Usage ---

let confirmations : TradeConfirmation list = [
    { TradeId = "T-001"; Status = Confirmed
      SettlementDate = DateTime.Today.AddDays(2.0); Currency = "USD"
      Quantity = 10_000m; Price = 185.00m }
    { TradeId = "T-002"; Status = Confirmed
      SettlementDate = DateTime.Today.AddDays(1.0); Currency = "CAD"
      Quantity = 5_000m; Price = 50.00m }
    { TradeId = "T-003"; Status = Rejected
      SettlementDate = DateTime.Today.AddDays(2.0); Currency = "USD"
      Quantity = 100m; Price = 420.00m }
    { TradeId = "T-004"; Status = Confirmed
      SettlementDate = DateTime.Today.AddDays(3.0); Currency = "GBP"
      Quantity = 1_000m; Price = 200.00m }  // no GBP rate — explicit Error
]

let results = processConfirmations confirmations

printfn "Settlement results:"
for result in results do
    match result with
    | Ok inst ->
        printfn $"  {inst.TradeId}: {inst.SettlementAmount:C} (approval: {inst.Approval})"
    | Error reason ->
        printfn $"  ERROR: {reason}"

// T-004's missing FX rate is an explicit Error in the output,
// not a silently skipped row.
// 20 lines of pipeline. No mutation. No nulls.
// Every field set at construction — can't forget one (compiler error).
```

**Why F# prevents it:** The record must be fully constructed — you can't create a `SettlementInstruction` with a missing `Approval` field. The `Result` type forces callers to handle the FX rate failure explicitly. The pipeline reads top-to-bottom as a data transformation, not as a sequence of mutations to a mutable object. And it's half the code.

---

## Summary

| Concept | C# Failure Mode | F# Prevention Mechanism |
|---|---|---|
| Referential Transparency | Hidden state mutation couples unrelated calls | Pure functions return values; no internal state to couple |
| Purity | I/O hidden in "calculator" methods; backtest uses live DB limits | `Async<'T>` return type reveals effects; pure version requires data as parameter |
| Immutability | Shared mutable references cause race conditions | Records immutable by default; `{ x with ... }` creates copies |
| Higher-Order Functions | Lambda closures capture mutable state | `mutable` must be explicit; fold makes state visible |
| ADTs | Nullable fields allow illegal state combinations | DU cases carry exactly relevant data; no nulls |
| Pattern Matching | Switch with `_` silently swallows new cases | Exhaustive matching warns on unhandled cases |
| Type Systems | `decimal` carries no semantic meaning | Units of measure catch currency/unit errors at compile time |
| Composability | Interface composition has hidden ordering and state | Types encode pipeline stages; wrong order = compiler error |
| Concurrency | Shared mutable dictionary under parallel writes | Immutable data + pure functions = no race conditions |
| Reasoning/Debugging | Temporal coupling between mutations is invisible | Type signatures encode dependencies explicitly |
| Testing | 4 mock interfaces for 1 assertion | Pure functions tested with values; zero mocks |
| Formal Verification | Runtime `Assert` stripped in Release | Units of measure + smart constructors = compile-time proofs |
| Expressiveness | 40 lines of mutable ceremony | 20 lines of declarative pipeline; all fields required |

Each C# example compiles and runs without warnings. Each contains a bug that has caused real production incidents at trading firms. Each F# example makes the corresponding bug a **compiler error**, not a runtime surprise.
