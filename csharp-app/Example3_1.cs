namespace Example3_1;

using System;
using System.Collections.Generic;
using System.Linq;

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

public class Example
{
    public static void Run()
    {
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












































/* 
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


public class Example3_1
{
    public static void RunExample()
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
} */