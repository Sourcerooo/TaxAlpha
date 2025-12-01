namespace TaxAlpha.Core.Models;

// Mutable State
public class TaxLot
{
    public string Id { get; }
    public string Symbol { get; }
    public string Isin { get; }
    public DateOnly DateAcquired { get; }
    public decimal OriginalQuantity { get; }
    public decimal RemainingQuantity { get; set; }
    public decimal AcquisitionCostTotal { get; }
    public decimal AccumulatedVap { get; set; } = 0m;

    // Debug Info
    public decimal AcquisitionPriceOrigin { get; }
    public decimal AcquisitionFxRate { get; }

    public TaxLot(string id, string symbol, string isin, DateOnly dateAcquired,
                  decimal originalQty, decimal costTotal, decimal priceOrigin, decimal fxRate)
    {
        Id = id; Symbol = symbol; Isin = isin; DateAcquired = dateAcquired;
        OriginalQuantity = originalQty; RemainingQuantity = originalQty;
        AcquisitionCostTotal = costTotal;
        AcquisitionPriceOrigin = priceOrigin; AcquisitionFxRate = fxRate;
    }
}