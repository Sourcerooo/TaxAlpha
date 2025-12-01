using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TaxAlpha.Core.Interfaces;
using TaxAlpha.Core.Models;
using YahooFinanceApi;

namespace TaxAlpha.Infrastructure.Providers
{
    public class YahooHistoricalPriceProvider : IHistoricalPriceProvider
    {
        private readonly string _inputPath;
        private readonly Dictionary<string, IReadOnlyList<PriceTick>> _priceCache = new();

        public YahooHistoricalPriceProvider(string inputPath)
        {
            _inputPath = inputPath;
        }

        public async Task<IReadOnlyList<PriceTick>> GetPrices(string symbol)
        {
            if (_priceCache.TryGetValue(symbol, out var cachedPrices))
            {
                return cachedPrices;
            }

            var filePath = Path.Combine(_inputPath, $"{symbol}.csv");
            var prices = new List<PriceTick>();

            if (!File.Exists(filePath))
            {
                await GenerateTestData(symbol, filePath);
            }
            
            prices.AddRange(ReadFromCsv(filePath));
            var orderedPrices = prices.OrderBy(p => p.Date).ToList();
            _priceCache[symbol] = orderedPrices;
            return orderedPrices;
        }
        
        public async Task<IReadOnlyList<PriceTick>> GetPricesFromYahoo(string symbol)
        {
            if (_priceCache.TryGetValue(symbol, out var cachedPrices))
            {
                return cachedPrices;
            }

            var filePath = Path.Combine(_inputPath, $"{symbol}.csv");
            var prices = new List<PriceTick>();

            if (File.Exists(filePath))
            {
                prices.AddRange(ReadFromCsv(filePath));
                var lastDate = prices.Max(p => p.Date);
                if (lastDate.Date < DateTime.Today)
                {
                    await Task.Delay(2000); // Add a small delay to avoid rate limiting
                    var missingData = await Yahoo.GetHistoricalAsync(symbol, lastDate.AddDays(1), DateTime.Today, Period.Daily);
                    var missingPrices = missingData.Select(d => new PriceTick
                    {
                        Date = d.DateTime,
                        Open = d.Open,
                        High = d.High,
                        Low = d.Low,
                        Close = d.Close,
                        AdjustedClose = d.AdjustedClose,
                        Volume = d.Volume
                    }).ToList();
                    prices.AddRange(missingPrices);
                    await AppendToCsv(filePath, missingPrices);
                }
            }
            else
            {
                await Task.Delay(2000); // Add a small delay to avoid rate limiting
                var history = await Yahoo.GetHistoricalAsync(symbol, DateTime.Today.AddYears(-1), DateTime.Today, Period.Daily);
                prices = history.Select(d => new PriceTick
                {
                    Date = d.DateTime,
                    Open = d.Open,
                    High = d.High,
                    Low = d.Low,
                    Close = d.Close,
                    AdjustedClose = d.AdjustedClose,
                    Volume = d.Volume
                }).ToList();
                await WriteToCsv(filePath, prices);
            }

            var orderedPrices = prices.OrderBy(p => p.Date).ToList();
            _priceCache[symbol] = orderedPrices;
            return orderedPrices;
        }


        private async Task GenerateTestData(string symbol, string filePath)
        {
            var prices = new List<PriceTick>();
            var random = new Random();
            var currentDate = DateTime.Today.AddYears(-1);
            var lastClose = (decimal)(random.NextDouble() * 100 + 50); 

            for (int i = 0; i < 365; i++)
            {
                var open = lastClose * (1 + ((decimal)random.NextDouble() - 0.5m) * 0.1m);
                var high = open * (1 + (decimal)random.NextDouble() * 0.05m);
                var low = open * (1 - (decimal)random.NextDouble() * 0.05m);
                var close = (high + low) / 2;
                var adjustedClose = close;
                var volume = (long)(random.NextDouble() * 1000000 + 500000);

                prices.Add(new PriceTick
                {
                    Date = currentDate.AddDays(i),
                    Open = Math.Round(open, 2),
                    High = Math.Round(high, 2),
                    Low = Math.Round(low, 2),
                    Close = Math.Round(close, 2),
                    AdjustedClose = Math.Round(adjustedClose, 2),
                    Volume = volume
                });
                lastClose = close;
            }
            await WriteToCsv(filePath, prices);
        }

        private IEnumerable<PriceTick> ReadFromCsv(string filePath)
        {
            return File.ReadAllLines(filePath).Skip(1).Select(line =>
            {
                var parts = line.Split(',');
                return new PriceTick
                {
                    Date = DateTime.Parse(parts[0], CultureInfo.InvariantCulture),
                    Open = decimal.Parse(parts[1], CultureInfo.InvariantCulture),
                    High = decimal.Parse(parts[2], CultureInfo.InvariantCulture),
                    Low = decimal.Parse(parts[3], CultureInfo.InvariantCulture),
                    Close = decimal.Parse(parts[4], CultureInfo.InvariantCulture),
                    AdjustedClose = decimal.Parse(parts[5], CultureInfo.InvariantCulture),
                    Volume = long.Parse(parts[6], CultureInfo.InvariantCulture)
                };
            });
        }

        private async Task WriteToCsv(string filePath, IEnumerable<PriceTick> prices)
        {
            var lines = new List<string> { "Date,Open,High,Low,Close,AdjustedClose,Volume" };
            lines.AddRange(prices.Select(p =>
                $"{p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},{p.Open.ToString(CultureInfo.InvariantCulture)},{p.High.ToString(CultureInfo.InvariantCulture)},{p.Low.ToString(CultureInfo.InvariantCulture)},{p.Close.ToString(CultureInfo.InvariantCulture)},{p.AdjustedClose.ToString(CultureInfo.InvariantCulture)},{p.Volume.ToString(CultureInfo.InvariantCulture)}"
            ));
            await File.WriteAllLinesAsync(filePath, lines);
        }

        private async Task AppendToCsv(string filePath, IEnumerable<PriceTick> prices)
        {
            var lines = prices.Select(p =>
                $"{p.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},{p.Open.ToString(CultureInfo.InvariantCulture)},{p.High.ToString(CultureInfo.InvariantCulture)},{p.Low.ToString(CultureInfo.InvariantCulture)},{p.Close.ToString(CultureInfo.InvariantCulture)},{p.AdjustedClose.ToString(CultureInfo.InvariantCulture)},{p.Volume.ToString(CultureInfo.InvariantCulture)}"
            ).ToList();

            if (lines.Any())
            {
                await File.AppendAllLinesAsync(filePath, lines);
            }
        }
    }
}