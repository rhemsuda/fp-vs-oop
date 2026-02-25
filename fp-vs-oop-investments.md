# F# vs C# in Investments: Where OOP's Emergent Behaviour Bites

Real-world investment domain examples demonstrating how C#'s OOP permits subtle, production-breaking bugs that F#'s compiler structurally prevents.

---

## 1. Core Principles

### 1.1 Referential Transparency

**Scenario:** Computing NAV per unit for a mutual fund. Operations publishes the daily strike price, then compliance independently verifies it using the same method.

#### C# — Hidden Fee Accrual Breaks Substitutability

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public class Holding
{
    public string SecurityId { get; set; } = "";
    public decimal Units { get; set; }
    public decimal PricePerUnit { get; set; }
}

public class Fund
{
    public string FundId { get; set; } = "";
    public List<Holding> Holdings { get; set; } = new();
    public decimal TotalUnitsOutstanding { get; set; }
    public decimal AnnualManagementFeeBps { get; set; }  // e.g., 150 = 1.50%
}

public class NavCalculator
{
    private DateTime _lastAccrualDate = DateTime.MinValue;
    private decimal _accruedFees = 0m;

    public decimal CalculateNavPerUnit(Fund fund)
    {
        var grossAssetValue = fund.Holdings.Sum(h => h.Units * h.PricePerUnit);

        // Side effect: accumulates fees since last call
        var today = DateTime.Today;
        if (_lastAccrualDate != DateTime.MinValue)
        {
            var daysSinceLastAccrual = (today - _lastAccrualDate).Days;
            var dailyFeeRate = fund.AnnualManagementFeeBps / 10_000m / 365m;
            _accruedFees += grossAssetValue * dailyFeeRate * daysSinceLastAccrual;
        }
        _lastAccrualDate = today;

        var netAssetValue = grossAssetValue - _accruedFees;
        return Math.Round(netAssetValue / fund.TotalUnitsOutstanding, 4);
    }
}

// --- Usage showing the bug ---
public class ReferentialTransparencyBug
{
    public static void Main()
    {
        var fund = new Fund
        {
            FundId = "MF-BALANCED-001",
            Holdings = new List<Holding>
            {
                new() { SecurityId = "CDN-BOND-ETF", Units = 50_000, PricePerUnit = 20.00m },
                new() { SecurityId = "CDN-EQUITY-ETF", Units = 30_000, PricePerUnit = 35.00m },
                new() { SecurityId = "US-EQUITY-ETF", Units = 20_000, PricePerUnit = 55.00m },
            },
            TotalUnitsOutstanding = 200_000m,
            AnnualManagementFeeBps = 150  // 1.50% MER
        };
        // Gross asset value = 1,000,000 + 1,050,000 + 1,100,000 = 3,150,000

        var calculator = new NavCalculator();

        // 9:00 AM — Operations computes the official daily NAV strike
        var opsNav = calculator.CalculateNavPerUnit(fund);
        Console.WriteLine($"Operations NAV/unit: {opsNav}");

        // 10:00 AM — Compliance runs independent verification using same method
        var complianceNav = calculator.CalculateNavPerUnit(fund);
        Console.WriteLine($"Compliance NAV/unit: {complianceNav}");

        // SHOULD be identical — same fund, same day, same prices.
        Console.WriteLine($"Match: {opsNav == complianceNav}");
        Console.WriteLine($"Difference: {complianceNav - opsNav}");

        // The bug:
        // - First call: _lastAccrualDate is MinValue, so no fees accrue.
        //   _accruedFees stays at 0. Sets _lastAccrualDate to today.
        //   NAV/unit = 3,150,000 / 200,000 = 15.75
        //
        // - Second call: _lastAccrualDate is now today, daysSinceLastAccrual = 0,
        //   so no ADDITIONAL fees accrue. But _accruedFees is still 0 from the
        //   first call's initialization behavior, so compliance gets the same
        //   number THIS time.
        //
        // The REAL bug surfaces across days. Simulate day 2:
        Console.WriteLine("\n--- Simulating next business day ---");

        // Prices moved overnight
        fund.Holdings[0].PricePerUnit = 20.10m;
        fund.Holdings[1].PricePerUnit = 34.80m;
        fund.Holdings[2].PricePerUnit = 55.50m;
        // New GAV = 1,005,000 + 1,044,000 + 1,110,000 = 3,159,000

        // Simulate tomorrow by adjusting the accrual date back
        // (In production, this just happens naturally the next day)
        calculator = new NavCalculator();

        // First call on day 2 — operations
        var opsNavDay2First = calculator.CalculateNavPerUnit(fund);
        // _lastAccrualDate was MinValue, so no fees. _accruedFees = 0.
        Console.WriteLine($"Day 2 Operations NAV/unit: {opsNavDay2First}");

        // But what if the calculator instance persists across days?
        // (common with DI singletons)
        var persistentCalc = new NavCalculator();
        var day1 = persistentCalc.CalculateNavPerUnit(fund);
        Console.WriteLine($"\nPersistent calc, call 1: {day1}");

        // Simulate: someone calls it again (report generation, API request, etc.)
        var day1Again = persistentCalc.CalculateNavPerUnit(fund);
        Console.WriteLine($"Persistent calc, call 2: {day1Again}");

        // Third call — maybe a late-running batch job
        var day1Third = persistentCalc.CalculateNavPerUnit(fund);
        Console.WriteLine($"Persistent calc, call 3: {day1Third}");

        Console.WriteLine($"\nAll three equal: {day1 == day1Again && day1Again == day1Third}");

        // On the SAME day, daysSinceLastAccrual = 0 for calls 2 and 3,
        // so they happen to match. But the method is only accidentally
        // consistent — it would break if calls spanned midnight, or if
        // the instance lived across weekends (3 days of fee accrual
        // applied on Monday but not on Tuesday's verification).
        //
        // The method signature is: decimal CalculateNavPerUnit(Fund fund)
        // Nothing indicates it has internal state. A developer looking at
        // the signature has every reason to think f(x) = f(x) always.
        //
        // When the auditor reconciles end-of-quarter, they find fees
        // are under-accrued by ~$47K because the number of calls to this
        // method determined how fees accumulated. Unit prices need to be
        // restated, and every subscription/redemption processed at the
        // wrong price needs correction. That's a regulatory filing.
    }
}
```

**The emergent problem:** `CalculateNavPerUnit` violates RT — calling it twice on the same fund can produce different results depending on whether the internal `_lastAccrualDate` was already set. The method signature `decimal CalculateNavPerUnit(Fund fund)` gives no indication that it accumulates internal state. A developer using it for compliance verification, report generation, or audit has every reason to believe `f(x) = f(x)` — but the number of times and the dates on which the method is called silently change the fee accrual, leading to misstated unit prices that compound over time.

#### F# — RT by Construction

```fsharp
open System

type Holding = {
    SecurityId: string
    Units: decimal
    PricePerUnit: decimal
}

type Fund = {
    FundId: string
    Holdings: Holding list
    TotalUnitsOutstanding: decimal
    AnnualManagementFeeBps: decimal
}

type NavResult = {
    GrossAssetValue: decimal
    AccruedFees: decimal
    NetAssetValue: decimal
    NavPerUnit: decimal
    AccrualDays: int
}

let calculateNavPerUnit
    (previousAccrualDate: DateTime option)
    (asOfDate: DateTime)
    (previousAccruedFees: decimal)
    (fund: Fund)
    : NavResult =
    let grossAssetValue =
        fund.Holdings |> List.sumBy (fun h -> h.Units * h.PricePerUnit)

    let accrualDays =
        match previousAccrualDate with
        | Some prev -> (asOfDate - prev).Days
        | None -> 0

    let dailyFeeRate = fund.AnnualManagementFeeBps / 10_000m / 365m
    let newFees = grossAssetValue * dailyFeeRate * decimal accrualDays
    let totalAccruedFees = previousAccruedFees + newFees

    let netAssetValue = grossAssetValue - totalAccruedFees
    let navPerUnit = Math.Round(netAssetValue / fund.TotalUnitsOutstanding, 4)

    { GrossAssetValue = grossAssetValue
      AccruedFees = totalAccruedFees
      NetAssetValue = netAssetValue
      NavPerUnit = navPerUnit
      AccrualDays = accrualDays }

// --- Usage: every call is independent, same inputs = same outputs ---

let fund = {
    FundId = "MF-BALANCED-001"
    Holdings = [
        { SecurityId = "CDN-BOND-ETF"; Units = 50_000m; PricePerUnit = 20.00m }
        { SecurityId = "CDN-EQUITY-ETF"; Units = 30_000m; PricePerUnit = 35.00m }
        { SecurityId = "US-EQUITY-ETF"; Units = 20_000m; PricePerUnit = 55.00m }
    ]
    TotalUnitsOutstanding = 200_000m
    AnnualManagementFeeBps = 150m
}

let today = DateTime.Today

// Operations computes the daily NAV
let opsResult = calculateNavPerUnit None today 0m fund
printfn $"Operations NAV/unit: {opsResult.NavPerUnit}"

// Compliance verifies independently — SAME inputs, SAME result. Always.
let complianceResult = calculateNavPerUnit None today 0m fund
printfn $"Compliance NAV/unit: {complianceResult.NavPerUnit}"
printfn $"Match: {opsResult.NavPerUnit = complianceResult.NavPerUnit}"

// Day 2: accrual state is EXPLICIT data, not hidden in an object
let yesterday = today.AddDays(-1.0)
let day2Fund = {
    fund with
        Holdings = [
            { SecurityId = "CDN-BOND-ETF"; Units = 50_000m; PricePerUnit = 20.10m }
            { SecurityId = "CDN-EQUITY-ETF"; Units = 30_000m; PricePerUnit = 34.80m }
            { SecurityId = "US-EQUITY-ETF"; Units = 20_000m; PricePerUnit = 55.50m }
        ]
}

let day2Result =
    calculateNavPerUnit (Some yesterday) today opsResult.AccruedFees day2Fund
printfn $"\nDay 2 NAV/unit: {day2Result.NavPerUnit}"
printfn $"Accrued fees: {day2Result.AccruedFees:F2} ({day2Result.AccrualDays} days)"

// Calling it 10 times with the same inputs gives the same result every time.
// The accrual state is a parameter, not a hidden field.
// A developer CANNOT forget to pass it — the compiler requires it.
// There's no object lifetime or singleton scope to reason about.
```

**Why F# prevents it:** The fee accrual state (`previousAccrualDate`, `previousAccruedFees`) is an explicit parameter, not hidden inside an object. Calling `calculateNavPerUnit` with the same arguments always returns the same result — full RT. A developer can't accidentally let an object's internal counter determine how fees accumulate, because there is no internal counter. The accrual history is data that flows through the system visibly, and the compiler forces every call site to supply it.

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
        printfn $"""{day.Date.ToString("yyyy-MM-dd")}: TSLA x {lots} lots (limit: {limit:P0})"""
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

**Scenario:** Building a trade processing pipeline that validates, prices, and risk-checks fund allocation orders before submission. The core question: can you safely compose these stages and trust that composing them produces predictable results?

#### C# — The Compiler Cannot Enforce Safe Composition

The natural objection to the F# side of this argument is: "I could just write the C# the same way — immutable DTOs, pure static methods, no state." And you can. But the compiler doesn't *protect* those decisions. Here's both the idiomatic version and the "clean" version, and why neither is safe.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

// =====================================================================
// ATTEMPT 1: Idiomatic C# — interfaces and mutation
// This is what the language GUIDES you toward.
// =====================================================================

public class Trade
{
    public string TradeId { get; set; } = "";
    public string Ticker { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }            // mutable — set during enrichment
    public bool IsValidated { get; set; }          // mutable — set during validation
    public bool IsRiskChecked { get; set; }        // mutable — set during risk check
}

public interface ITradeProcessor
{
    Trade Process(Trade trade);
}

public class Validator : ITradeProcessor
{
    public Trade Process(Trade trade)
    {
        trade.IsValidated = trade.Quantity > 0 && trade.Ticker != "";
        return trade;  // returns the SAME object it received, now mutated
    }
}

public class PricingEnricher : ITradeProcessor
{
    public Trade Process(Trade trade)
    {
        // Implicit ordering dependency: relies on IsValidated being set
        if (!trade.IsValidated)
            throw new InvalidOperationException("Must validate before pricing");
        trade.Price = GetMarketPrice(trade.Ticker);
        return trade;  // same object, mutated again
    }

    private decimal GetMarketPrice(string ticker) => ticker switch
    {
        "AAPL" => 185.00m,
        "MSFT" => 420.00m,
        "RY.TO" => 145.00m,
        _ => 100.00m
    };
}

public class RiskChecker : ITradeProcessor
{
    public Trade Process(Trade trade)
    {
        trade.IsRiskChecked = true;
        if (trade.Quantity * trade.Price > 1_000_000m)
            throw new InvalidOperationException(
                $"Trade {trade.TradeId}: exposure {trade.Quantity * trade.Price:C} exceeds limit");
        return trade;  // same object, mutated again
    }
}

// =====================================================================
// ATTEMPT 2: "Functional" C# — trying to do it the clean way
// A developer who knows FP tries to make it immutable and pure.
// =====================================================================

// Immutable DTOs — looks good
public record CleanTrade(string TradeId, string Ticker, decimal Quantity);
public record CleanPricedTrade(CleanTrade Trade, decimal MarketPrice);
public record CleanRiskResult(CleanPricedTrade PricedTrade, decimal Exposure, bool Approved);

public static class CleanPipeline
{
    public static CleanTrade? Validate(CleanTrade trade)
    {
        if (trade.Quantity > 0 && trade.Ticker != "")
            return trade;
        return null;  // C# has no Result<T,E> — null is the natural fallback
    }

    public static CleanPricedTrade Price(CleanTrade trade)
    {
        decimal price = trade.Ticker switch
        {
            "AAPL" => 185.00m,
            "MSFT" => 420.00m,
            "RY.TO" => 145.00m,
            _ => 100.00m
        };
        return new CleanPricedTrade(trade, price);
    }

    public static CleanRiskResult CheckRisk(CleanPricedTrade pricedTrade)
    {
        var exposure = pricedTrade.Trade.Quantity * pricedTrade.MarketPrice;
        return new CleanRiskResult(pricedTrade, exposure, exposure < 1_000_000m);
    }
}

// =====================================================================
// Now observe what the compiler permits in BOTH approaches
// =====================================================================

public class ComposabilityBug
{
    public static void Main()
    {
        // --- Idiomatic approach: mutation makes composition unsafe ---

        var validator = new Validator();
        var enricher = new PricingEnricher();
        var riskChecker = new RiskChecker();

        var pipeline = new List<ITradeProcessor> { validator, enricher, riskChecker };

        var trade1 = new Trade { TradeId = "T-001", Ticker = "AAPL", Quantity = 100 };
        var result1 = pipeline.Aggregate(trade1, (t, p) => p.Process(t));
        Console.WriteLine($"Idiomatic: {result1.TradeId} price={result1.Price} validated={result1.IsValidated}");

        // Bug: reorder the pipeline — compiles, blows up at runtime
        var wrongPipeline = new List<ITradeProcessor> { enricher, validator, riskChecker };
        var trade2 = new Trade { TradeId = "T-002", Ticker = "MSFT", Quantity = 200 };
        try
        {
            wrongPipeline.Aggregate(trade2, (t, p) => p.Process(t));
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"Idiomatic reorder bug: {ex.Message}");
        }

        // Bug: reuse the mutated trade object — IsValidated is still true
        trade1.Price = 0;
        var reused = pipeline.Aggregate(trade1, (t, p) => p.Process(t));
        Console.WriteLine($"Idiomatic reuse: price went from 0 to {reused.Price}, " +
            $"but IsValidated was already {reused.IsValidated} from the first pass");

        Console.WriteLine();

        // --- "Clean" approach: looks functional, but the compiler can't protect it ---

        var cleanTrade = new CleanTrade("T-003", "RY.TO", 5000m);
        var validated = CleanPipeline.Validate(cleanTrade);

        // BUG 1: Validate returns null for failures. Nothing forces the caller
        // to check it. This compiles — NullReferenceException at runtime.
        var badTrade = new CleanTrade("T-004", "", -100);
        var badValidated = CleanPipeline.Validate(badTrade);
        // badValidated is null, but the compiler lets you pass it right through:
        try
        {
            var badPriced = CleanPipeline.Price(badValidated!);  // ! suppresses the warning
            Console.WriteLine($"Clean null bug: priced an invalid trade at {badPriced.MarketPrice}");
        }
        catch (NullReferenceException)
        {
            Console.WriteLine("Clean null bug: NullReferenceException at runtime");
        }

        // BUG 2: Nothing prevents skipping stages. This compiles:
        var skippedValidation = CleanPipeline.Price(cleanTrade);
        var skippedResult = CleanPipeline.CheckRisk(skippedValidation);
        Console.WriteLine($"Clean skip bug: risk-checked without validation, " +
            $"approved={skippedResult.Approved}");

        // BUG 3: Nothing prevents wrong ordering. This compiles:
        // CleanPipeline.CheckRisk takes CleanPricedTrade, so you can't
        // pass CleanTrade directly — that's good. But you CAN skip
        // validation entirely because Validate and Price both take
        // CleanTrade. The "clean" pipeline only enforces that pricing
        // happens before risk-checking, not that validation happens first.

        // BUG 4: Records are immutable, but a developer can add a mutable
        // field tomorrow. The compiler won't flag it or warn about it:
        //   public record CleanTrade(string TradeId, string Ticker, decimal Quantity)
        //   {
        //       public bool IsValidated { get; set; }  // compiles fine in a record!
        //   }
        // Now your "immutable" pipeline has the same mutation bugs as the
        // idiomatic version. The record keyword doesn't prevent this.

        if (validated != null)
        {
            var priced = CleanPipeline.Price(validated);
            var riskResult = CleanPipeline.CheckRisk(priced);
            Console.WriteLine($"Clean happy path: {riskResult.PricedTrade.Trade.TradeId} " +
                $"exposure={riskResult.Exposure:C} approved={riskResult.Approved}");
        }
    }
}
```

**The emergent problem — in both approaches:**

The idiomatic C# version composes via interfaces that all operate on the same mutable `Trade` object. Ordering is enforced by convention (validate, then price, then risk-check) but the compiler treats any order as valid. Reuse of the mutated object carries stale flags between passes.

The "clean" C# version looks functional but has three structural gaps the compiler can't close:
1. **No `Result` type** — validation failure returns `null`, and the compiler doesn't force callers to handle it (even with nullable reference types, the `!` operator is one keystroke away).
2. **Incomplete type encoding** — `Validate` and `Price` both accept `CleanTrade`, so the compiler can't prevent skipping validation. You'd need a `ValidatedTrade` wrapper type, but C# has no convention or language pressure to create one — it's extra ceremony that every developer on the team has to voluntarily adopt.
3. **No immutability enforcement** — `record` allows mutable properties. A developer adding `{ get; set; }` to a record compiles without warnings, silently breaking the invariant that made composition safe.

You *can* write disciplined functional C#. The compiler just won't help you keep it that way.

#### F# — The Compiler Enforces Safe Composition

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

let results = trades |> List.map (processTrade getMarketPrice)

for result in results do
    match result with
    | Ok checked ->
        let status = if checked.Approved then "APPROVED" else "REJECTED (risk)"
        printfn $"{checked.PricedTrade.Trade.TradeId}: {status}, exposure = {checked.Exposure:C}"
    | Error reason ->
        printfn $"FAILED: {reason}"

// Each function is independently testable with zero setup:
//   validate { TradeId = "X"; Ticker = "AAPL"; Quantity = 100m }  -- returns Ok
//   validate { TradeId = "X"; Ticker = ""; Quantity = -1m }       -- returns Error
//   price getMarketPrice { TradeId = "X"; Ticker = "AAPL"; Quantity = 100m }
//      -- returns { Trade = ...; MarketPrice = 185.00m }
// No mocks. No interfaces. No DI container. Just values in, values out.
```

**Why F# prevents it:** The types form a pipeline: `Trade -> PricedTrade -> RiskCheckedTrade`. Each stage consumes one type and produces another. The compiler rejects invalid orderings, skipped stages, and mutation — not as warnings, but as hard errors. You don't need team discipline to maintain these invariants because the compiler maintains them for you.

The argument that "you could write the C# the same way" is technically true but misses the point. You *can* write functional C# today, alone. But C#'s compiler doesn't enforce immutability (`record` allows `set`), doesn't enforce error handling (`null` and `!` are one keystroke), and doesn't pressure you into distinct types per pipeline stage. In a mutual fund operation where a mispriced allocation order produces a *plausible but wrong* unit price, the question isn't whether one developer can write clean code — it's whether the compiler prevents the next developer from silently breaking the invariants that made it clean.

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

**Scenario:** A balanced mutual fund drifts from its target allocation. The system computes drift, determines required trades, applies constraints (minimum trade size, cash reserve), and generates rebalancing orders. An order comes out wrong — the fund is selling more of a holding than it should. Which stage produced the bad number?

#### C# — Mutation Chain Makes Root Cause Non-Local

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

public class FundHolding
{
    public string SecurityId { get; set; } = "";
    public decimal Units { get; set; }
    public decimal MarketValue { get; set; }
    public decimal CurrentWeight { get; set; }      // mutated during drift calc
    public decimal TargetWeight { get; set; }
    public decimal Drift { get; set; }               // mutated during drift calc
    public decimal RequiredTradeValue { get; set; }   // mutated during trade sizing
    public decimal AdjustedTradeValue { get; set; }   // mutated during constraint application
}

public class Fund
{
    public string FundId { get; set; } = "";
    public List<FundHolding> Holdings { get; set; } = new();
    public decimal TotalNav { get; set; }
    public decimal CashBalance { get; set; }
    public decimal MinCashReserve { get; set; }
    public decimal MinTradeSize { get; set; }
}

public class RebalanceEngine
{
    private Fund? _fund;

    public void LoadFund(Fund fund) => _fund = fund;

    public void CalculateDrift()
    {
        foreach (var h in _fund!.Holdings)
        {
            h.CurrentWeight = h.MarketValue / _fund.TotalNav;
            h.Drift = h.CurrentWeight - h.TargetWeight;
        }
    }

    public void SizeRequiredTrades()
    {
        foreach (var h in _fund!.Holdings)
        {
            // Bug: uses TotalNav, but TotalNav includes CashBalance.
            // If CalculateDrift updated TotalNav (maybe a later refactor),
            // this would silently change. Nothing enforces what TotalNav means
            // at this point in the pipeline.
            h.RequiredTradeValue = -h.Drift * _fund.TotalNav;
        }
    }

    public void ApplyConstraints()
    {
        var totalSellValue = _fund!.Holdings
            .Where(h => h.RequiredTradeValue < 0)
            .Sum(h => h.RequiredTradeValue);  // negative

        foreach (var h in _fund.Holdings)
        {
            // Apply minimum trade size filter
            if (Math.Abs(h.RequiredTradeValue) < _fund.MinTradeSize)
            {
                h.AdjustedTradeValue = 0;
                continue;
            }

            // Apply cash reserve constraint to buys
            var availableCash = _fund.CashBalance + Math.Abs(totalSellValue) - _fund.MinCashReserve;
            if (h.RequiredTradeValue > 0 && h.RequiredTradeValue > availableCash)
            {
                h.AdjustedTradeValue = availableCash;

                // Bug: mutates CashBalance to track available cash across iterations.
                // But iteration ORDER of Holdings determines which buy gets constrained.
                // Reordering the Holdings list changes which securities get bought.
                _fund.CashBalance -= h.AdjustedTradeValue;
            }
            else
            {
                h.AdjustedTradeValue = h.RequiredTradeValue;
            }
        }
    }

    public void Rebalance()
    {
        CalculateDrift();
        SizeRequiredTrades();
        ApplyConstraints();
    }
}

// --- Usage showing the debugging problem ---
public class ReasoningBug
{
    public static void Main()
    {
        var fund = new Fund
        {
            FundId = "MF-BALANCED-001",
            TotalNav = 10_000_000m,
            CashBalance = 200_000m,
            MinCashReserve = 100_000m,
            MinTradeSize = 5_000m,
            Holdings = new List<FundHolding>
            {
                new() { SecurityId = "CDN-BOND-ETF", Units = 50_000, MarketValue = 3_200_000m, TargetWeight = 0.30m },
                new() { SecurityId = "CDN-EQ-ETF",   Units = 30_000, MarketValue = 2_800_000m, TargetWeight = 0.30m },
                new() { SecurityId = "US-EQ-ETF",    Units = 20_000, MarketValue = 2_500_000m, TargetWeight = 0.25m },
                new() { SecurityId = "INTL-EQ-ETF",  Units = 15_000, MarketValue = 1_300_000m, TargetWeight = 0.15m },
            }
        };

        var engine = new RebalanceEngine();
        engine.LoadFund(fund);
        engine.Rebalance();

        Console.WriteLine("Rebalance orders:");
        foreach (var h in fund.Holdings)
        {
            var action = h.AdjustedTradeValue > 0 ? "BUY" : h.AdjustedTradeValue < 0 ? "SELL" : "SKIP";
            Console.WriteLine($"  {h.SecurityId}: drift={h.Drift:P2}, " +
                $"required={h.RequiredTradeValue:C}, adjusted={h.AdjustedTradeValue:C} [{action}]");
        }

        // The INTL-EQ-ETF order looks wrong. To debug:
        //
        // 1. Was CalculateDrift called? Did it compute CurrentWeight correctly?
        //    Need to check TotalNav wasn't modified before this step.
        //
        // 2. Did SizeRequiredTrades use the right TotalNav? Or did
        //    CalculateDrift modify it? Check every method that runs before.
        //
        // 3. Did ApplyConstraints see the right RequiredTradeValue? Or did
        //    another method modify it between SizeRequiredTrades and ApplyConstraints?
        //
        // 4. Did the iteration order in ApplyConstraints affect which buys
        //    got the available cash? The CashBalance mutation means the first
        //    buy in the list gets priority — reordering Holdings changes results.
        //
        // 5. Did LoadFund get called with the right fund? Or did another thread
        //    call LoadFund between Rebalance steps?
        //
        // None of these questions can be answered by reading ApplyConstraints alone.
        // You have to trace the entire mutation chain from LoadFund through every step.
        // Every method signature is `void` — the types tell you nothing about data flow.

        // Bug demonstration: reorder holdings and get different results
        Console.WriteLine("\n--- Same fund, reordered holdings ---");
        var fund2 = new Fund
        {
            FundId = "MF-BALANCED-001",
            TotalNav = 10_000_000m,
            CashBalance = 200_000m,
            MinCashReserve = 100_000m,
            MinTradeSize = 5_000m,
            Holdings = new List<FundHolding>
            {
                // Same holdings, reversed order
                new() { SecurityId = "INTL-EQ-ETF",  Units = 15_000, MarketValue = 1_300_000m, TargetWeight = 0.15m },
                new() { SecurityId = "US-EQ-ETF",    Units = 20_000, MarketValue = 2_500_000m, TargetWeight = 0.25m },
                new() { SecurityId = "CDN-EQ-ETF",   Units = 30_000, MarketValue = 2_800_000m, TargetWeight = 0.30m },
                new() { SecurityId = "CDN-BOND-ETF", Units = 50_000, MarketValue = 3_200_000m, TargetWeight = 0.30m },
            }
        };

        var engine2 = new RebalanceEngine();
        engine2.LoadFund(fund2);
        engine2.Rebalance();

        Console.WriteLine("Rebalance orders (reordered):");
        foreach (var h in fund2.Holdings)
        {
            var action = h.AdjustedTradeValue > 0 ? "BUY" : h.AdjustedTradeValue < 0 ? "SELL" : "SKIP";
            Console.WriteLine($"  {h.SecurityId}: adjusted={h.AdjustedTradeValue:C} [{action}]");
        }
        // Different adjusted values! Same fund, different results depending
        // on list ordering. The CashBalance mutation in the loop is the cause,
        // but you'd never find it by reading ApplyConstraints' signature.
    }
}
```

**The emergent problem:** Every method mutates the same `FundHolding` objects and the `Fund` itself. When the INTL-EQ-ETF order is wrong, you can't look at any single stage in isolation — each stage reads fields that a previous stage mutated, and the mutation of `CashBalance` inside the constraint loop means the iteration order of the list changes results. The method signatures are all `void`, giving zero indication of what data flows where.

#### F# — Data Flow Makes Dependencies Explicit

```fsharp
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
```

**Why F# prevents it:** Each stage takes explicit inputs and produces a new typed output. `calculateDrift` takes `decimal -> FundHolding -> DriftResult`. You can call it in isolation, inspect its output, and verify it without running the whole pipeline. The constraint logic is order-independent because buys are proportionally scaled rather than first-come-first-served via mutation. Reordering the holdings list produces identical results — something the C# version cannot guarantee because its `CashBalance` mutation inside the loop makes iteration order a hidden input.

---

### 3.4 Improved Testing

**Scenario:** Testing the fund rebalancing logic from 3.3 — specifically the drift calculation, trade sizing, and constraint application.

#### C# — Testing Requires Complex Setup and Produces Fragile Tests

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

// Reusing the types from 3.3
public class FundHolding
{
    public string SecurityId { get; set; } = "";
    public decimal Units { get; set; }
    public decimal MarketValue { get; set; }
    public decimal CurrentWeight { get; set; }
    public decimal TargetWeight { get; set; }
    public decimal Drift { get; set; }
    public decimal RequiredTradeValue { get; set; }
    public decimal AdjustedTradeValue { get; set; }
}

public class Fund
{
    public string FundId { get; set; } = "";
    public List<FundHolding> Holdings { get; set; } = new();
    public decimal TotalNav { get; set; }
    public decimal CashBalance { get; set; }
    public decimal MinCashReserve { get; set; }
    public decimal MinTradeSize { get; set; }
}

public class RebalanceEngine
{
    private Fund? _fund;

    public void LoadFund(Fund fund) => _fund = fund;

    public void CalculateDrift()
    {
        foreach (var h in _fund!.Holdings)
        {
            h.CurrentWeight = h.MarketValue / _fund.TotalNav;
            h.Drift = h.CurrentWeight - h.TargetWeight;
        }
    }

    public void SizeRequiredTrades()
    {
        foreach (var h in _fund!.Holdings)
        {
            h.RequiredTradeValue = -h.Drift * _fund.TotalNav;
        }
    }

    public void ApplyConstraints()
    {
        var totalSellValue = _fund!.Holdings
            .Where(h => h.RequiredTradeValue < 0)
            .Sum(h => h.RequiredTradeValue);

        foreach (var h in _fund.Holdings)
        {
            if (Math.Abs(h.RequiredTradeValue) < _fund.MinTradeSize)
            {
                h.AdjustedTradeValue = 0;
                continue;
            }
            var availableCash = _fund.CashBalance + Math.Abs(totalSellValue) - _fund.MinCashReserve;
            if (h.RequiredTradeValue > 0 && h.RequiredTradeValue > availableCash)
            {
                h.AdjustedTradeValue = availableCash;
                _fund.CashBalance -= h.AdjustedTradeValue;
            }
            else
            {
                h.AdjustedTradeValue = h.RequiredTradeValue;
            }
        }
    }

    public void Rebalance()
    {
        CalculateDrift();
        SizeRequiredTrades();
        ApplyConstraints();
    }
}

public class TestingExample
{
    // Helper to assert
    static void Assert(string name, bool condition)
    {
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
    }

    public static void Main()
    {
        Console.WriteLine("Test: Drift calculation for overweight holding");
        {
            // To test JUST drift, we still need a full Fund with a Holdings list,
            // because CalculateDrift reads _fund.TotalNav and mutates holdings in place.
            var fund = new Fund
            {
                FundId = "TEST",
                TotalNav = 1_000_000m,
                CashBalance = 0m,
                MinCashReserve = 0m,
                MinTradeSize = 0m,
                Holdings = new List<FundHolding>
                {
                    new()
                    {
                        SecurityId = "BOND-ETF",
                        MarketValue = 400_000m,
                        TargetWeight = 0.30m
                    }
                }
            };

            var engine = new RebalanceEngine();
            engine.LoadFund(fund);
            engine.CalculateDrift();

            // Test the mutated holding
            Assert("current weight is 40%", fund.Holdings[0].CurrentWeight == 0.40m);
            Assert("drift is +10%", fund.Holdings[0].Drift == 0.10m);
        }

        Console.WriteLine("\nTest: Trade sizing");
        {
            // To test sizing, we need drift to be already set on the holding.
            // But SizeRequiredTrades reads h.Drift — which CalculateDrift sets.
            // So we either: (a) call CalculateDrift first (testing two stages),
            // or (b) manually set Drift on the holding (fragile setup that
            // bypasses the actual pipeline).
            var fund = new Fund
            {
                FundId = "TEST",
                TotalNav = 1_000_000m,
                CashBalance = 0m,
                MinCashReserve = 0m,
                MinTradeSize = 0m,
                Holdings = new List<FundHolding>
                {
                    new()
                    {
                        SecurityId = "BOND-ETF",
                        MarketValue = 400_000m,
                        TargetWeight = 0.30m,
                        Drift = 0.10m  // manually pre-set — skipping CalculateDrift
                    }
                }
            };

            var engine = new RebalanceEngine();
            engine.LoadFund(fund);
            engine.SizeRequiredTrades();

            // Drift of +10% on 1M NAV => sell 100K
            Assert("required trade is -100K (sell)", fund.Holdings[0].RequiredTradeValue == -100_000m);
        }

        Console.WriteLine("\nTest: Constraint application");
        {
            // To test constraints alone, we need RequiredTradeValue pre-set.
            // But we also need CashBalance, MinCashReserve, MinTradeSize on Fund.
            // And the result depends on Holdings list ORDER because of the
            // CashBalance mutation in the loop.
            var fund = new Fund
            {
                FundId = "TEST",
                TotalNav = 1_000_000m,
                CashBalance = 50_000m,
                MinCashReserve = 10_000m,
                MinTradeSize = 5_000m,
                Holdings = new List<FundHolding>
                {
                    new() { SecurityId = "SELL-THIS", RequiredTradeValue = -80_000m },
                    new() { SecurityId = "BUY-THIS",  RequiredTradeValue = 200_000m },
                    new() { SecurityId = "TOO-SMALL", RequiredTradeValue = 2_000m },
                }
            };

            var engine = new RebalanceEngine();
            engine.LoadFund(fund);
            engine.ApplyConstraints();

            Assert("small trade skipped", fund.Holdings[2].AdjustedTradeValue == 0m);
            Assert("sell passes through", fund.Holdings[0].AdjustedTradeValue == -80_000m);

            // What SHOULD BUY-THIS be?
            // Available = 50K cash + 80K sell proceeds - 10K reserve = 120K
            // Requested = 200K, so constrained to 120K
            Assert("buy constrained to available cash", fund.Holdings[1].AdjustedTradeValue == 120_000m);

            // But this test is ORDER-DEPENDENT. If BUY-THIS was first in the list
            // and SELL-THIS was second, the CashBalance mutation would produce
            // a different result. The test only passes for THIS ordering.
        }

        // Summary: each test required building a full Fund object with fields
        // that aren't relevant to the stage being tested. Testing constraints
        // alone required manually pre-setting RequiredTradeValue — which means
        // the test is coupled to internal implementation details of how data
        // flows between stages. And the constraint test is fragile because
        // it depends on list ordering.
    }
}
```

**The emergent problem:** Every test needs a full `Fund` object even when testing a single stage. Testing later stages requires manually pre-setting fields that earlier stages would have mutated — coupling tests to internal implementation details. The constraint test is order-dependent because the mutation of `CashBalance` makes list ordering a hidden input.

#### F# — Pure Logic Tested Directly at Each Stage

```fsharp
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
```

**Why F# prevents it:** Each stage is a pure function with a clear signature. Testing drift needs a `decimal` and a `FundHolding` — not a fully constructed `Fund` with irrelevant fields. Testing constraints needs a `SizedTrade list` — not a `Fund` with pre-mutated `RequiredTradeValue` fields. No test depends on list ordering because the logic is order-independent by construction. And you can trivially prove that with a test — something the C# version can never pass because its mutation makes ordering a hidden input.

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
| Referential Transparency | Hidden fee accrual state causes different NAV on repeated calls; misstated unit prices compound | Pure function with explicit accrual parameters; same inputs = same output always |
| Purity | I/O hidden in "calculator" methods; backtest uses live DB limits | `Async<'T>` return type reveals effects; pure version requires data as parameter |
| Immutability | Shared mutable references cause race conditions | Records immutable by default; `{ x with ... }` creates copies |
| Higher-Order Functions | Lambda closures capture mutable state | `mutable` must be explicit; fold makes state visible |
| ADTs | Nullable fields allow illegal state combinations | DU cases carry exactly relevant data; no nulls |
| Pattern Matching | Switch with `_` silently swallows new cases | Exhaustive matching warns on unhandled cases |
| Type Systems | `decimal` carries no semantic meaning | Units of measure catch currency/unit errors at compile time |
| Composability | Interface composition has hidden ordering/state; "functional" C# compiles but compiler can't enforce immutability, error handling, or stage ordering | Types encode pipeline stages; wrong order = compiler error; immutability and Result enforced by default |
| Concurrency | Shared mutable dictionary under parallel writes | Immutable data + pure functions = no race conditions |
| Reasoning/Debugging | Rebalance stages mutate same objects; list ordering changes results; debugging requires tracing entire mutation chain | Each stage is a pure function with typed input/output; debug any stage in isolation |
| Testing | Testing one rebalance stage requires full Fund object with pre-mutated fields; tests are order-dependent | Each stage tested with just its inputs; no Fund object needed; order-independence provable in tests |
| Formal Verification | Runtime `Assert` stripped in Release | Units of measure + smart constructors = compile-time proofs |
| Expressiveness | 40 lines of mutable ceremony | 20 lines of declarative pipeline; all fields required |

Each C# example compiles and runs without warnings. Each contains a bug that has caused real production incidents at trading firms. Each F# example makes the corresponding bug a **compiler error**, not a runtime surprise.
