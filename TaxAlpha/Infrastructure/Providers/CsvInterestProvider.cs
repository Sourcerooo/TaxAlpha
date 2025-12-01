using System.Globalization;
using TaxAlpha.Core.Interfaces;

namespace TaxAlpha.Infrastructure.Providers;

public class CsvInterestProvider : IInterestProvider
{
    private readonly Dictionary<int, decimal> _rates = new();

    public CsvInterestProvider(string filePath)
    {
        Load(filePath);
    }

    private void Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("WARNUNG: Basiszins-Datei fehlt", filePath);
        }

        try
        {
            foreach (var line in File.ReadLines(filePath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(',');
                if (parts.Length < 2) continue;

                if (int.TryParse(parts[0], out int year) &&
                    decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out decimal rate))
                {
                    _rates[year] = rate;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERORO loading base interest rates: {ex.Message}");
            throw new ApplicationException("ERROR: Occurred during loading of base interest rate CSV");
        }
    }

    public decimal GetRate(int year)
    {
        return _rates.TryGetValue(year, out var rate) ? rate : 0m;
    }
}