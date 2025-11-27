from loader import IbkrXmlLoader, PriceProvider, BaseInterestProvider, InstrumentProvider
from engine import PortfolioEngine
import datetime

def main():
    print("--- TaxAlpha Final Run ---")
    
    # 1. Laden
    print("Lade Konfigurationen...")
    loader = IbkrXmlLoader("./input")
    prices = PriceProvider("./input/prices.csv")
    interest_provider = BaseInterestProvider("./input/basiszins.csv")
    instrument_provider = InstrumentProvider("./input/instruments.json")
    
    transactions = loader.load_all()
    
    engine = PortfolioEngine(interest_provider, instrument_provider)
    
    # 2. Chronologische Verarbeitung mit Jahresabschluss
    # Wir sortieren erst alles
    current_year = transactions[0].date_time.year
    max_year = transactions[-1].date_time.year
    
    # Pointer für Transaktionen
    tx_idx = 0
    total_tx = len(transactions)
    
    for year in range(current_year, max_year + 2): # +2 um sicherzugehen
        # A. Alle Trades für dieses Jahr verarbeiten
        while tx_idx < total_tx and transactions[tx_idx].date_time.year == year:
            engine.process_transaction(transactions[tx_idx])
            tx_idx += 1
            
        # B. Jahresabschluss machen (VAP berechnen)
        engine.perform_year_end_closing(year, prices)
    
    # 3. Reporting für 2024 (Vergleich IGLN)
    print("\n" + "="*90)
    print("REPORT 2023 (Inkl. VAP)")
    print("="*90)
    print(f"{'Datum':<12} | {'Sym':<6} | {'TFS-Quote':<10} | {'Typ':<4} | {'Roh-Gewinn':>12} | {'VAP genutzt':>12} | {'Steuerpfl.':>12}")
    
    sum_taxable = 0
    for evt in engine.tax_ledger:
        if evt.year == 2023 and evt.type == 'SELL':
             inst = instrument_provider.get_instrument(evt.isin, evt.symbol)
             tfs_info = f"(TFS {inst.tfs_quote*100:.0f}%)"
             print(f"{str(evt.date):<12} | {evt.symbol:<6} | {tfs_info:<10} | {evt.type:<4} | "
                   f"{evt.raw_profit:>12.2f} | {evt.used_vap:>12.2f} | {evt.taxable_profit:>12.2f}")
             sum_taxable += evt.taxable_profit
    
    print("-" * 90)
    print(f"Summe Steuerpflichtig 2024: {sum_taxable:.2f} EUR")

if __name__ == "__main__":
    main()