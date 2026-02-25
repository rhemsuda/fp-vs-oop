namespace Example1_1;

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


public class Example
{
    public static void Run()
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