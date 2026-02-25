namespace Example1_3;

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


public class Example
{
    public static void Run()
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