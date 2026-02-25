namespace Example3_2;

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

public class Example
{
    public static void Run()
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