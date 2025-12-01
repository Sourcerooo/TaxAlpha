namespace TaxAlpha.Core.Models;

// Immutable Raw Data
public record RawTransaction(
    string TransId,
    string Symbol,
    string Isin,
    string AssetClass,
    DateTime DateTime,
    TransactionAction Action,
    string Description,
    decimal Quantity,
    decimal AmountEur,
    decimal FeesEur,
    decimal FxRateToBase,
    decimal PriceOrigin
);