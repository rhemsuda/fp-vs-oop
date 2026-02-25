module Example1_1

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


// Calling it 10 times with the same inputs gives the same result every time.
// The accrual state is a parameter, not a hidden field.
// A developer CANNOT forget to pass it — the compiler requires it.
// There's no object lifetime or singleton scope to reason about.
let runExample1_1 =
    let day2Result =
        calculateNavPerUnit (Some yesterday) today opsResult.AccruedFees day2Fund    
    printfn $"Day 2 NAV/unit: {day2Result.NavPerUnit}"
    printfn $"Accrued fees: {day2Result.AccruedFees:F2} ({day2Result.AccrualDays} days)"