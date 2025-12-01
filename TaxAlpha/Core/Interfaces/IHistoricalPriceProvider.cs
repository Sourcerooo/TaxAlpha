namespace TaxAlpha.Core.Interfaces;

using TaxAlpha.Core.Models;

public interface IHistoricalPriceProvider
{
    Task<IReadOnlyList<PriceTick>> GetPrices(string symbol);
}
