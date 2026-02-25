namespace Example1_2;

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;

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
public class Example
{
    public static void Run()
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