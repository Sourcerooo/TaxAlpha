using TaxAlpha.Core.Engine;
using TaxAlpha.Core.Interfaces;
using TaxAlpha.Core.Models;
using TaxAlpha.Core.Strategies;
using TaxAlpha.Infrastructure.Loaders;
using TaxAlpha.Infrastructure.Providers;
using TaxAlpha.Reporting;

Console.WriteLine("--- TaxAlpha ---");

Console.WriteLine("Please select an option:");
Console.WriteLine("1. Run Tax Report");
Console.WriteLine("2. Run Trading Engine");

var option = Console.ReadKey().KeyChar;
Console.WriteLine();

switch (option)
{
    case '1':
        await RunTaxReport();
        break;
    case '2':
        await RunTradingEngine();
        break;
    default:
        Console.WriteLine("Invalid option.");
        break;
}


static async Task RunTaxReport()
{
    Console.WriteLine("\n--- Running Tax Report ---");
    string basePath = AppDomain.CurrentDomain.BaseDirectory;
    string inputPath = Path.Combine(basePath, "input");
    string configurationPath = Path.Combine(inputPath, "configuration");

    Console.WriteLine($"Searching for input files in: {inputPath}"); // Debug Output

    // 1. Setup Infrastructure
    ITransactionLoader loader;
    IPriceProvider priceProvider;
    IInterestProvider interestProvider;
    IInstrumentProvider instrumentProvider;
    List<RawTransaction> transactions;
    try
    {
        priceProvider = new CsvPriceProvider(Path.Combine(configurationPath, "prices.csv"));
        interestProvider = new CsvInterestProvider(Path.Combine(configurationPath, "basiszins.csv"));
        instrumentProvider = new JsonInstrumentProvider(Path.Combine(configurationPath, "instruments.json"));

        // 2. Load Data
        loader = new IbkrXmlLoader();
        transactions = loader.LoadAll(inputPath);
    }
    catch (Exception exception)
    {
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
}

static async Task RunTradingEngine()
{
    Console.WriteLine("\n--- Running Trading Engine ---");
    string basePath = AppDomain.CurrentDomain.BaseDirectory;
    string inputPath = Path.Combine(basePath, "input");
    string pricePath = Path.Combine(inputPath, "priceData");

    var historicalPriceProvider = new YahooHistoricalPriceProvider(pricePath);
    var portfolio = new Portfolio(100000m);
    var logger = new ConsoleStrategyLogger();
    var tradingEngine = new TradingEngine(historicalPriceProvider, portfolio, logger);
    
    var strategies = new List<ITradingStrategy>
    {
        new PersistentPortfolioStrategy(tradingEngine),
        new TacticalAssetAllocationStrategy(tradingEngine)
    };

    Console.WriteLine("\nPlease select a strategy:");
    for (int i = 0; i < strategies.Count; i++)
    {
        Console.WriteLine($"{i + 1}. {strategies[i].Name}");
    }

    var strategyOption = Console.ReadKey().KeyChar;
    Console.WriteLine();

    if (int.TryParse(strategyOption.ToString(), out int strategyIndex) && strategyIndex > 0 && strategyIndex <= strategies.Count)
    {
        var selectedStrategy = strategies[strategyIndex - 1];
        Console.WriteLine($"\n--- Running Strategy: {selectedStrategy.Name} ---");
        await tradingEngine.RunStrategy(selectedStrategy);
    }
    else
    {
        Console.WriteLine("Invalid strategy option.");
    }
}
