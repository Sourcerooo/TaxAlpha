using System.Globalization;
using System.Xml.Linq;
using TaxAlpha.Core.Interfaces;
using TaxAlpha.Core.Models;

namespace TaxAlpha.Infrastructure.Loaders;

public class IbkrXmlLoader : ITransactionLoader
{
    private readonly CultureInfo _culture = CultureInfo.InvariantCulture;

    // HashSet zum Tracken ignorierter Assets für den Report
    public Dictionary<string, HashSet<string>> IgnoredAssets { get; } = new();

    public List<RawTransaction> LoadAll(string folderPath)
    {
        var allTransactions = new List<RawTransaction>();

        if (!Directory.Exists(folderPath))
        {
            Console.WriteLine($"FEHLER: Input Ordner fehlt: {folderPath}");
            return allTransactions;
        }

        var files = Directory.GetFiles(folderPath, "*.xml");
        Console.WriteLine($"Starte Import von {files.Length} XML-Dateien...");

        foreach (var file in files)
        {
            try
            {
                var fileTx = ParseFile(file);
                allTransactions.AddRange(fileTx);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"KRITISCHER FEHLER in {Path.GetFileName(file)}: {ex.Message}");
                // In C# throwen wir hier nicht zwingend sys.exit, aber wir loggen laut
            }
        }

        // 1. Sortieren
        var sorted = allTransactions.OrderBy(t => t.DateTime).ToList();

        // 2. Bereinigen (Stornos)
        var cleaned = CleanCancellations(sorted);

        return cleaned;
    }

    private List<RawTransaction> CleanCancellations(List<RawTransaction> list)
    {
        var indicesToRemove = new HashSet<int>();

        for (int i = 0; i < list.Count; i++)
        {
            var tx = list[i];
            // Erkennung von Stornos im Description-Text (markiert im Parser) oder Action
            bool isCorrection = tx.Description.Contains("[CANCEL]");

            if (isCorrection)
            {
                indicesToRemove.Add(i); // Das Storno selbst entfernen

                // Rückwärtssuche nach dem Original
                bool matchFound = false;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (indicesToRemove.Contains(j)) continue;

                    var candidate = list[j];

                    // Matching Logik: Gleiches Symbol, Gegenteilige Menge, Gleicher Preis (ca.)
                    if (candidate.Symbol == tx.Symbol &&
                        candidate.Quantity == -tx.Quantity &&
                        Math.Abs(candidate.AmountEur + tx.AmountEur) < 0.05m)
                    {
                        indicesToRemove.Add(j);
                        matchFound = true;
                        // Console.WriteLine($"   MATCH! Storno {tx.TransId} löscht {candidate.TransId}");
                        break;
                    }
                }

                if (!matchFound)
                {
                    Console.WriteLine($"WARNUNG: Isoliertes Storno gefunden für {tx.Symbol}. Wird ignoriert.");
                }
            }
        }

        return list.Where((_, idx) => !indicesToRemove.Contains(idx)).ToList();
    }

    private IEnumerable<RawTransaction> ParseFile(string filePath)
    {
            var doc = XDocument.Load(filePath);
            var result = new List<RawTransaction>();

            // Trades
            foreach (var el in doc.Descendants("Trade"))
            {
                var tx = ParseElement(el, isCash: false);
                if (tx != null) result.Add(tx);
            }

            // Cash
            foreach (var el in doc.Descendants("CashTransaction"))
            {
                var tx = ParseElement(el, isCash: true);
                if (tx != null) result.Add(tx);
            }
            return result;
                
    }

    private RawTransaction? ParseElement(XElement el, bool isCash)
    {
        var id = el.Attribute("transactionID")?.Value;
        if (string.IsNullOrEmpty(id)) return null;

        var symbol = el.Attribute("symbol")?.Value ?? "UNKNOWN";
        var assetCategory = el.Attribute("assetCategory")?.Value;
        var assetClass = el.Attribute("assetClass")?.Value;

        // Priorität: assetCategory > assetClass > UNKNOWN
        var finalAssetClass = !string.IsNullOrEmpty(assetCategory) ? assetCategory : (assetClass ?? "UNKNOWN");

        var type = el.Attribute("type")?.Value ?? "";

        // --- FILTER LOGIK ---
        if (isCash)
        {
            bool isInt = type.Contains("Interest");
            bool isDiv = type.Contains("Dividends");
            bool isWht = type.Contains("Withholding Tax");

            if (!isInt && !isDiv && !isWht) return null;

            // Dividenden nur von Aktien/Fonds
            if (isDiv && finalAssetClass != "STK" && finalAssetClass != "FUND") return null;
        }
        else // Trades
        {
            // 1. Check auf explizite Asset Class "CASH" (Forex)
            if (finalAssetClass == "CASH")
            {
                LogIgnored("CASH", symbol);
                return null;
            }

            // 2. Heuristik für Forex (z.B. EUR.USD)
            if (symbol.Contains(".") && symbol.Length == 7)
            {
                var currencies = new[] { "EUR", "USD", "GBP", "CHF", "JPY" };
                if (currencies.Any(c => symbol.Contains(c)))
                {
                    LogIgnored("FOREX_HEURISTIC", symbol);
                    return null;
                }
            }

            // 3. Whitelist
            if (finalAssetClass != "STK" && finalAssetClass != "FUND")
            {
                LogIgnored(finalAssetClass, symbol);
                return null;
            }
        }

        // Parsing Values
        var dtStr = el.Attribute("dateTime")?.Value ?? el.Attribute("tradeDate")?.Value ?? el.Attribute("reportDate")?.Value;
        var dt = ParseDate(dtStr);

        var qty = ParseDecimal(el.Attribute("quantity")?.Value);
        var price = ParseDecimal(el.Attribute("tradePrice")?.Value ?? el.Attribute("amount")?.Value); // Amount bei Cash
        var fx = ParseDecimal(el.Attribute("fxRateToBase")?.Value ?? "1.0");
        var comm = ParseDecimal(el.Attribute("ibCommission")?.Value);

        var feesEur = Math.Abs(comm) * fx;

        // Amount calculation
        // Bei Cash ist 'amount' schon der Wert. Bei Trades ist es Preis * Menge.
        // IBKR liefert tradePrice in Originalwährung.
        decimal amountEur;
        if (isCash)
            amountEur = price * fx; // hier ist 'price' eigentlich der amount
        else
            amountEur = (price * qty) * fx;


        // Action Mapping
        var actionStr = el.Attribute("buySell")?.Value;
        var action = TransactionAction.Unknown;
        var desc = el.Attribute("description")?.Value ?? "";

        if (isCash)
        {
            if (type.Contains("Dividends")) action = TransactionAction.Div;
            else if (type.Contains("Withholding")) action = TransactionAction.Wht;
            else action = TransactionAction.Int;
        }
        else
        {
            if (actionStr == "BUY") action = TransactionAction.Buy;
            else if (actionStr == "SELL") action = TransactionAction.Sell;

            // Storno-Markierung für den Cleaner
            if (desc.Contains("(Ca.)") || (actionStr?.Contains("Ca.") ?? false))
            {
                desc += " [CANCEL]";
            }
        }

        return new RawTransaction(
            TransId: id,
            Symbol: symbol,
            Isin: el.Attribute("isin")?.Value ?? "UNKNOWN",
            AssetClass: finalAssetClass,
            DateTime: dt,
            Action: action,
            Description: desc,
            Quantity: qty,
            AmountEur: amountEur,
            FeesEur: feesEur,
            FxRateToBase: fx,
            PriceOrigin: price
        );
    }

    private void LogIgnored(string category, string symbol)
    {
        if (!IgnoredAssets.ContainsKey(category))
            IgnoredAssets[category] = new HashSet<string>();
        IgnoredAssets[category].Add(symbol);
    }

    private decimal ParseDecimal(string? val)
    {
        if (string.IsNullOrEmpty(val)) return 0m;
        // IBKR nutzt Punkt als Dezimaltrenner
        if (decimal.TryParse(val, NumberStyles.Any, _culture, out var result))
            return result;
        return 0m;
    }

    private DateTime ParseDate(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return DateTime.MinValue;

        if (DateTime.TryParseExact(dateStr, "yyyyMMdd;HHmmss", _culture, DateTimeStyles.None, out var dt))
            return dt;

        if (DateTime.TryParseExact(dateStr, "yyyyMMdd", _culture, DateTimeStyles.None, out var dtDate))
            return dtDate.Add(new TimeSpan(23, 59, 59)); // End of Day Fallback

        return DateTime.MinValue;
    }
}