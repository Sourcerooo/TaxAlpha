using TaxAlpha.Core.Engine;
using TaxAlpha.Core.Interfaces;
using TaxAlpha.Core.Models;
using TaxAlpha.Infrastructure.Loaders;
using TaxAlpha.Infrastructure.Providers;
using TaxAlpha.Reporting;

Console.WriteLine("--- TaxAlpha ---");

string basePath = AppDomain.CurrentDomain.BaseDirectory;
string inputPath = Path.Combine(basePath, "input");

Console.WriteLine($"Searching for input files in: {inputPath}"); // Debug Output

// 1. Setup Infrastructure
ITransactionLoader loader;
IPriceProvider priceProvider;
IInterestProvider interestProvider;
IInstrumentProvider instrumentProvider;
List<RawTransaction> transactions;
try{
    priceProvider = new CsvPriceProvider(Path.Combine(inputPath, "prices.csv"));    
    interestProvider = new CsvInterestProvider(Path.Combine(inputPath, "basiszins.csv"));
    instrumentProvider = new JsonInstrumentProvider(Path.Combine(inputPath, "instruments.json"));
    
    // 2. Load Data
    loader = new IbkrXmlLoader();
    transactions = loader.LoadAll(inputPath);
}
catch(Exception exception){
    Console.WriteLine("ERROR: Exception occurred. Program will terminate. Message: {0}", exception.Message);
    return;
}

Console.WriteLine($"Loaded: {transactions.Count} transactions.");
ConsoleReporting.PrintIgnoredAssets((IbkrXmlLoader)loader);

// 3. Setup Engine
var engine = new PortfolioEngine(instrumentProvider, interestProvider);

// 4. Processing Loop
if (transactions.Any())
{
    var minYear = transactions.Min(t => t.DateTime.Year);
    var maxYear = transactions.Max(t => t.DateTime.Year);
    int txIdx = 0;

    // Need to process up to maxYear + 1 to get VAP for the last year
    for (int year = minYear; year <= maxYear + 1; year++)
    {
        // A. Process trades for current year
        while (txIdx < transactions.Count && transactions[txIdx].DateTime.Year == year)
        {
            engine.ProcessTransaction(transactions[txIdx]);
            txIdx++;
        }

        // B. Annual statement (calculate VAP deadline 31.12.)
        engine.PerformYearEndClosing(year, priceProvider);

        // C. Report generation
        ConsoleReporting.PrintReport(engine, year, instrumentProvider);

    }
}