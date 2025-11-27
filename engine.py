from decimal import Decimal
from typing import List, Dict, Optional
from datetime import date
from models import TaxLot, TaxEvent, Instrument
from loader import RawTransaction, PriceProvider, BaseInterestProvider, InstrumentProvider

D = Decimal

class PortfolioEngine:
    def __init__(self, interest_provider: BaseInterestProvider, instrument_provider: InstrumentProvider):
        # Das "Regal": ISIN -> Liste von TaxLots
        self.portfolio: Dict[str, List[TaxLot]] = {}
        # Das Logbuch: Liste aller steuerlichen Events
        self.tax_ledger: List[TaxEvent] = []
        # Externe Datenquellen
        self.interest_provider = interest_provider
        self.instrument_provider = instrument_provider
        # Dividenden-Tracker pro Jahr und ISIN (für VAP-Abzug)
        self.distributions = {} # Key: (year, isin), Value: Decimal (Summe pro Stück)

    def get_instrument_config(self, isin: str, symbol: str) -> Instrument:
        return self.instrument_provider.get_instrument(isin, symbol)

    def process_transaction(self, tx: RawTransaction):
        """Hauptmethode: Verarbeitet eine RawTransaction"""
        
        # 1. Datum extrahieren
        tx_date = tx.date_time.date()
        year = tx_date.year
        inst = self.get_instrument_config(tx.isin, tx.symbol)

        # 2. Verteiler nach Typ
        if tx.action == 'BUY':
            self._handle_buy(tx, inst, tx_date)
        
        elif tx.action == 'SELL':
            self._handle_sell(tx, inst, tx_date, year)
            
        elif tx.action == 'DIV' or tx.action == 'WHT':
            # Dividenden behandeln wir separat, aber WHT gehört meist zur DIV
            # Einfachheitshalber: Wir summieren WHT bei der DIV auf oder loggen es separat
            self._handle_dividend(tx, inst, tx_date, year)

    def _handle_buy(self, tx: RawTransaction, inst: Instrument, tx_date: date):
        # Kaufkosten = (Preis * Menge) + Gebühren
        total_cost = tx.amount_eur + tx.fees_eur # Gebühren erhöhen den Einstandspreis!
        
        new_lot = TaxLot(
            id=tx.trans_id,
            symbol=tx.symbol,
            isin=tx.isin,
            date_acquired=tx_date,
            original_quantity=tx.quantity,
            remaining_quantity=tx.quantity,
            acquisition_cost_total=total_cost,
            accumulated_vap=D('0.00'),
            acquisition_price_origin=tx.price_origin,
            acquisition_fx_rate=tx.fx_rate_to_base
        )
        
        if tx.isin not in self.portfolio:
            self.portfolio[tx.isin] = []
        
        self.portfolio[tx.isin].append(new_lot)
        # print(f"[BUY] {tx.quantity} {tx.symbol} in neuen Lot gesteckt.")

    def _handle_sell(self, tx: RawTransaction, inst: Instrument, tx_date: date, year: int):
        qty_to_sell = abs(tx.quantity) # Menge ist im XML negativ bei Sell
        
        # Erlös = (Preis * Menge) - Verkaufsgebühren
        # amount_eur ist im XML negativ bei Sell (Geldzufluss), wir nutzen Absolutwerte für die Rechnung
        proceeds = abs(tx.amount_eur)
        fees = tx.fees_eur
        
        # Netto-Erlös pro Stück (für die proportionale Verteilung auf Lots egal, aber gut zu wissen)
        # Wir müssen den Erlös proportional auf die Lots verteilen, 
        # oder einfacher: Wir berechnen den Gewinn pro Lot.
        
        remaining_to_sell = qty_to_sell
        
        if tx.isin not in self.portfolio or not self.portfolio[tx.isin]:
            print(f"WARNUNG: Leerverkauf (Short) oder Datenfehler bei {tx.symbol} am {tx_date}! Keine Lots vorhanden.")
            return

        lots = self.portfolio[tx.isin]
        
        while remaining_to_sell > 0:
            if not lots:
                print(f"KRITISCH: Bestand leer bei Verkauf von {tx.symbol}!")
                break
                
            lot = lots[0] # FIFO: Nimm den ältesten
            
            take_qty = min(remaining_to_sell, lot.remaining_quantity)
            
            # Anteilige Kosten und Erlöse berechnen
            ratio = take_qty / qty_to_sell # Wieviel % des Trades ist dieser Teil?
            
            part_proceeds = proceeds * ratio
            part_sell_costs = fees * ratio
            
            # Anteilige Anschaffungskosten dieses Lots
            # (Total Cost / Original Qty) * Taken Qty
            lot_buy_price_avg = lot.acquisition_cost_total / lot.original_quantity
            part_acquisition_costs = lot_buy_price_avg * take_qty
            
            # Anteilige VAP anrechnen
            # (Total VAP / Remaining Qty vor dem Trade?) -> Nein, VAP hängt am Stück.
            # Vereinfachung: accumulated_vap ist Summe für den REST-Bestand.
            part_vap = (lot.accumulated_vap / lot.remaining_quantity) * take_qty if lot.remaining_quantity > 0 else D(0)
            
            # Gewinn Berechnung nach § 20 EStG
            # Gewinn = Erlös - Verkaufsgebühren - Anschaffungskosten - Vorabpauschalen
            raw_profit = part_proceeds - part_sell_costs - part_acquisition_costs - part_vap
            
            # Teilfreistellung
            taxable_profit = raw_profit * (D('1.0') - inst.tfs_quote)
            
            # Event erstellen
            event = TaxEvent(
                year=year,
                date=tx_date,
                type='SELL',
                symbol=tx.symbol,
                isin=tx.isin,
                quantity_sold=take_qty,
                sale_proceeds=part_proceeds,
                sale_costs=part_sell_costs,
                acquisition_costs=part_acquisition_costs,
                used_vap=part_vap,
                raw_profit=raw_profit,
                taxable_profit=taxable_profit,
                date_acquired=lot.date_acquired,
                buy_price_origin=lot.acquisition_price_origin,
                buy_fx=lot.acquisition_fx_rate,
                sell_price_origin=tx.price_origin,
                sell_fx=tx.fx_rate_to_base
            )
            self.tax_ledger.append(event)
            
            # Lot updaten
            # Da dataclass nicht frozen sein muss (wir haben es oben entfernt), können wir ändern:
            lot.remaining_quantity -= take_qty
            lot.accumulated_vap -= part_vap
            
            remaining_to_sell -= take_qty
            
            # Leere Lots entfernen
            if lot.remaining_quantity <= D('0.000001'): # Toleranz für float-math (auch wenn wir Decimal nutzen)
                lots.pop(0)

    def _handle_dividend(self, tx: RawTransaction, inst: Instrument, tx_date: date, year: int):
        # Einfache Implementierung: Volle Dividende ist steuerpflichtig
        # TODO: WHT (Quellensteuer) muss hier korrekt abgezogen/angerechnet werden
        
        # Bei IBKR kommen DIV und WHT oft als getrennte Transaktionen
        # Wenn es eine DIV ist:
        if tx.action == 'DIV':
            raw_amount = tx.amount_eur
            # TFS bei Dividenden? Ja, bei Aktienfonds 30%
            taxable = raw_amount * (D('1.0') - inst.tfs_quote)
            
            event = TaxEvent(
                year=year,
                date=tx_date,
                type='DIV',
                symbol=tx.symbol,
                isin=tx.isin,
                raw_profit=raw_amount,
                taxable_profit=taxable
            )
            self.tax_ledger.append(event)
        
        elif tx.action == 'WHT':
            # Das ist eine NEGATIVE Zahl (Abfluss)
            # Das mindert nicht den Gewinn, sondern die zu zahlende Steuer (Anrechnung)
            # Wir speichern es separat
            event = TaxEvent(
                year=year,
                date=tx_date,
                type='WHT',
                symbol=tx.symbol,
                isin=tx.isin,
                foreign_wht=abs(tx.amount_eur) # Speichern als positiven Wert (gezahlte Steuer)
            )
            self.tax_ledger.append(event)

        # NEU: Speichere Dividende pro Stück für VAP Berechnung
        if tx.action == 'DIV':
            # Wir brauchen die Menge zum Zeitpunkt der Ausschüttung.
            # Vereinfachung: Wir nehmen an, die Dividende gilt für alle aktuell gehaltenen Anteile.
            # In der Realität müssten wir 'tx.quantity' aus dem XML nehmen (wenn vorhanden) oder Bestand prüfen.
            # Da Cash-Tx oft Quantity=0 haben im XML, müssen wir den Bestand zum Stichtag nehmen.
            
            # Finde Bestand zum Datum
            current_lots = self.portfolio.get(tx.isin, [])
            total_qty = sum(l.remaining_quantity for l in current_lots)
            
            if total_qty > 0:
                div_per_share = tx.amount_eur / total_qty
                
                key = (year, tx.isin)
                if key not in self.distributions:
                    self.distributions[key] = D('0')
                self.distributions[key] += div_per_share

    def perform_year_end_closing(self, year: int, prices: PriceProvider):
        """
        Berechnet die Vorabpauschale zum 31.12. des Jahres.
        """
        # Zins aus CSV holen
        zins = self.interest_provider.get_rate(year) # <-- Hier Änderung

        if zins <= 0:
            return # Keine VAP in Negativzinsphasen

        basisfaktor = zins * D('0.7')
        
        print(f"\n--- Jahresabschluss {year} (Basiszins: {zins*100}%) ---")

        for isin, lots in self.portfolio.items():
            # 1. Schlusskurs holen
            eoy_price = prices.get_price(isin, year)
            if eoy_price is None:
                # Nur Warnung, wenn wir noch Lots haben
                if any(l.remaining_quantity > 0 for l in lots):
                    print(f"WARNUNG: Kein Schlusskurs für {isin} in {year}. VAP = 0 gesetzt.")
                continue

            # 2. Dividenden pro Stück in diesem Jahr holen
            dist_per_share = self.distributions.get((year, isin), D('0'))
 
            # WICHTIG: TFS für die VAP-Besteuerung holen!
            inst = self.get_instrument_config(isin, lots[0].symbol if lots else "")
            tfs = inst.tfs_quote

            for lot in lots:
                if lot.remaining_quantity <= 0:
                    continue
                
                # A. Basisertrag berechnen
                # Nach Gesetz: 70% des Basiszins auf den *Anfangswert* des Jahres
                # Anfangswert ist entweder Kaufpreis (wenn im Jahr gekauft) oder Kurs vom Vorjahr.
                # Vereinfachung für MVP: Wir nehmen immer den Einstandskurs (Vorsicht: Ungenau bei Altbeständen!)
                # KORREKTE VERSION: Wir müssten eigentlich den Startkurs des Jahres tracken.
                # Für den Anfang nutzen wir lot.acquisition_price_origin (umgerechnet in EUR) 
                # oder besser: lot.acquisition_cost_total / original_qty
                
                lot_buy_price = lot.acquisition_cost_total / lot.original_quantity
                
                # Wenn Lot dieses Jahr gekauft wurde, VAP anteilig (1/12 pro Monat)
                months = D('12')
                if lot.date_acquired.year == year:
                    months = D('12') - D(lot.date_acquired.month) + D('1')
                
                basisertrag = lot_buy_price * basisfaktor * (months / D('12'))
                
                # B. Wertsteigerung (Cap)
                # Wert Ende - Wert Anfang (bzw. Kauf)
                # ACHTUNG: Hier müssten wir eigentlich den Kurs vom Jahresanfang nehmen.
                # Da wir den State "Kurs letztes Jahr" nicht im Lot speichern, nehmen wir hier vereinfacht
                # den Kaufkurs als Untergrenze. Das ist eine Unschärfe, die wir später fixen können.
                # Für IGLN (gekauft vor 2023) ist der Gewinn aber eh meist > Basisertrag, also greift der Cap nicht.
                
                wertsteigerung = eoy_price - lot_buy_price
                if wertsteigerung < 0:
                    wertsteigerung = D('0') # Kein VAP bei Verlusten
                
                # C. Max VAP
                max_vap = min(basisertrag, wertsteigerung)
                
                # D. Tatsächliche VAP (Abzug Ausschüttungen)
                actual_vap_per_share = max_vap - dist_per_share
                if actual_vap_per_share < 0:
                    actual_vap_per_share = D('0')
                
                # E. Anwenden
                if actual_vap_per_share > 0:
                    total_vap = actual_vap_per_share * lot.remaining_quantity
                    
                    # 1. Auf Lot aufaddieren (damit Verkaufsgewinn später sinkt)
                    lot.accumulated_vap += total_vap
                    
                    # 2. Steuer-Event erzeugen (Zufluss theoretisch am 01.01. des Folgejahres)
                    event = TaxEvent(
                        year=year + 1, # Steuerlich relevant im Folgejahr!
                        date=date(year + 1, 1, 1), # Fiktives Datum
                        type='VAP',
                        symbol=lot.symbol,
                        isin=isin,
                        raw_profit=total_vap,
                        taxable_profit=total_vap * (D('1.0') - tfs) 
                    )
                    self.tax_ledger.append(event)
                    # print(f"   VAP für {lot.symbol}: {total_vap:.2f} EUR (Lot vom {lot.date_acquired})")