using System;
using System.Collections.Generic;
using System.Text;
using TaxAlpha.Core.Engine;
using TaxAlpha.Core.Models;
using TaxAlpha.Core.Interfaces;
using TaxAlpha.Infrastructure.Loaders;

namespace TaxAlpha.Reporting
{
    internal class ConsoleReporting
    {
        public static void PrintReport(PortfolioEngine eng, int year, IInstrumentProvider instProvider)
        {
            var events = eng.Ledger.Where(e => e.Year == year).ToList();

            // If neither trades, dividends nor VAP, skip processing
            if (!events.Any()) return;

            Console.WriteLine($"\n{new string('#', 80)}");
            Console.WriteLine($"STEUERREPORT {year} (Simulation Anlage KAP)");
            Console.WriteLine($"{new string('#', 80)}");

            // --- 1. Proceeds ---
            Console.WriteLine($"\n1. VERÄUSSERUNGSGEWINNE (Inkl. VAP-Korrektur)");
            Console.WriteLine($"{"Datum",-12} | {"Symbol",-7} | {"TFS",-7} | {"Roh-Gewinn",12} | {"VAP Abzug",10} | {"Steuerpfl.",12}");
            Console.WriteLine(new string('-', 80));

            decimal sumSellTaxable = 0m;
            foreach (var e in events.Where(ev => ev.Type == TaxEventType.SELL))
            {
                var inst = instProvider.GetInstrument(e.Isin, e.Symbol);
                var tfsPct = inst.TfsQuote * 100m;

                Console.WriteLine($"{e.Date,-12:yyyy-MM-dd} | {e.Symbol,-7} | {tfsPct,6:F0}% | {e.RawProfit,12:F2} | {e.UsedVap,10:F2} | {e.TaxableProfit,13:F2}");
                sumSellTaxable += e.TaxableProfit;
            }
            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"SUMME Veräußerungen:{sumSellTaxable,56:F2} EUR");


            // --- 2. Show german "Vorabpauschalen" ---
            Console.WriteLine($"\n2. VORABPAUSCHALEN (VAP) - Fiktiver Zufluss");
            Console.WriteLine($"{"Datum",-12} | {"Symbol",-8} | {"TFS",-5} | {"VAP Roh",12} | {"Steuerpfl.",29}");
            Console.WriteLine(new string('-', 80));

            decimal sumVapTaxable = 0m;
            foreach (var e in events.Where(ev => ev.Type == TaxEventType.VORABPAUSCHALE))
            {
                var inst = instProvider.GetInstrument(e.Isin, e.Symbol);
                var tfsPct = inst.TfsQuote * 100m;

                Console.WriteLine($"{e.Date,-12:yyyy-MM-dd} | {e.Symbol,-8} | {tfsPct,4:F0}% | {e.RawProfit,12:F2} | {e.TaxableProfit,27:F2}");
                sumVapTaxable += e.TaxableProfit;
            }
            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"SUMME Vorabpauschalen:{sumVapTaxable,54:F2} EUR");


            // --- 3. Dividends ---
            Console.WriteLine($"\n3. DIVIDENDEN (Laufende Erträge)");
            Console.WriteLine($"{"Datum",-12} | {"Symbol",-8} | {"TFS",-5} | {"Brutto",12} | {"Steuerpfl.",29}");
            Console.WriteLine(new string('-', 80));

            decimal sumDivTaxable = 0m;
            foreach (var e in events.Where(ev => ev.Type == TaxEventType.DIVIDEND))
            {
                var inst = instProvider.GetInstrument(e.Isin, e.Symbol);
                var tfsPct = inst.TfsQuote * 100m;

                Console.WriteLine($"{e.Date,-12:yyyy-MM-dd} | {e.Symbol,-8} | {tfsPct,4:F0}% | {e.RawProfit,12:F2} | {e.TaxableProfit,27:F2}");
                sumDivTaxable += e.TaxableProfit;
            }
            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"SUMME Dividenden:{sumDivTaxable,59:F2} EUR");


            // --- 4. Interest ---
            Console.WriteLine($"\n4. ZINSEN (Fremdwährung & Cash)");
            Console.WriteLine($"{"Datum",-12} | {"Währung",-8} | {"Brutto EUR",53}");
            Console.WriteLine(new string('-', 80));

            decimal sumIntTaxable = 0m;
            foreach (var e in events.Where(ev => ev.Type == TaxEventType.INTEREST))
            {
                Console.WriteLine($"{e.Date,-12:yyyy-MM-dd} | {e.Symbol,-8} | {e.RawProfit,50:F2}");
                sumIntTaxable += e.TaxableProfit;
            }
            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"SUMME Zinsen:{sumIntTaxable,63:F2} EUR");


            // --- 5. Quellensteuer ---
            Console.WriteLine($"\n5. GEZAHLTE QUELLENSTEUER (Anrechenbar)");
            Console.WriteLine(new string('-', 80));
            decimal sumWht = 0m;
            foreach (var e in events.Where(ev => ev.Type == TaxEventType.WITHHOLDINGTAX || ev.ForeignWht > 0))
            {
                // In C# Engine haben wir ForeignWht bereits positiv gespeichert bei WHT-Events
                var val = e.ForeignWht;

                Console.WriteLine($"{e.Date,-12:yyyy-MM-dd} | {e.Symbol,-8} | {val,50:F2}");
                sumWht += val;
            }
            Console.WriteLine(new string('-', 80));
            Console.WriteLine($"SUMME Anrechenbare QSt:{sumWht,53:F2} EUR");


            // --- ZUSAMMENFASSUNG ---
            decimal totalIncome = sumSellTaxable + sumVapTaxable + sumDivTaxable + sumIntTaxable;
            decimal estTax = (totalIncome * 0.25m) * 1.055m; // 25% + Soli
            decimal estTaxFinal = estTax - sumWht;

            Console.WriteLine($"\n{new string('=', 80)}");
            Console.WriteLine($"GESAMTERGEBNIS {year}");
            Console.WriteLine($"{new string('=', 80)}");
            Console.WriteLine($"Summe der steuerpflichtigen Kapitalerträge: {totalIncome,20:F2} EUR");
            Console.WriteLine($"  davon Aktienveräußerungen (Verlusttopf):  {sumSellTaxable,20:F2} EUR");
            Console.WriteLine($"  davon Sonstiges (Vorab/Div/Zins):         {sumVapTaxable + sumDivTaxable + sumIntTaxable,20:F2} EUR");
            Console.WriteLine(new string('-', 65));
            Console.WriteLine($"Geschätzte Steuerlast (25% + Soli):         {estTax,20:F2} EUR");
            Console.WriteLine($"Abzüglich anrechenbare Quellensteuer:       {-sumWht,20:F2} EUR");
            Console.WriteLine(new string('=', 65));
            Console.WriteLine($"NACHZAHLUNG / ERSTATTUNG (ca.):             {estTaxFinal,20:F2} EUR");
            Console.WriteLine($"{new string('=', 80)}\n");
        }
        public static void PrintIgnoredAssets(IbkrXmlLoader loader)
        {
            // Report über ignorierte Assets ausgeben (wie in Python)
            if (loader.IgnoredAssets.Any())
            {
                Console.WriteLine("\n--- BERICHT: IGNORIERTE ASSETS ---");
                foreach (var kvp in loader.IgnoredAssets)
                {
                    Console.WriteLine($"Kategorie '{kvp.Key}': {kvp.Value.Count} Symbole ignoriert (z.B. {string.Join(", ", kvp.Value.Take(3))}...)");
                }
            }
        }
    }
    
}
