using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using TaxAlpha.Core.Interfaces;
using TaxAlpha.Core.Models;

namespace TaxAlpha.Infrastructure.Providers;

public class JsonInstrumentProvider : IInstrumentProvider
{
    private readonly Dictionary<string, Instrument> _instruments = new();

    // Internal DTO for JSON Mapping
    private class InstrumentDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("tfs_quote")]
        public object TfsQuoteRaw { get; set; } = 0.0m; 
    }

    public JsonInstrumentProvider(string filePath)
    {
        Load(filePath);
    }

    private void Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine("ERROR: instruments.json not found. Data must be maintained for corrrect tax calculation.");
            throw new FileNotFoundException("ERROR: Instrument description file not found.", filePath);            
        }

        try
        {
            var jsonString = File.ReadAllText(filePath);
            var dtos = JsonSerializer.Deserialize<Dictionary<string, InstrumentDto>>(jsonString);
            var errorOccurred = false;
            if (dtos != null)
            {
                foreach (var (isin, dto) in dtos)
                {                    
                    decimal tfs = 0.0m;
                    string? rawTfs = dto.TfsQuoteRaw.ToString();
                    if(rawTfs == null)
                    {
                        Console.WriteLine("Parsing error occurred for ISIN {0} Name {1}. Now TFS maintained. Record is skipped. Fix file before continuing.", isin, dto.Name);
                        errorOccurred = true;
                        continue;
                    }
                    if (!decimal.TryParse(rawTfs, NumberStyles.Any, CultureInfo.InvariantCulture, out tfs))
                    {
                        Console.WriteLine("Parsing error occurred for ISIN {0} Name {1}. Record is skipped. Fix file before continuing.", isin, dto.Name);
                        errorOccurred = true;
                        continue;
                    }

                    _instruments[isin] = new Instrument(isin, "Unknown", dto.Name, tfs);
                }
                if (errorOccurred)
                {
                    throw new ApplicationException("ERROR: At least one parsing error occured.");
                }
            }
        }
        catch (Exception ex)
        {
            throw new ApplicationException($"ERROR: Error occured during loading of instruments.json. {ex.Message}");
        }
    }

    public Instrument GetInstrument(string isin, string fallbackSymbol)
    {
        if (_instruments.TryGetValue(isin, out var inst))
        {
            // Wir aktualisieren das Symbol mit den Daten aus dem Trade, 
            // da es im JSON oft fehlt oder "Unknown" ist.
            // Records sind immutable, wir nutzen "with" für eine Kopie.
            if (inst.Symbol == "Unknown")
            {
                return inst with { Symbol = fallbackSymbol };
            }
            return inst;
        }

        // Fallback für unbekannte Instrumente
        return new Instrument(
            Isin: isin,
            Symbol: fallbackSymbol,
            Name: "Auto-Generated",
            TfsQuote: 0.30m // Konservativer Default für Aktien-ETFs
        );
    }
}