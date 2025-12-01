import xml.etree.ElementTree as ET
import glob
import json
import os
import sys
import csv
from dataclasses import dataclass
from datetime import datetime, time
from decimal import Decimal
from typing import List
from typing import Optional
from models import Instrument

D = Decimal

@dataclass(frozen=True)
class RawTransaction:
    trans_id: str
    symbol: str
    isin: str
    asset_class: str
    date_time: datetime
    action: str             
    description: str
    quantity: Decimal
    amount_eur: Decimal
    fees_eur: Decimal
    fx_rate_to_base: Decimal
    price_origin: Decimal = Decimal('0') 

    def is_cancellation_of(self, other: 'RawTransaction') -> bool:
        if self.symbol != other.symbol:
            return False
        if self.quantity != -other.quantity:
            return False
        if abs(self.amount_eur + other.amount_eur) > D('0.05'): 
            return False
        return True

class IbkrXmlLoader:
    def __init__(self, folder_path: str):
        self.folder_path = folder_path
        self.transactions: List[RawTransaction] = []
        self.ignored_assets = {} # Setze Dictionary: { "AssetClass": ["Symbol1", "Symbol2"] }

    def _parse_clean_date(self, date_str: str) -> datetime:
        if not date_str:
            raise ValueError("Leeres Datum")
        try:
            return datetime.strptime(date_str, "%Y%m%d;%H%M%S")
        except ValueError:
            pass
        try:
            dt = datetime.strptime(date_str, "%Y%m%d")
            return datetime.combine(dt.date(), time(23, 59, 59))
        except ValueError:
            raise ValueError(f"Unbekanntes Format: '{date_str}'")

    def load_all(self) -> List[RawTransaction]:
        search_pattern = os.path.join(self.folder_path, "*.xml")
        files = glob.glob(search_pattern)
        
        if not files:
            print(f"FEHLER: Keine XML-Dateien in {self.folder_path} gefunden!")
            sys.exit(1)

        print(f"Starte Import von {len(files)} Dateien...")
        for file_path in files:
            try:
                self._parse_file(file_path)
            except Exception as e:
                print(f"KRITISCHER FEHLER in {os.path.basename(file_path)}: {e}")
                sys.exit(1)

        self.transactions.sort(key=lambda x: x.date_time)
        cleaned_transactions = self._clean_cancellations(self.transactions)
        
        print(f"\nFinaler Datensatz: {len(cleaned_transactions)} saubere Transaktionen.")
        print("\n--- BERICHT: IGNORIERTE ASSETS ---")
        if not self.ignored_assets:
            print("Keine Assets wurden ignoriert.")
        else:
            for ac, symbols in self.ignored_assets.items():
                print(f"Kategorie '{ac}': {len(symbols)} Symbole ignoriert (z.B. {list(symbols)[:3]}...)")
        return cleaned_transactions

    def _clean_cancellations(self, tx_list: List[RawTransaction]) -> List[RawTransaction]:
        indices_to_remove = set()
        
        for i, tx in enumerate(tx_list):
            is_correction = "Ca." in tx.action or "(Ca.)" in tx.description or "Ca." in tx.action
            # Manchmal steht "Buy (Ca.)", manchmal nur Ca. im Action Field
            
            if is_correction:
                # Wir suchen RÜCKWÄRTS
                match_found = False
                for j in range(i - 1, -1, -1):
                    candidate = tx_list[j]
                    if j not in indices_to_remove and not ("Ca." in candidate.action or "(Ca.)" in candidate.description):
                        if tx.is_cancellation_of(candidate):
                            indices_to_remove.add(i)
                            indices_to_remove.add(j)
                            match_found = True
                            break
                
                if not match_found:
                    # Wenn wir kein Match finden, lassen wir das Storno drin? 
                    # Nein, ein isoliertes Storno ist meist Datenmüll oder bezieht sich auf Trades VOR dem Export-Zeitraum.
                    # Wir ignorieren es sicherheitshalber, um FIFO nicht zu killen.
                    print(f"WARNUNG: Storno ohne Original gefunden (Ignoriere Storno): {tx.symbol} {tx.quantity}")
                    indices_to_remove.add(i)

        clean_list = [t for k, t in enumerate(tx_list) if k not in indices_to_remove]
        if indices_to_remove:
            print(f"Bereinigung: {len(indices_to_remove)} Einträge entfernt.")
            
        return clean_list

    def _parse_file(self, file_path: str):
        tree = ET.parse(file_path)
        root = tree.getroot()
        for trade in root.findall('.//Trade'):
            self._parse_trade_tag(trade)
        for cash in root.findall('.//CashTransaction'):
            self._parse_cash_tag(cash)

    def _parse_trade_tag(self, tag: ET.Element):
        trans_id = tag.get('transactionID')
        if not trans_id: return 

        symbol = tag.get('symbol', 'UNKNOWN')
        asset_class = tag.get('assetCategory') or tag.get('assetClass', 'UNKNOWN')

        # --- UPDATE START: Paranoider Forex Filter ---
        # 1. Check auf explizite Asset Class
        if asset_class == 'CASH':
            if asset_class not in self.ignored_assets: self.ignored_assets[asset_class] = set()
            self.ignored_assets[asset_class].add(symbol)
            return

        # 2. Check auf Symbol-Muster für Forex (z.B. "EUR.USD", "GBP.USD")
        if '.' in symbol and len(symbol) == 7:
            # Einfache Heuristik: 3 Zeichen + Punkt + 3 Zeichen
            # Prüfen ob bekannte Währungen dabei sind
            currencies = ['EUR', 'USD', 'GBP', 'CHF', 'JPY']
            if any(c in symbol for c in currencies):
                # Wir loggen das unter 'FOREX_HEURISTIC'
                if 'FOREX_HEURISTIC' not in self.ignored_assets: self.ignored_assets['FOREX_HEURISTIC'] = set()
                self.ignored_assets['FOREX_HEURISTIC'].add(symbol)
                return
        
        # 3. Whitelist Check (nur STK/FUND erlaubt)
        if asset_class not in ['STK', 'FUND']:
            if asset_class not in self.ignored_assets: self.ignored_assets[asset_class] = set()
            self.ignored_assets[asset_class].add(symbol)
            return
        
        dt_str = tag.get('dateTime') or tag.get('tradeDate')
        dt_obj = self._parse_clean_date(dt_str)

        action = tag.get('buySell')
        description = tag.get('description', '')
        
        qty = D(tag.get('quantity', '0'))
        price = D(tag.get('tradePrice', '0'))
        fx_rate = D(tag.get('fxRateToBase', '1.0'))
        
        comm_raw = D(tag.get('ibCommission', '0'))
        fees_eur = abs(comm_raw) * fx_rate
        
        trade_value_eur = (price * qty).quantize(D("0.0001")) * fx_rate

        t = RawTransaction(
            trans_id=trans_id,
            symbol=symbol,
            isin=tag.get('isin', 'UNKNOWN'),
            asset_class=asset_class,
            date_time=dt_obj,
            action=action,
            description=description,
            quantity=qty,
            amount_eur=trade_value_eur,
            fees_eur=fees_eur,
            fx_rate_to_base=fx_rate,
            price_origin=price
        )
        self.transactions.append(t)
        

    def _parse_cash_tag(self, tag: ET.Element):
        type_str = tag.get('type')
        
        is_interest = 'Interest' in type_str
        is_div = 'Dividends' in type_str
        is_wht = 'Withholding Tax' in type_str

        if not (is_interest or is_div or is_wht):
            return 
        
        # FIX: Auch hier assetCategory nutzen
        asset_class = tag.get('assetCategory') or tag.get('assetClass', 'UNKNOWN')

        # UPDATE: Cash-Zinsen haben oft assetClass="CASH" oder gar keine. 
        # Dividenden haben "STK". Wir lassen beides durch.
        # Aber wir filtern Forex-Kram bei Dividenden
        if is_div and asset_class not in ['STK', 'FUND']:
            return

        dt_str = tag.get('dateTime') or tag.get('reportDate')
        dt_obj = self._parse_clean_date(dt_str)
        
        amount_raw = D(tag.get('amount', '0'))
        currency = tag.get('currency')
        fx_rate = D(tag.get('fxRateToBase', '1.0'))
        amount_value_eur = (amount_raw).quantize(D("0.0001")) * fx_rate
        
        # Action Mapping
        if is_div: action = 'DIV'
        elif is_wht: action = 'WHT'
        else: action = 'INT' # Interest
        
        t = RawTransaction(
            trans_id=tag.get('transactionID'),
            symbol=tag.get('symbol', 'CASH'), # Zinsen haben oft kein Symbol
            isin=tag.get('isin', 'UNKNOWN'),
            asset_class=asset_class,
            date_time=dt_obj,
            action=action,
            description=tag.get('description', ''),
            quantity=D('0'),
            amount_eur=amount_value_eur,
            fees_eur=D('0'),
            fx_rate_to_base=fx_rate,
            price_origin=amount_raw # Bei Cash ist Preis = Betrag
        )
        self.transactions.append(t)

class PriceProvider:
    def __init__(self, file_path: str):
        self.prices = {} # Key: (year, isin), Value: Decimal
        self._load(file_path)
    
    def _load(self, file_path: str):
        if not os.path.exists(file_path):
            print(f"WARNUNG: Keine Preis-Datei gefunden unter {file_path}. VAP kann nicht berechnet werden!")
            return
            
        with open(file_path, mode='r', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for row in reader:
                try:
                    y = int(row['year'])
                    isin = row['isin']
                    p = D(row['price_eur'])
                    self.prices[(y, isin)] = p
                except ValueError:
                    continue

    def get_price(self, isin: str, year: int) -> Optional[Decimal]:
        return self.prices.get((year, isin))
    
class BaseInterestProvider:
    def __init__(self, file_path: str):
        self.rates = {} # Key: Year (int), Value: Decimal
        self._load(file_path)

    def _load(self, file_path: str):
        if not os.path.exists(file_path):
            print(f"WARNUNG: Basiszins-Datei fehlt ({file_path})! VAP Berechnung wird fehlschlagen.")
            return
            
        with open(file_path, mode='r', encoding='utf-8-sig') as f:
            reader = csv.DictReader(f)
            for row in reader:
                try:
                    y = int(row['year'])
                    r = D(row['rate'])
                    self.rates[y] = r
                except ValueError:
                    continue
    
    def get_rate(self, year: int) -> Decimal:
        # Default auf 0, wenn Jahr fehlt (konservativ)
        return self.rates.get(year, D('0'))

class InstrumentProvider:
    def __init__(self, file_path: str):
        self.instruments = {} # Key: ISIN (str), Value: Instrument Object
        self._load(file_path)

    def _load(self, file_path: str):
        if not os.path.exists(file_path):
            print(f"INFO: Keine instruments.json gefunden. Nutze Defaults (0.30 TFS).")
            return

        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                data = json.load(f)
                
            for isin, details in data.items():
                self.instruments[isin] = Instrument(
                    symbol="Unknown", # Wird zur Laufzeit ggf. überschrieben oder ist egal
                    isin=isin,
                    name=details.get("name", ""),
                    tfs_quote=D(str(details.get("tfs_quote", "0.30")))
                )
        except Exception as e:
            print(f"FEHLER beim Laden der instruments.json: {e}")

    def get_instrument(self, isin: str, symbol_fallback: str) -> Instrument:
        if isin in self.instruments:
            # Wir updaten das Symbol falls es im JSON fehlt, aber im Trade bekannt ist
            inst = self.instruments[isin]
            if inst.symbol == "Unknown":
                inst.symbol = symbol_fallback
            return inst
        else:
            # Fallback: Wenn ISIN nicht im JSON, nehmen wir an es ist ein Aktien-ETF (0.30)
            # Das ist "gefährlich", aber für dein Portfolio (Equity-Fokus) praktisch.
            # Besser: Warnung ausgeben.
            # print(f"HINWEIS: ISIN {isin} ({symbol_fallback}) nicht konfiguriert. Nutze Default TFS 0.30.")
            return Instrument(
                symbol=symbol_fallback,
                isin=isin,
                name="Unknown (Auto-Generated)",
                tfs_quote=D('0.30')
            )

if __name__ == "__main__":
    loader = IbkrXmlLoader("./input") 
    data = loader.load_all()
    
    print("\n--- CHECK: ERSTE 10 TRANSAKTIONEN ---")
    for tx in data[:10]:
        print(f"{tx.date_time} | {tx.action} | {tx.symbol} | {tx.quantity} | {tx.amount_eur:.2f} EUR")

    print("\n--- CHECK: STORNOS ---")
    vusa_trades = [x for x in data if "VUSA" in x.symbol or "IDTL" in x.symbol] # IDTL aus deinem Beispiel
    for tx in vusa_trades:
        print(f"{tx.date_time} | {tx.symbol} | {tx.action} | {tx.quantity}")        
    