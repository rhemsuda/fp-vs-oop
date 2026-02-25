namespace Example3_4;

using System;
using System.Collections.Generic;
using System.Linq;


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

public class Example
{
    // Helper to assert
    static void Assert(string name, bool condition)
    {
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
    }

    public static void Run()
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