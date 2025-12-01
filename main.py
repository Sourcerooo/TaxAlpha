from loader import IbkrXmlLoader, PriceProvider, BaseInterestProvider, InstrumentProvider
from engine import PortfolioEngine
from collections import defaultdict
from decimal import Decimal

D = Decimal

def generate_annual_report(engine: PortfolioEngine, year: int, instrument_provider: InstrumentProvider):
    print("\n" + "#"*80)
    print(f"STEUERREPORT {year} (Simulation Anlage KAP)")
    print("#"*80)
    
    events = [e for e in engine.tax_ledger if e.year == year]
    
    if not events:
        print("Keine steuerrelevanten Events in diesem Jahr.")
        return

    # --- 1. Veräußerungsgewinne (Aktien/ETFs) ---
    print(f"\n1. VERÄUSSERUNGSGEWINNE (Inkl. VAP-Korrektur)")
    print(f"{'Datum':<12} | {'Symbol':<8} | {'TFS':<5} | {'Roh-Gewinn':>12} | {'VAP Abzug':>10} | {'Steuerpfl.':>12}")
    print("-" * 75)
    
    sum_sell_taxable = D(0)
    
    for e in events:
        if e.type == 'SELL':
            inst = instrument_provider.get_instrument(e.isin, e.symbol)
            tfs_pct = inst.tfs_quote * 100
            
            print(f"{str(e.date):<12} | {e.symbol:<8} | {tfs_pct:>4.0f}% | "
                  f"{e.raw_profit:>12.2f} | {e.used_vap:>10.2f} | {e.taxable_profit:>12.2f}")
            sum_sell_taxable += e.taxable_profit
            
    print("-" * 75)
    print(f"SUMME Veräußerungen:{sum_sell_taxable:>53.2f} EUR")

    # --- 2. Vorabpauschalen (Zufluss 01.01. des Jahres) ---
    print(f"\n2. VORABPAUSCHALEN (VAP) - Fiktiver Zufluss")
    print(f"{'Datum':<12} | {'Symbol':<8} | {'TFS':<5} | {'VAP Roh':>12} | {'Steuerpfl.':>12}")
    print("-" * 60)
    
    sum_vap_taxable = D(0)
    
    for e in events:
        if e.type == 'VAP':
            inst = instrument_provider.get_instrument(e.isin, e.symbol)
            tfs_pct = inst.tfs_quote * 100
            
            print(f"{str(e.date):<12} | {e.symbol:<8} | {tfs_pct:>4.0f}% | "
                  f"{e.raw_profit:>12.2f} | {e.taxable_profit:>12.2f}")
            sum_vap_taxable += e.taxable_profit

    print("-" * 60)
    print(f"SUMME Vorabpauschalen:{sum_vap_taxable:>38.2f} EUR")

    # --- 3. Dividenden ---
    print(f"\n3. DIVIDENDEN (Laufende Erträge)")
    print(f"{'Datum':<12} | {'Symbol':<8} | {'TFS':<5} | {'Brutto':>12} | {'Steuerpfl.':>12}")
    print("-" * 60)
    
    sum_div_taxable = D(0)
    
    for e in events:
        if e.type == 'DIV':
            inst = instrument_provider.get_instrument(e.isin, e.symbol)
            tfs_pct = inst.tfs_quote * 100
            
            print(f"{str(e.date):<12} | {e.symbol:<8} | {tfs_pct:>4.0f}% | "
                  f"{e.raw_profit:>12.2f} | {e.taxable_profit:>12.2f}")
            sum_div_taxable += e.taxable_profit

    print("-" * 60)
    print(f"SUMME Dividenden:{sum_div_taxable:>43.2f} EUR")

    # --- 4. Zinsen ---
    print(f"\n4. ZINSEN (Fremdwährung & Cash)")
    print(f"{'Datum':<12} | {'Währung':<8} | {'Brutto EUR':>12}")
    print("-" * 40)
    
    sum_int_taxable = D(0)
    
    for e in events:
        if e.type == 'INT':
            # Ignoriere Bagatell-Zinsen < 1 Cent oder negative Zinsen für die Summe?
            # Wir zeigen alles an.
            print(f"{str(e.date):<12} | {e.symbol:<8} | {e.raw_profit:>12.2f}")
            sum_int_taxable += e.taxable_profit

    print("-" * 40)
    print(f"SUMME Zinsen:{sum_int_taxable:>27.2f} EUR")

    # --- 5. Quellensteuer (Anrechenbar) ---
    print(f"\n5. GEZAHLTE QUELLENSTEUER (Anrechenbar)")
    sum_wht = D(0)
    for e in events:
        if e.type == 'WHT' or e.foreign_wht > 0:
            val = e.foreign_wht if e.foreign_wht > 0 else abs(e.raw_profit) # WHT ist oft im raw_profit negativ bei Transaktionen
            # In engine speichern wir WHT als eigenen Typ mit foreign_wht > 0
            if e.type == 'WHT': val = e.foreign_wht
            
            print(f"{str(e.date):<12} | {e.symbol:<8} | {val:>12.2f}")
            sum_wht += val
            
    print("-" * 40)
    print(f"SUMME Anrechenbare QSt:{sum_wht:>17.2f} EUR")

    # --- ZUSAMMENFASSUNG ---
    total_income = sum_sell_taxable + sum_vap_taxable + sum_div_taxable + sum_int_taxable
    est_tax = (total_income * D('0.25')) * D('1.055') # 25% + Soli
    est_tax_final = est_tax - sum_wht # QSt wird abgezogen
    
    print("\n" + "="*80)
    print(f"GESAMTERGEBNIS {year}")
    print("="*80)
    print(f"Summe der steuerpflichtigen Kapitalerträge: {total_income:>20.2f} EUR")
    print(f"  davon Aktienveräußerungen (Verlusttopf):  {sum_sell_taxable:>20.2f} EUR")
    print(f"  davon Sonstiges (Vorab/Div/Zins):         {sum_vap_taxable + sum_div_taxable + sum_int_taxable:>20.2f} EUR")
    print("-" * 65)
    print(f"Geschätzte Steuerlast (25% + Soli):         {est_tax:>20.2f} EUR")
    print(f"Abzüglich anrechenbare Quellensteuer:       {-sum_wht:>20.2f} EUR")
    print("=" * 65)
    print(f"NACHZAHLUNG / ERSTATTUNG (ca.):             {est_tax_final:>20.2f} EUR")
    print("="*80 + "\n")


def main():
    print("--- TaxAlpha Report Generator ---")
    
    loader = IbkrXmlLoader("./input")
    prices = PriceProvider("./input/prices.csv")
    interest_provider = BaseInterestProvider("./input/basiszins.csv")
    instrument_provider = InstrumentProvider("./input/instruments.json")
    
    transactions = loader.load_all()
    engine = PortfolioEngine(interest_provider, instrument_provider)
    
    # Engine laufen lassen
    current_year = transactions[0].date_time.year
    max_year = transactions[-1].date_time.year
    
    tx_idx = 0
    total_tx = len(transactions)
    
    for year in range(current_year, max_year + 2):
        while tx_idx < total_tx and transactions[tx_idx].date_time.year == year:
            engine.process_transaction(transactions[tx_idx])
            tx_idx += 1
        
        engine.perform_year_end_closing(year, prices)
        
        # Report generieren für das Jahr (ab 2021 interessant)
        if year >= 2021:
            generate_annual_report(engine, year, instrument_provider)

if __name__ == "__main__":
    main()