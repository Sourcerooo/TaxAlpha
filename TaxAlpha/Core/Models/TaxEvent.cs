namespace TaxAlpha.Core.Models;

public enum TaxEventType
{
    SELL=1,
    DIVIDEND=2,
    INTEREST=3,
    VORABPAUSCHALE=4,
    WITHHOLDINGTAX=5
}

// Immutable Tax Result Event
public record TaxEvent(
    int Year,
    DateOnly Date,
    TaxEventType Type,
    string Symbol,
    string Isin,
    decimal RawProfit,
    decimal TaxableProfit,
    decimal UsedVap = 0m,
    decimal ForeignWht = 0m,
    // Debug info
    decimal QuantitySold = 0m,
    decimal SaleProceeds = 0m,
    decimal AcquisitionCosts = 0m
);
