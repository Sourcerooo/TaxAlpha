# Design & Requirement Specification: IBKR German Tax Engine (Project: "TaxAlpha")

## 1. Executive Summary
Entwicklung einer Python-basierten Steuer-Engine, die Handelsdaten von Interactive Brokers (IBKR) verarbeitet und einen steuerlichen Report nach deutschem Investmentsteuergesetz (InvStG) und Einkommensteuergesetz (EStG) erstellt.
Der Fokus liegt auf **Exaktheit**, **Revisionssicherheit (FIFO)** und der korrekten Berechnung der **Vorabpauschale (VAP)** für ETFs.

Das System folgt dem Prinzip "The Holy Ledger": Die Historie wird bei jedem Lauf komplett neu aus den Rohdaten berechnet, um Konsistenz zu garantieren.

---

## 2. System Architecture

Das System folgt einer modularen **Pipeline-Architektur**. Komponenten sind über abstrakte Interfaces gekoppelt, um zukünftige Erweiterungen (Datenbank, API-Preisabfragen) zu ermöglichen.

### 2.1 High-Level Komponenten
1.  **Ingestion Layer:** Liest Rohdaten (XML, JSON, CSV).
2.  **Model Layer:** Standardisierte Datenobjekte (`Transaction`, `TaxLot`, `Instrument`).
3.  **Processing Core (The Engine):** Implementiert die Steuerlogik (FIFO, VAP, Töpfe).
4.  **Reporting Layer:** Generiert die Ausgabedateien.

### 2.2 Datenfluss
`[IBKR XML] + [Manual Configs] -> [Ingestion] -> [Event Stream (Chronological)] -> [Tax Engine (State Machine)] -> [Yearly Tax Report]`

---

## 3. Funktionale Anforderungen (Requirements)

### 3.1 Datenimport (Ingestion)
*   **REQ-IN-01 (IBKR Flex Query):** Das System muss IBKR Flex Query XML Dateien parsen.
    *   *Constraint:* Es werden nur `TradeConfirmation` (für Käufe/Verkäufe) und `CashTransaction` (für Dividenden/Steuern) auf Einzelzeilen-Ebene verarbeitet. Aggregierte Sektionen werden ignoriert.
*   **REQ-IN-02 (Währung):** Beträge müssen zum Zeitpunkt der Transaktion in EUR umgerechnet werden.
    *   *Logic:* Nutzung des Feldes `amountInBase` (wenn Base=EUR) oder `fxRateToBase` aus der IBKR XML pro Transaktion.
*   **REQ-IN-03 (Historische Preise):** Für die VAP-Berechnung müssen Jahresschlusskurse importiert werden.
    *   *Initial:* CSV-Datei (`eoy_prices.csv`).
    *   *Design:* Interface `IPriceProvider`, um später APIs anzubinden.

### 3.2 Steuer-Logik (Core)
*   **REQ-CORE-01 (FIFO):** Verkäufe müssen strikt nach First-In-First-Out Prinzip gegen bestehende Bestände (`TaxLots`) verrechnet werden.
*   **REQ-CORE-02 (Verlustverrechnungstöpfe):** Unterscheidung der Ergebnisse nach:
    *   Topf 1: Aktien (Verluste nur mit Aktiengewinnen verrechenbar).
    *   Topf 2: Sonstiges (ETFs, Derivate, Dividenden, Zinsen).
*   **REQ-CORE-03 (Teilfreistellung - TFS):** Anwendung von Freistellungsquoten basierend auf dem Fondstyp.
    *   Aktienfonds: 30% Steuerfrei.
    *   Mischfonds: 15% Steuerfrei.
    *   Sonstige: 0%.
    *   *Source:* Manuelles Mapping via `instruments.json`.
*   **REQ-CORE-04 (Vorabpauschale - VAP):**
    *   Berechnung am 31.12. für alle offenen Lots.
    *   Formel: `Max(0, Min(Basisertrag, Wertsteigerung)) - Ausschüttungen`.
    *   Der "Basisertrag" wird mit dem BMF-Basiszins des jeweiligen Jahres berechnet.
    *   Die berechnete VAP wird auf dem `TaxLot` gespeichert (`accumulated_vap`) und mindert den Gewinn bei späterem Verkauf.
*   **REQ-CORE-05 (Kapitalmaßnahmen):** Manuelle Korrekturen (Splits/Mergers) müssen vor der FIFO-Logik angewandt werden.

### 3.3 Reporting
*   **REQ-OUT-01 (Jahresreport):** Ausgabe der steuerrelevanten Summen pro Jahr (Anlage KAP Zeilen-Simulation).
*   **REQ-OUT-02 (Audit Trail):** Optionaler detaillierter Log, welcher Lot wann verkauft wurde (für Nachvollziehbarkeit bei Prüfungen).

---

## 4. Daten-Schnittstellen (Input Contracts)

### 4.1 ETF Stammdaten (`instruments.json`)
Hier definierst du manuell deine ETFs, da IBKR diese Klassifizierung nicht liefert.
```json
{
  "IE00B5BMR087": {
    "symbol": "SXR8",
    "name": "iShares Core S&P 500 UCITS ETF",
    "type": "ETF_EQUITY", 
    "tfs_ratio": 0.30
  },
  "US0378331005": {
    "symbol": "AAPL",
    "name": "Apple Inc.",
    "type": "STOCK",
    "tfs_ratio": 0.00
  }
}
```

### 4.2 Jahresschlusskurse (`eoy_prices.csv`)
Manuell gepflegt (Phase 1). Kurse in EUR.
```csv
year,isin,price_eur
2023,IE00B5BMR087,450.50
2023,US0378331005,175.20
```

### 4.3 Manuelle Korrekturen (`corp_actions.json`)
```json
[
  {
    "date": "2024-06-15",
    "isin": "US123456789",
    "type": "SPLIT",
    "ratio": 10.0, 
    "comment": "1:10 Stock Split"
  }
]
```

---

## 5. Software Design (Python Classes)

### 5.1 Module Structure
```
/tax_engine
    /data_loaders
        - interface.py (IDataLoader)
        - ibkr_xml_loader.py
    /price_providers
        - interface.py (IPriceProvider)
        - csv_provider.py
    /models
        - transaction.py (DataClass)
        - tax_lot.py (DataClass with mutable state for VAP)
        - instrument.py
    /core
        - engine.py (Main Logic)
        - calculator.py (Math helpers, VAP logic)
    /reporting
        - report_generator.py
```

### 5.2 Core Logic Flow (Pseudocode)

```python
def run_pipeline():
    # 1. Setup
    instruments = load_instruments("instruments.json")
    prices = CsvPriceProvider("eoy_prices.csv")
    adjustments = load_adjustments("corp_actions.json")
    
    # 2. Load History
    # WICHTIG: Sortiert nach Date + Time
    transactions = IbkrXmlLoader("data/*.xml").get_all_transactions()
    
    # 3. Apply Adjustments (Virtual Transactions)
    transactions = inject_adjustments(transactions, adjustments)

    portfolio = Portfolio()
    years_to_process = [2020, 2021, 2022, 2023, 2024, 2025]

    for year in years_to_process:
        print(f"--- Processing {year} ---")
        
        # A. Filter Trades für dieses Jahr
        yearly_tx = [t for t in transactions if t.year == year]
        
        # B. FIFO Abarbeitung
        for tx in yearly_tx:
            portfolio.process_transaction(tx)
            
        # C. Jahresabschluss (VAP) am 31.12.
        basis_zins = get_bmf_zins(year)
        
        for position in portfolio.open_positions:
            eoy_price = prices.get_price(position.isin, year)
            if eoy_price:
                vap = calculate_vap(position, eoy_price, basis_zins)
                position.apply_vap(vap) # Erhöht accumulated_vap
                report.add_vap_entry(year, position, vap)
            else:
                log_warning(f"Kein EOY Preis für {position.isin} in {year}!")

    # 4. Generate Report
    generate_excel_report(portfolio.ledger)
```

---

## 6. Roadmap & Phasen

### Phase 1 (MVP) - Das Ziel für jetzt
*   Input: Lokale XML Files + Manuelle JSONs/CSVs.
*   Output: Textbasierter Report auf der Konsole / einfache CSV.
*   Scope: Nur Aktien und ETFs (keine Futures/Optionen in der ersten Iteration der Steuerlogik, da hier andere Verlusttöpfe gelten - das fügen wir hinzu, sobald das Grundgerüst steht).

### Phase 2 (Komfort)
*   Automatischer Download der EOY-Kurse (z.B. via Yahoo Finance API Wrapper).
*   PDF-Export.

### Phase 3 (Performance & Scale)
*   Datenbank-Layer (SQLite) zum Cachen von geparsten Trades.

---
