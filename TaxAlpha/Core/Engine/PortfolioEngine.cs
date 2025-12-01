namespace TaxAlpha.Core.Engine;

using TaxAlpha.Core.Interfaces;
using TaxAlpha.Core.Models;

public class PortfolioEngine
{
    private readonly Dictionary<string, List<TaxLot>> _portfolio = new();
    private readonly List<TaxEvent> _ledger = new();
    private readonly Dictionary<(int Year, string Isin), decimal> _distributions = new();

    private readonly IInstrumentProvider _instrumentProvider;
    private readonly IInterestProvider _interestProvider;

    public PortfolioEngine(IInstrumentProvider instrumentProvider, IInterestProvider interestProvider)
    {
        _instrumentProvider = instrumentProvider;
        _interestProvider = interestProvider;
    }

    public IReadOnlyList<TaxEvent> Ledger => _ledger.AsReadOnly();
    public IReadOnlyDictionary<string, List<TaxLot>> Portfolio => _portfolio;

    public void ProcessTransaction(RawTransaction tx)
    {
        var txDate = DateOnly.FromDateTime(tx.DateTime);
        var year = txDate.Year;
        var inst = _instrumentProvider.GetInstrument(tx.Isin, tx.Symbol);

        switch (tx.Action)
        {
            case TransactionAction.Buy:
                HandleBuy(tx, txDate);
                break;
            case TransactionAction.Sell:
                HandleSell(tx, inst, txDate, year);
                break;
            case TransactionAction.Div:
            case TransactionAction.Wht:
                HandleDividend(tx, inst, txDate, year);
                break;
            case TransactionAction.Int:
                HandleInterest(tx, txDate, year);
                break;
        }
    }

    private void HandleBuy(RawTransaction tx, DateOnly date)
    {
        var totalCost = tx.AmountEur + tx.FeesEur;
        var lot = new TaxLot(
            tx.TransId, tx.Symbol, tx.Isin, date,
            tx.Quantity, totalCost, tx.PriceOrigin, tx.FxRateToBase
        );

        if (!_portfolio.ContainsKey(tx.Isin))
            _portfolio[tx.Isin] = new List<TaxLot>();

        _portfolio[tx.Isin].Add(lot);
    }

    private void HandleSell(RawTransaction tx, Instrument inst, DateOnly date, int year)
    {
        var qtyToSell = Math.Abs(tx.Quantity);
        var proceeds = Math.Abs(tx.AmountEur);
        var remainingToSell = qtyToSell;

        if (!_portfolio.ContainsKey(tx.Isin) || _portfolio[tx.Isin].Count == 0)
        {
            Console.WriteLine($"WARNUNG: Short/Fehler bei {tx.Symbol} am {date}. Kein Bestand.");
            return;
        }

        var lots = _portfolio[tx.Isin];

        while (remainingToSell > 0 && lots.Count > 0)
        {
            var lot = lots[0]; // FIFO
            var takeQty = Math.Min(remainingToSell, lot.RemainingQuantity);

            // Proportionale Berechnung
            var ratio = takeQty / qtyToSell;
            var partProceeds = proceeds * ratio;
            var partSellCosts = tx.FeesEur * ratio;

            var lotBuyPriceAvg = lot.AcquisitionCostTotal / lot.OriginalQuantity;
            var partAcquisitionCosts = lotBuyPriceAvg * takeQty;

            var partVap = (lot.RemainingQuantity > 0)
                ? (lot.AccumulatedVap / lot.RemainingQuantity) * takeQty
                : 0m;

            var rawProfit = partProceeds - partSellCosts - partAcquisitionCosts - partVap;
            var taxableProfit = rawProfit * (1.0m - inst.TfsQuote);

            _ledger.Add(new TaxEvent(
                year, date, TaxEventType.SELL, tx.Symbol, tx.Isin,
                rawProfit, taxableProfit, partVap, 0m,
                takeQty, partProceeds, partAcquisitionCosts
            ));

            // State Update
            lot.RemainingQuantity -= takeQty;
            lot.AccumulatedVap -= partVap;
            remainingToSell -= takeQty;

            if (lot.RemainingQuantity <= 0.000001m) lots.RemoveAt(0);
        }
    }

    private void HandleDividend(RawTransaction tx, Instrument inst, DateOnly date, int year)
    {
        if (tx.Action == TransactionAction.Div)
        {
            var rawAmount = tx.AmountEur;
            var taxable = rawAmount * (1.0m - inst.TfsQuote);

            _ledger.Add(new TaxEvent(year, date, TaxEventType.DIVIDEND, tx.Symbol, tx.Isin, rawAmount, taxable));

            // VAP Tracking
            if (_portfolio.TryGetValue(tx.Isin, out var lots))
            {
                var totalQty = lots.Sum(l => l.RemainingQuantity);
                if (totalQty > 0)
                {
                    var divPerShare = tx.AmountEur / totalQty;
                    var key = (year, tx.Isin);
                    if (!_distributions.ContainsKey(key)) _distributions[key] = 0m;
                    _distributions[key] += divPerShare;
                }
            }
        }
        else if (tx.Action == TransactionAction.Wht)
        {
            _ledger.Add(new TaxEvent(year, date, TaxEventType.WITHHOLDINGTAX, tx.Symbol, tx.Isin, tx.AmountEur, 0m, ForeignWht: Math.Abs(tx.AmountEur)));
        }
    }

    private void HandleInterest(RawTransaction tx, DateOnly date, int year)
    {
        _ledger.Add(new TaxEvent(year, date, TaxEventType.INTEREST, tx.Symbol, tx.Isin, tx.AmountEur, tx.AmountEur));
    }

    public void PerformYearEndClosing(int year, IPriceProvider priceProvider)
    {
        var zins = _interestProvider.GetRate(year);
        if (zins <= 0) return;

        var basisfaktor = zins * 0.7m;
        Console.WriteLine($"\n--- Jahresabschluss {year} (Basiszins: {zins:P}) ---");

        foreach (var (isin, lots) in _portfolio)
        {
            var eoyPrice = priceProvider.GetPrice(isin, year);
            if (!eoyPrice.HasValue) continue;

            var distPerShare = _distributions.GetValueOrDefault((year, isin), 0m);
            var inst = _instrumentProvider.GetInstrument(isin, lots.FirstOrDefault()?.Symbol ?? "");

            foreach (var lot in lots.Where(l => l.RemainingQuantity > 0))
            {
                var lotBuyPrice = lot.AcquisitionCostTotal / lot.OriginalQuantity;

                // Monatliche Berechnung
                decimal months = 12m;
                if (lot.DateAcquired.Year == year)
                    months = 12m - lot.DateAcquired.Month + 1m;

                var basisertrag = lotBuyPrice * basisfaktor * (months / 12m);
                var wertsteigerung = Math.Max(0m, eoyPrice.Value - lotBuyPrice);
                var maxVap = Math.Min(basisertrag, wertsteigerung);

                var actualVapPerShare = Math.Max(0m, maxVap - distPerShare);

                if (actualVapPerShare > 0)
                {
                    var totalVap = actualVapPerShare * lot.RemainingQuantity;
                    lot.AccumulatedVap += totalVap;

                    _ledger.Add(new TaxEvent(
                        year + 1, new DateOnly(year + 1, 1, 1), TaxEventType.VORABPAUSCHALE,
                        lot.Symbol, isin,
                        totalVap, totalVap * (1.0m - inst.TfsQuote)
                    ));
                }
            }
        }
    }
}