using TaxAlpha.Core.Interfaces;
using TaxAlpha.Core.Models;

namespace TaxAlpha.Core.Engine
{
    public class TradingEngine
    {
        private readonly IHistoricalPriceProvider _historicalPriceProvider;
        private const int MovingAverageDays = 150;

        public TradingEngine(IHistoricalPriceProvider historicalPriceProvider)
        {
            _historicalPriceProvider = historicalPriceProvider;
        }

        public async Task<Dictionary<string, (bool isBuySignal, decimal movingAverage, decimal lastClose)>> GetTradingSignals(params string[] symbols)
        {
            var signals = new Dictionary<string, (bool isBuySignal, decimal movingAverage, decimal lastClose)>();

            foreach (var symbol in symbols)
            {
                var prices = await _historicalPriceProvider.GetPrices(symbol);
                if (prices.Count >= MovingAverageDays)
                {
                    var movingAverage = prices.TakeLast(MovingAverageDays).Average(p => p.Close);
                    var lastClose = prices.Last().Close;
                    var isBuySignal = lastClose > movingAverage;
                    signals.Add(symbol, (isBuySignal, movingAverage, lastClose));
                }
            }

            return signals;
        }

        public Dictionary<string, (int shares, decimal value)> CalculatePortfolioAllocation(
            decimal netLiqValue,
            Dictionary<string, (bool isBuySignal, decimal movingAverage, decimal lastClose)> signals,
            Dictionary<string, decimal> allocation)
        {
            var portfolio = new Dictionary<string, (int shares, decimal value)>();

            foreach (var (symbol, (isBuySignal, _, lastClose)) in signals)
            {
                if (isBuySignal && allocation.TryGetValue(symbol, out var targetAllocation))
                {
                    var targetValue = netLiqValue * targetAllocation;
                    var shares = (int)(targetValue / lastClose);
                    var value = shares * lastClose;
                    portfolio.Add(symbol, (shares, value));
                }
            }

            return portfolio;
        }
    }
}
