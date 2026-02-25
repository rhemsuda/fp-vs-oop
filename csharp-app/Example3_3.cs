namespace Example3_3;

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

public class Example
{
    public static void Run()
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