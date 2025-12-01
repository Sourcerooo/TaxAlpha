namespace TaxAlpha.Core.Interfaces;

using TaxAlpha.Core.Models;

public interface IInstrumentProvider
{
    Instrument GetInstrument(string isin, string fallbackSymbol);
}