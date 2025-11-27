from dataclasses import dataclass, field
from datetime import date, datetime
from decimal import Decimal
from typing import Optional

# Wir nutzen Decimal für Geld, um Rundungsfehler zu vermeiden
D = Decimal

@dataclass
class Instrument:
    symbol: str
    isin: str
    name: str = ""
    # Steuerliche Klassifizierung (manuell gepflegt)
    # 0.0 = Keine Teilfreistellung (Anleihen, Derivate, Crypto)
    # 0.15 = Mischfonds
    # 0.30 = Aktienfonds (ETF)
    # 0.60 / 0.80 = Immobilienfonds
    tfs_quote: Decimal = D('0.30') # Default auf Aktien-ETF (konservativ)

@dataclass
class TaxLot:
    """
    Repräsentiert einen Kauf (eine 'Kiste' im Regal).
    """
    id: str                 # Referenz zur Transaktions-ID des Kaufs
    symbol: str
    isin: str
    date_acquired: date     # Kaufdatum (für Spekulationsfrist relevant)
    
    original_quantity: Decimal
    remaining_quantity: Decimal
    
    # Anschaffungskosten (Inkl. Kaufgebühren!) in EUR
    acquisition_cost_total: Decimal 
    
    # Bereits gezahlte Vorabpauschalen auf diesen Lot (akkumuliert über die Jahre)
    accumulated_vap: Decimal = D('0.00')
    
    acquisition_price_origin: Decimal = D('0') # Kaufpreis in USD/GBP
    acquisition_fx_rate: Decimal = D('0')      # FX Rate beim Kauf

    @property
    def cost_per_share(self) -> Decimal:
        if self.original_quantity == 0: return D(0)
        return self.acquisition_cost_total / self.original_quantity

@dataclass
class TaxEvent:
    """
    Das Ergebnis eines Verkaufs oder einer Dividende für den Steuerreport.
    """
    year: int
    date: date
    type: str # 'SELL', 'DIV', 'VAP' (Vorabpauschale)
    symbol: str
    isin: str
    
    # Nur bei Verkäufen relevant:
    quantity_sold: Decimal = D('0')
    sale_proceeds: Decimal = D('0')     # Erlös (Kurs * Menge)
    sale_costs: Decimal = D('0')        # Verkaufsgebühren
    acquisition_costs: Decimal = D('0') # Einstandskurs der verkauften Anteile
    
    # Korrekturen
    used_vap: Decimal = D('0')          # Angerechnete Vorabpauschale (mindernd)
    
    # Ergebnis
    raw_profit: Decimal = D('0')        # Gewinn VOR Teilfreistellung
    taxable_profit: Decimal = D('0')    # Gewinn NACH Teilfreistellung
    
    # Quellensteuer (bereits gezahlt an der Quelle, z.B. USA 15%)
    foreign_wht: Decimal = D('0')
    
    # NEU: Debugging Felder für den Report
    date_acquired: Optional[date] = None # Wann wurde dieser Lot gekauft?
    buy_price_origin: Decimal = D('0')
    buy_fx: Decimal = D('0')
    sell_price_origin: Decimal = D('0')
    sell_fx: Decimal = D('0')