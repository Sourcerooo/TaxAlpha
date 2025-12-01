namespace TaxAlpha.Core.Interfaces;

public interface IPriceProvider
{
    decimal? GetPrice(string isin, int year);
}
