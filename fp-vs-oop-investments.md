# F# vs C# in Investments: Where OOP's Emergent Behaviour Bites

Real-world investment domain examples demonstrating how C#'s OOP permits subtle, production-breaking bugs that F#'s compiler structurally prevents.

---

## 1. Core Principles

### 1.1 Referential Transparency

**Scenario:** Portfolio valuation used in both risk reporting and order sizing.

#### C# — Hidden State Mutation Breaks Substitutability

```csharp
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

// The bug: calling GetNetAssetValue twice can produce different results
// (prices moved), but worse — the ORDER of calls matters because
// _lastPricedAt is silently updated. A developer refactoring risk
// reporting to cache the NAV doesn't realize IsStale() now returns
// stale timestamps because GetNetAssetValue was called elsewhere first.

var nav1 = valuator.GetNetAssetValue(portfolio);  // sets _lastPricedAt
Thread.Sleep(6 * 60 * 1000);
// Developer assumes IsStale() reflects their last explicit check
if (valuator.IsStale())  // true — but only because of nav1's side effect
{
    // Triggers unnecessary repricing, costing API credits and latency
    var nav2 = valuator.GetNetAssetValue(portfolio);
}
```

**The emergent problem:** `GetNetAssetValue` violates RT — you can't substitute its call with its return value because it also mutates `_lastPricedAt`. A developer reading only the call site has no idea ordering matters. In a trading system, this means risk limits computed with a "stale" flag that's actually reflecting a *different* valuation call's timestamp.

#### F# — RT by Construction

```fsharp
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

// Usage: the ValuationResult is a value. Calling getNetAssetValue
// twice gives two independent results. isStale takes a result
// explicitly — no hidden coupling. You CAN'T forget which
// valuation the staleness refers to.

let result = getNetAssetValue getPrice portfolio
// ... later ...
if isStale result then
    let freshResult = getNetAssetValue getPrice portfolio
    // freshResult is a completely independent value
```

**Why F# prevents it:** There's no mutable `_lastPricedAt` to silently couple two unrelated operations. The timestamp travels *with* the valuation as data. A developer can't accidentally create ordering dependencies because there's no hidden state to order against.

---

### 1.2 Purity

**Scenario:** Position sizing function used in both live trading and historical backtesting.

#### C# — Impure "Calculator" With Hidden Database Dependency

```csharp
public class PositionSizer
{
    private readonly IRiskLimits _riskLimits;

    public int CalculateLotSize(string ticker, decimal price, decimal portfolioValue)
    {
        var maxExposure = _riskLimits.GetMaxExposure(ticker); // DB call — fetches LIVE limits
        var lots = (int)(portfolioValue * maxExposure / price);
        return lots;
    }
}

// Live trading: works correctly. GetMaxExposure returns today's limits.
var lots = sizer.CalculateLotSize("AAPL", currentPrice, portfolioValue);

// Backtesting: silently produces wrong results.
// A quant reuses the same PositionSizer to simulate 2019–2023 performance.
foreach (var day in backtestDays)  // iterating over historical data
{
    var lots = sizer.CalculateLotSize("AAPL", day.Price, day.PortfolioValue);
    simulation.RecordPosition("AAPL", lots, day.Date);
}

// The method signature is: int CalculateLotSize(string, decimal, decimal)
// Nothing tells the caller it reaches into a database.
//
// The bug: GetMaxExposure returns TODAY's risk limits — which were
// tightened in 2022 after the fund took a large drawdown. The backtest
// is applying post-drawdown limits to pre-drawdown market data.
//
// Result: the simulation shows the strategy would have AVOIDED the
// 2022 drawdown entirely. The quant presents this to the PM as
// evidence the strategy is robust. In reality, the strategy only
// "avoided" the drawdown because limits tightened AFTER it happened.
// The backtest has look-ahead bias baked into every single position
// size — and the numbers look perfectly plausible.
//
// This is not caught by:
// - Code review (method looks like arithmetic)
// - Unit tests (they mock IRiskLimits and return whatever the test sets)
// - Integration tests (they use a test DB with test limits)
// - The backtest itself (results are plausible, just subtly wrong)
//
// It's caught six months later when the strategy underperforms live
// because real-time limits are looser than the quant's model assumed.
```

**The emergent problem:** The method *looks* like arithmetic but performs I/O. The C# compiler treats `decimal GetMaxExposure(...)` identically to pure computation. The method signature `int CalculateLotSize(string, decimal, decimal)` gives zero indication that calling it fetches external state — so a developer reusing it for backtesting has no reason to suspect the results are contaminated with future knowledge.

#### F# — Effects Are Visible in Types

```fsharp
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

// Backtesting: uses the PURE function with historical limits as data
let backtestResults =
    backtestDays
    |> List.map (fun day ->
        // historicalLimits is a Map<DateTime, decimal> loaded once from
        // a historical dataset — not fetched from live DB per iteration
        let limit = historicalLimits |> Map.find day.Date
        calculateLotSize limit day.Price day.PortfolioValue)

// The critical difference: a developer CANNOT accidentally use
// calculateLotSizeLive in a backtest loop without confronting its
// Async<int> return type. The compiler forces them to handle the
// async context, which immediately raises the question: "Why is
// my backtest doing async I/O?" That question leads directly to
// discovering the look-ahead bias.
//
// Meanwhile, the pure calculateLotSize takes maxExposure as a
// parameter — the caller MUST supply it, making the data source
// an explicit decision rather than a hidden implementation detail.
```

**Why F# prevents it:** The pure function `calculateLotSize` takes `decimal -> decimal -> decimal -> int`. You literally *cannot* perform I/O inside it without changing the return type to `Async<int>`, which the compiler would flag at every call site. The effectful version's type signature (`Async<int>`) screams "I do I/O" — a developer trying to use it in a tight backtest loop is forced to confront the async machinery, which makes the hidden database dependency impossible to overlook. More importantly, the pure version *requires* `maxExposure` as an explicit parameter, turning the data source from a hidden implementation detail into a conscious architectural choice at every call site.

---

### 1.3 Immutability

**Scenario:** Concurrent risk aggregation across multiple portfolios sharing position references.

#### C# — Shared Mutable Position Objects

```csharp
public class Position
{
    public string Ticker { get; set; }
    public decimal Quantity { get; set; }
    public decimal MarketValue { get; set; }  // mutable!
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

// The bug: Portfolio A and Portfolio B share Position objects
// (common when a fund has sub-portfolios viewing the same book)
var sharedAAPL = new Position { Ticker = "AAPL", Quantity = 1000 };
portfolioA.Positions.Add(sharedAAPL);
portfolioB.Positions.Add(sharedAAPL);  // same reference!

// Two risk threads run simultaneously
Task.Run(() => riskAggregator.UpdateMarketValues(portfolioA.Positions, livePrice));
Task.Run(() => riskAggregator.UpdateMarketValues(portfolioB.Positions, delayedPrice));

// sharedAAPL.MarketValue is now a race condition:
// - Could reflect live price (from portfolio A's thread)
// - Could reflect delayed price (from portfolio B's thread)
// - Could reflect a torn read (partially written decimal)
//
// Risk report shows Portfolio A with delayed prices
// while Portfolio B shows live prices. Compliance flags
// a $2M discrepancy that doesn't actually exist.
```

**The emergent problem:** The `set` accessor on `MarketValue` means any thread with a reference can mutate the object. The shared reference between portfolios is invisible at the type level — nothing stops two risk calculations from stomping on each other's results through the same object.

#### F# — Immutable Records Force Explicit Data Flow

```fsharp
type Position = {
    Ticker: string
    Quantity: decimal
    MarketValue: decimal
}

let updateMarketValues (getPrice: string -> decimal) (positions: Position list) : Position list =
    positions
    |> List.map (fun pos ->
        { pos with MarketValue = pos.Quantity * getPrice pos.Ticker })
    // Returns a NEW list of NEW position records — originals untouched

// Each portfolio gets its own independent valuations
let portfolioAValued =
    updateMarketValues getLivePrice portfolioA.Positions
    |> Async.StartAsTask

let portfolioBValued =
    updateMarketValues getDelayedPrice portfolioB.Positions
    |> Async.StartAsTask

// Even if portfolioA and portfolioB share position records in their
// original lists, `{ pos with ... }` creates a COPY. The original
// is never modified. No race condition is possible — each task
// produces an entirely independent result list.
//
// Attempting `pos.MarketValue <- newValue` is a compiler error.
```

**Why F# prevents it:** Record fields are immutable by default. `{ pos with MarketValue = ... }` is syntactic sugar for creating a new record — the original is structurally frozen. You can't create a race condition on data that no thread can write to. The compiler error on mutation isn't a lint warning you can suppress — it's a hard type error.

---

## 2. Language Features

### 2.1 Higher-Order Functions

**Scenario:** Building a trade filtering and transformation pipeline for order routing.

#### C# — Lambda Closures Capturing Mutable State

```csharp
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

// The bugs:
// 1. If LINQ evaluates lazily and you iterate twice, _totalExposure
//    doubles. Add a .Count() call before .ToList()? Exposure is 2x.
// 2. The second .Where filters based on RunningExposure, but that
//    value depends on iteration order — which LINQ doesn't guarantee
//    for all providers (e.g., PLINQ reorders).
// 3. Calling ProcessTrades twice without resetting _totalExposure
//    means the second batch starts from the first batch's total.

var batch1 = router.ProcessTrades(morningTrades);   // _totalExposure = 500,000
var batch2 = router.ProcessTrades(afternoonTrades); // starts at 500,000!
// Afternoon trades incorrectly hit the exposure limit
```

**The emergent problem:** C# allows side effects inside LINQ lambdas with no compiler warning. The `Select` is supposed to be a pure mapping operation, but the closure over `_totalExposure` makes it stateful. Lazy evaluation, multiple enumeration, and parallel execution all produce different results from the same input.

#### F# — Pipeline Composition Without Hidden State

```fsharp
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

// The accumulation is EXPLICIT via fold — not hidden in a lambda.
// Each call starts fresh: TotalExposure = 0m.
// No mutable variable to accidentally persist between batches.
// List.fold has defined left-to-right ordering — no ambiguity.

let batch1 = processTradesWithExposure morningTrades    // independent
let batch2 = processTradesWithExposure afternoonTrades  // independent
```

**Why F# prevents it:** `List.map` in F# returns a new list — attempting to mutate a captured variable inside it requires a `mutable` binding, which the compiler forces you to declare explicitly. The idiomatic approach (fold) makes the state threading *visible in the function signature*. There's no implicit accumulator hiding in a closure.

---

### 2.2 Algebraic Data Types

**Scenario:** Modeling order execution states in a trading system.

#### C# — Null-Based State With Incomplete Hierarchies

```csharp
public class ExecutionReport
{
    public string OrderId { get; set; }
    public string Status { get; set; }  // "Filled", "Partial", "Rejected"
    public decimal? FilledQuantity { get; set; }  // null if rejected
    public decimal? FilledPrice { get; set; }     // null if rejected
    public string? RejectionReason { get; set; }  // null if filled
    public decimal? RemainingQuantity { get; set; } // null if fully filled
}

public decimal CalculateSlippage(ExecutionReport report, decimal expectedPrice)
{
    // Developer checks Status but forgets a case
    if (report.Status == "Filled")
    {
        return report.FilledPrice.Value - expectedPrice;  // .Value can throw!
    }
    else if (report.Status == "Partial")
    {
        return report.FilledPrice.Value - expectedPrice;
    }
    // Forgot "Rejected" — falls through, returns 0
    // Also: what about "PartiallyRejected"? "Cancelled"? "Expired"?
    return 0m;
}

// The bugs:
// 1. A new Status value "Expired" is added. No compiler error.
//    CalculateSlippage silently returns 0 for expired orders.
// 2. FilledPrice is nullable but .Value is called without null check.
//    A "Partial" fill with null FilledPrice throws NullReferenceException
//    at 3 AM during Asian market hours.
// 3. String-based status means typos compile: Status = "Fileld"
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

// Illegal states are impossible:
// - A Filled report ALWAYS has a filledPrice (it's not optional)
// - A Rejected report NEVER has a filledPrice (field doesn't exist on that case)
// - You can't construct Filled("123", ...) with a null price — decimal isn't nullable
```

**Why F# prevents it:** Each DU case carries exactly the data relevant to that state. There's no "Filled with null price" because the `Filled` case *requires* a `decimal`. The exhaustiveness check is a compiler warning (promotable to error with `<TreatWarningsAsErrors>`) — adding `| Cancelled of orderId: string * reason: string` immediately surfaces every `match` that needs updating.

---

### 2.3 Pattern Matching

**Scenario:** Routing orders to different execution venues based on instrument characteristics.

#### C# — Switch With Silent Fallthrough and No Exhaustiveness

```csharp
public enum AssetClass { Equity, FixedIncome, Derivative, Crypto }

public class VenueRouter
{
    public string RouteOrder(Order order)
    {
        // C# switch expression — better than statements, but...
        return order.AssetClass switch
        {
            AssetClass.Equity => RouteEquity(order),
            AssetClass.FixedIncome => RouteFixed(order),
            AssetClass.Derivative => RouteDerivative(order),
            // Forgot Crypto — compiler allows it with a default/discard
            _ => "DEFAULT_VENUE"  // silently catches everything else
        };
    }

    // Six months later, someone adds AssetClass.FX to the enum.
    // No compiler error. FX orders silently route to DEFAULT_VENUE.
    // DEFAULT_VENUE charges 5x the spread. Nobody notices for weeks
    // because the orders execute successfully — just expensively.
}

// Worse: nested routing with complex conditions
public decimal CalculateFee(Order order)
{
    if (order.AssetClass == AssetClass.Equity && order.Value > 100_000)
        return order.Value * 0.001m;
    else if (order.AssetClass == AssetClass.Equity)
        return order.Value * 0.002m;
    else if (order.AssetClass == AssetClass.FixedIncome)
        return order.Value * 0.0005m;
    // Derivative and Crypto both fall to...
    return 0m;  // FREE TRADES! Bug: derivatives trade at zero commission
}
```

**The emergent problem:** The `_ => ...` discard pattern and the implicit `return 0m` at the end of if-else chains silently swallow unhandled cases. C# doesn't warn on non-exhaustive enum switches unless you enable specific analyzers — and even then, the `_` discard suppresses it.

#### F# — Exhaustive Matching With Destructuring

```fsharp
type AssetClass = Equity | FixedIncome | Derivative | Crypto

type InstrumentDetails =
    | EquityDetails of exchange: string * sector: string
    | BondDetails of maturity: DateTime * couponRate: decimal
    | DerivativeDetails of underlying: string * expiry: DateTime * optionType: OptionType
    | CryptoDetails of chain: string * dex: bool

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
    // This is a compiler WARNING — cannot be ignored in CI with strict settings.

let calculateFee (order: Order) : decimal =
    match order.Details, order.Value with
    | EquityDetails _, v when v > 100_000m -> v * 0.001m
    | EquityDetails _, v -> v * 0.002m
    | BondDetails _, v -> v * 0.0005m
    | DerivativeDetails _, v -> v * 0.0015m
    | CryptoDetails _, v -> v * 0.003m
    // Every case accounted for. No implicit zero. No silent fallthrough.
```

**Why F# prevents it:** F# pattern matching on DUs is exhaustive by default. There's no `_` needed unless you explicitly want a catch-all — and the idiomatic practice is to list all cases. When you add a new case to the DU, the compiler tells you every function that needs updating. You physically can't add `FXDetails` without touching every `match` in the codebase.

---

### 2.4 Type Inference and Advanced Type Systems

**Scenario:** Currency conversion errors in a multi-currency portfolio.

#### C# — Decimals Are Just Decimals

```csharp
public class FxConverter
{
    public decimal ConvertToBase(decimal amount, string fromCurrency, string toCurrency)
    {
        var rate = GetRate(fromCurrency, toCurrency);
        return amount * rate;
    }
}

// The bug: nothing prevents mixing currencies
decimal usdPosition = 1_000_000m;
decimal jpyPosition = 150_000_000m;  // 150M yen

// This compiles and runs — adding USD to JPY
decimal totalNAV = usdPosition + jpyPosition;  // 151,000,000 — meaningless number

// Worse: applying the wrong conversion direction
decimal rate = GetRate("USD", "JPY");  // ~150
decimal converted = jpyPosition * rate;  // multiplied JPY by USD/JPY rate
// Result: 22,500,000,000 — should have DIVIDED
// This showed up as a $22B position in the risk report.
// The PM called the CRO at midnight.
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

// Correct conversion: USD * (JPY/USD) = JPY ✓
let usdInJpy : decimal<JPY> = usdPosition * usdJpyRate

// Wrong direction won't compile:
// let wrong = jpyPosition * usdJpyRate
// Error: expected decimal<JPY * JPY/USD> but got... — units don't cancel

// Correct: JPY / (JPY/USD) = USD ✓
let jpyInUsd : decimal<USD> = jpyPosition / usdJpyRate

// Now you can add them
let totalNAV : decimal<USD> = usdPosition + jpyInUsd  // type-safe addition
```

**Why F# prevents it:** Units of measure are erased at runtime (zero performance cost) but checked at compile time. The type system literally does dimensional analysis — if the units don't cancel correctly, the code won't compile. You can't accidentally add USD to JPY, and you can't apply a conversion rate in the wrong direction. This is the kind of bug that has actually caused eight-figure losses at real trading firms.

---

## 3. Practical Benefits

### 3.1 Composability

**Scenario:** Building a pricing pipeline that validates, enriches, prices, and risk-checks trades.

#### C# — Interface Composition With Emergent State Interactions

```csharp
public interface ITradeProcessor { Trade Process(Trade trade); }

public class Validator : ITradeProcessor
{
    private int _validationCount = 0;  // hidden state
    public Trade Process(Trade trade)
    {
        _validationCount++;
        if (_validationCount > 1000)  // rate limiting — but hidden!
            throw new RateLimitException();
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
}

// The pipeline LOOKS composable:
var pipeline = new List<ITradeProcessor> { validator, enricher, riskChecker };
var result = pipeline.Aggregate(trade, (t, processor) => processor.Process(t));

// But the Aggregate hides:
// 1. Ordering dependency: enricher throws if validator didn't run
// 2. Mutation: each processor modifies the SAME trade object
// 3. Hidden state: validator's rate limit counter persists across calls
// 4. Reuse hazard: running the pipeline twice on the same trade
//    sets IsValidated = true on first pass, so second pass skips validation
//    even if the trade was modified between passes
```

**The emergent problem:** Each `ITradeProcessor` can have internal state and can mutate its input. "Composition" here is really sequential mutation of a shared object, with implicit ordering requirements and hidden rate limits. Reordering processors or reusing the pipeline produces completely different behaviors — none of which the type system catches.

#### F# — Function Composition With Explicit Data Flow

```fsharp
type ValidationResult = Valid of Trade | Invalid of Trade * string
type PricedTrade = { Trade: Trade; MarketPrice: decimal }
type RiskCheckedTrade = { PricedTrade: PricedTrade; RiskScore: decimal; Approved: bool }

let validate (trade: Trade) : ValidationResult =
    if trade.Quantity > 0m && trade.Ticker <> ""
    then Valid trade
    else Invalid (trade, "Invalid quantity or ticker")

let price (getMarketPrice: string -> decimal) (trade: Trade) : PricedTrade =
    { Trade = trade; MarketPrice = getMarketPrice trade.Ticker }

let checkRisk (maxRisk: decimal) (pricedTrade: PricedTrade) : RiskCheckedTrade =
    let exposure = pricedTrade.Trade.Quantity * pricedTrade.MarketPrice
    { PricedTrade = pricedTrade; RiskScore = exposure; Approved = exposure < maxRisk }

// Compose with Result chaining — ordering is enforced by TYPES
let processTrade (getPrice: string -> decimal) (trade: Trade) : Result<RiskCheckedTrade, string> =
    match validate trade with
    | Invalid (_, reason) -> Error reason
    | Valid validTrade ->
        validTrade
        |> price getPrice
        |> checkRisk 1_000_000m
        |> Ok

// You CAN'T call `price` before `validate` with the wrong type.
// You CAN'T call `checkRisk` on an unpriced trade — it needs PricedTrade.
// There's no shared mutable trade object to corrupt.
// No hidden counters. No ordering ambiguity.
// Each function is independently testable with zero setup.
```

**Why F# prevents it:** The types form a pipeline: `Trade -> ValidationResult -> PricedTrade -> RiskCheckedTrade`. You physically can't reorder the stages because the types won't align. Each function is pure — no internal counters, no mutation. Composition is mathematical: `f >> g >> h` always behaves the same regardless of how many times you call it.

---

### 3.2 Concurrency and Parallelism Safety

**Scenario:** Real-time P&L aggregation across multiple trading desks.

#### C# — Shared Mutable Dictionary Under Concurrent Access

```csharp
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
}

// Multiple desks update simultaneously
Parallel.ForEach(desks, desk =>
{
    aggregator.UpdateDeskPnl(desk.Name, desk.Trades);
});

// Bugs:
// 1. Dictionary is not thread-safe. Concurrent writes can corrupt
//    internal hash table, causing lost updates or exceptions.
// 2. Even with ConcurrentDictionary, the read-modify-write pattern
//    (check-then-add) is not atomic — two threads can read the same
//    value, add to it independently, and one update is lost.
// 3. GetTotalPnl() called while updates are running reads a
//    partially-updated state. Risk dashboard shows $5M P&L when
//    it should be $8M — just because Desk C hasn't updated yet.
// 4. The compiler gives zero warnings about any of this.
```

#### F# — Immutable Aggregation With Atomic Results

```fsharp
let calculateDeskPnl (trades: Trade list) : decimal =
    trades |> List.sumBy (fun t -> t.RealizedPnl)

let aggregatePnl (desks: Desk list) : Map<string, decimal> =
    desks
    |> List.map (fun desk ->
        // Each computation is independent — no shared state
        async { return desk.Name, calculateDeskPnl desk.Trades })
    |> Async.Parallel     // safe: no shared mutable state
    |> Async.RunSynchronously
    |> Map.ofArray        // atomic: map is created all at once

let totalPnl (pnlByDesk: Map<string, decimal>) : decimal =
    pnlByDesk |> Map.toSeq |> Seq.sumBy snd

// The result is an immutable Map. You either have the complete,
// consistent result or you don't have it yet. There's no
// "partially updated" state. No race conditions. No locks.
// Map is immutable — reading it from any thread is always safe.
```

**Why F# prevents it:** There's no shared mutable dictionary. Each desk's P&L is computed independently in a pure function, and the results are combined into an immutable `Map` atomically. You can't observe an intermediate state because there isn't one — the `Map` only exists once all computations complete.

---

### 3.3 Easier Reasoning, Debugging, and Refactoring

**Scenario:** Debugging why a portfolio's Greeks (delta, gamma) are wrong.

#### C# — Mutation Chain Makes Root Cause Non-Local

```csharp
public class GreeksCalculator
{
    private Portfolio _portfolio;

    public void LoadPortfolio(Portfolio p) => _portfolio = p;

    public void CalculateDelta()
    {
        foreach (var pos in _portfolio.Positions)
        {
            pos.Delta = ComputeDelta(pos);
            pos.AdjustedQuantity = pos.Quantity * pos.Delta;  // mutation
        }
    }

    public void CalculateGamma()
    {
        foreach (var pos in _portfolio.Positions)
        {
            // Bug: uses AdjustedQuantity (set by CalculateDelta) instead of Quantity.
            // But this only breaks if CalculateDelta was called first.
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

// To debug wrong gamma values, you need to verify:
// 1. Was CalculateDelta called before CalculateGamma?
// 2. Did anything else modify AdjustedQuantity between calls?
// 3. Is _portfolio the same reference that was loaded?
// 4. Did another thread call LoadPortfolio() mid-calculation?
// None of these are visible at the call site of CalculateGamma().
```

#### F# — Data Flow Makes Dependencies Explicit

```fsharp
type PositionWithDelta = { Position: Position; Delta: decimal; AdjustedQuantity: decimal }
type PositionWithGreeks = { PositionWithDelta: PositionWithDelta; Gamma: decimal }

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
    |> calculateGammas   // takes PositionWithDelta, not Position

// To debug wrong gamma: look at calculateGammas. Its ONLY input is
// PositionWithDelta list. No hidden state. No temporal coupling.
// The type signature tells you deltas were already calculated.
// You can't call calculateGammas with raw positions — compiler error.
```

**Why F# prevents it:** The dependency between delta and gamma calculations is encoded in the types. `calculateGammas` requires `PositionWithDelta list`, not `Position list` — so it's structurally impossible to call it without deltas having been computed first. Debugging is local: you only need to look at the function's inputs, not at the entire history of mutations across a shared object graph.

---

### 3.4 Improved Testing

**Scenario:** Testing a margin call detection system.

#### C# — Testing Requires Complex Setup and Mocking

```csharp
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
        var account = _accountService.GetAccount(accountId);  // DB call
        var positions = _accountService.GetPositions(accountId);  // DB call

        var marketValue = positions
            .Sum(p => p.Quantity * _pricingService.GetPrice(p.Ticker));  // API call

        var marginRatio = marketValue / account.LoanAmount;

        if (marginRatio < account.MaintenanceMargin)
        {
            _notificationService.SendMarginCall(accountId, marginRatio);  // email
            _auditLog.Record($"Margin call: {accountId}");  // DB write
            return true;
        }
        return false;
    }
}

// Test requires mocking 4 interfaces:
[Test]
public void TestMarginCall()
{
    var mockAccount = new Mock<IAccountService>();
    var mockPricing = new Mock<IPricingService>();
    var mockNotifier = new Mock<INotificationService>();
    var mockAudit = new Mock<IAuditLog>();

    mockAccount.Setup(x => x.GetAccount("ACC1"))
        .Returns(new Account { LoanAmount = 100_000, MaintenanceMargin = 0.3m });
    mockAccount.Setup(x => x.GetPositions("ACC1"))
        .Returns(new List<Position> { new() { Ticker = "AAPL", Quantity = 100 } });
    mockPricing.Setup(x => x.GetPrice("AAPL")).Returns(200m);

    var detector = new MarginCallDetector(
        mockAccount.Object, mockPricing.Object,
        mockNotifier.Object, mockAudit.Object);

    var result = detector.CheckMarginCall("ACC1");

    Assert.IsTrue(result);
    mockNotifier.Verify(x => x.SendMarginCall("ACC1", It.IsAny<decimal>()), Times.Once);
    // 20 lines of setup for 1 line of assertion
    // And we're not testing the LOGIC — we're testing the WIRING
}
```

#### F# — Pure Logic Tested Directly, Effects Tested Separately

```fsharp
// Pure logic — zero dependencies
let calculateMarginRatio (positions: (decimal * decimal) list) (loanAmount: decimal) : decimal =
    let marketValue = positions |> List.sumBy (fun (qty, price) -> qty * price)
    marketValue / loanAmount

let isMarginCall (maintenanceMargin: decimal) (marginRatio: decimal) : bool =
    marginRatio < maintenanceMargin

// Tests are trivial — just call functions with values
[<Test>]
let ``margin call triggered when ratio below maintenance`` () =
    let ratio = calculateMarginRatio [(100m, 200m)] 100_000m  // = 0.2
    Assert.IsTrue(isMarginCall 0.3m ratio)

[<Test>]
let ``no margin call when ratio above maintenance`` () =
    let ratio = calculateMarginRatio [(100m, 500m)] 100_000m  // = 0.5
    Assert.IsFalse(isMarginCall 0.3m ratio)

[<Test>]
let ``multi-position margin calculation`` () =
    let ratio = calculateMarginRatio [(100m, 200m); (50m, 300m)] 100_000m
    Assert.AreEqual(0.35m, ratio)

// 3 tests, 6 lines total, testing actual business logic.
// No mocks. No interfaces. No setup. No teardown.
// Edge cases are trivial to add:
// - Zero loan? Empty positions? Negative prices? Just pass values.
```

**Why F# prevents it:** By separating pure calculation from effectful orchestration, the business logic (`calculateMarginRatio`, `isMarginCall`) becomes plain functions from values to values. You don't need to mock a pricing service to test margin math — you just pass `(100m, 200m)` as a position. The effectful wiring (fetching prices, sending emails) is a thin layer at the boundary, tested separately if needed.

---

### 3.5 Formal Verification and Correctness

**Scenario:** Ensuring trade settlement amounts are computed correctly across FX conversions.

#### C# — Runtime Assertion Is the Best You Can Do

```csharp
public decimal CalculateSettlement(Trade trade, decimal fxRate, string baseCurrency)
{
    // Developer must manually ensure correctness
    Debug.Assert(fxRate > 0, "FX rate must be positive");
    Debug.Assert(trade.Quantity > 0, "Quantity must be positive");

    var localAmount = trade.Quantity * trade.Price;

    // Is this right? Multiply or divide?
    // Depends on whether fxRate is quoted as base/foreign or foreign/base
    // Nothing in the type system tells you
    var baseAmount = trade.Currency == baseCurrency
        ? localAmount
        : localAmount * fxRate;  // or should this be / fxRate?

    return baseAmount;
}

// Debug.Assert is stripped in Release builds.
// The fxRate direction ambiguity has caused real-world settlement failures.
// Nothing prevents calling this with a negative quantity — the Assert
// only fires in Debug mode, and even then, it doesn't prevent execution.
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

// Compile-time guarantees:
// 1. qty is always positive (can't construct PositiveDecimal with negative)
// 2. price has units of CAD/shares — dimensional analysis prevents misuse
// 3. fxRate is USD/CAD — you CAN'T accidentally pass CAD/USD
//    (the units wouldn't cancel to produce USD)
// 4. The result is USD — guaranteed by the type system
// 5. None of this exists at runtime — zero performance cost
```

**Why F# prevents it:** The type system acts as a proof assistant. The compiler verifies that `shares * (CAD/shares) * (USD/CAD) = USD` through dimensional analysis. A developer can't accidentally multiply when they should divide, because the units would produce a nonsensical type like `CAD²/shares` instead of `USD`. The `PositiveDecimal` smart constructor makes negative quantities *unrepresentable* — not "checked at runtime," but structurally impossible.

---

### 3.6 Expressiveness and Conciseness

**Scenario:** Processing a batch of trade confirmations and generating settlement instructions.

#### C# — Ceremony-Heavy Imperative Processing

```csharp
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
                var rate = GetFxRate(conf.Currency, "USD");
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
// 40 lines. Multiple mutation points. Silent null skip. Mutable instruction
// built up incrementally. Easy to add a field and forget to set it.
```

#### F# — Declarative Pipeline

```fsharp
type ApprovalRequirement =
    | NoApproval
    | StandardApproval
    | SeniorApproval

type SettlementInstruction = {
    TradeId: string
    Amount: decimal
    SettlementAmount: decimal
    Currency: string
    Approval: ApprovalRequirement
}

let processConfirmations
    (getFxRate: string -> string -> decimal option)
    (confirmations: TradeConfirmation list) : Result<SettlementInstruction, string> list =
    confirmations
    |> List.filter (fun c -> c.Status = Confirmed && c.SettlementDate >= DateTime.Today)
    |> List.map (fun conf ->
        let amount = conf.Quantity * conf.Price
        let converted =
            if conf.Currency = "USD" then Ok amount
            else
                getFxRate conf.Currency "USD"
                |> Option.map (fun rate -> amount * rate)
                |> Option.toResult $"No FX rate for {conf.Currency}"

        converted |> Result.map (fun settlementAmount ->
            { TradeId = conf.TradeId
              Amount = amount
              SettlementAmount = settlementAmount
              Currency = "USD"
              Approval =
                  if settlementAmount > 10_000_000m then SeniorApproval
                  elif settlementAmount > 1_000_000m then StandardApproval
                  else NoApproval }))
// 20 lines. No mutation. No nulls. Missing FX rate is an explicit Error,
// not a silent skip. Every field is set at construction — you can't
// forget one (compiler error). ApprovalRequirement is a DU, not a
// nullable bool + nullable enum combination.
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
