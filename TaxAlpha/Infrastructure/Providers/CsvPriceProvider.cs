using System.Globalization;
using TaxAlpha.Core.Interfaces;

namespace TaxAlpha.Infrastructure.Providers;

public class CsvPriceProvider : IPriceProvider
{
    // Key: (Year, Isin), Value: Price
    private readonly Dictionary<(int Year, string Isin), decimal> _prices = new();

    public CsvPriceProvider(string filePath)
    {
        Load(filePath);
    }
        
    private void Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"ERROR: Price file ({filePath}) is missing. VAP can't be calculated.");
            throw new FileNotFoundException("ERROR: Price-file used for VAP calculation does not exist.", filePath);            
        }

        try
        {
            // Reset dictionary
            _prices.Clear();

            // Liest Zeile für Zeile (Speicherschonend)
            foreach (var line in File.ReadLines(filePath).Skip(1)) // Skip Header
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(',');
                if (parts.Length < 3) continue;

                // Robustes Parsing mit InvariantCulture (Punkt als Dezimaltrenner)
                if (int.TryParse(parts[0], out int year) &&
                    decimal.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price))
                {
                    var isin = parts[1].Trim();
                    _prices[(year, isin)] = price;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR loading price data: {ex.Message}");
            throw new ApplicationException("ERROR: Loading price data csv failed");
        }
    }

    public decimal? GetPrice(string isin, int year)
    {
        return _prices.TryGetValue((year, isin), out var price) ? price : null;
    }
}