# Script robusto per estrarre shapefile da ZIP e convertirli in CSV, gestendo grandi dataset a chunk.
# Per ogni shapefile trovato viene generato un CSV dedicato, con serializzazione JSON a chunk.
# Barra di avanzamento e messaggi di debug inclusi.

import geopandas as gpd
import pandas as pd
import zipfile
import os
import tempfile
import shutil
from tkinter import Tk, filedialog
from tqdm import tqdm

# Import robusto di ujson, con fallback su json standard
try:
    import ujson as json
except ImportError:
    import json

def format_italian(value):
    """Formatta numeri secondo la convenzione italiana."""
    if isinstance(value, float):
        return f"{value:,.2f}".replace(",", "X").replace(".", ",").replace("X", ".")
    elif isinstance(value, int):
        return f"{value:,}".replace(",", ".")
    return value

def serializza_dati_variabili(df, d20_cols, chunk_size=100_000):
    """
    Serializza la colonna DATI_VARIABILI a chunk per evitare memory error su grandi dataset.
    Ritorna una lista con i JSON serializzati, uno per ogni riga del dataframe.
    """
    dati_variabili_serializzati = []
    for start in range(0, len(df), chunk_size):
        chunk = df.iloc[start:start+chunk_size]
        d20_df = chunk[d20_cols]
        d20_dicts = d20_df.where(pd.notnull(d20_df)).to_dict(orient="records")
        for row in d20_dicts:
            dati_variabili_serializzati.append(
                json.dumps({k: v for k, v in row.items() if pd.notnull(v)}, ensure_ascii=False)
            )
    return dati_variabili_serializzati

def salva_csv_chunked(df, output_csv, colonne_finali, chunk_size=100_000):
    """
    Salva un DataFrame molto grande in CSV, scrivendo a chunk per risparmiare memoria.
    """
    # Scrive l'intestazione
    df.iloc[:0].to_csv(output_csv, columns=colonne_finali, index=False, sep='|', lineterminator='\n')
    # Scrive i dati a chunk
    for start in tqdm(range(0, len(df), chunk_size), desc=f"Scrittura CSV: {os.path.basename(output_csv)}", unit="chunk"):
        end = min(start + chunk_size, len(df))
        df.iloc[start:end].to_csv(output_csv, mode='a', header=False, columns=colonne_finali, 
                                 index=False, sep='|', lineterminator='\n')

def main():
    print("=== AVVIO SCRIPT ===")

    steps = [
        "Estrazione archivio",
        "Analisi shapefile",
        "Conversione e salvataggio CSV",
        "Pulizia finale"
    ]

    with tqdm(total=len(steps), desc="Avanzamento globale", ncols=90, bar_format='{l_bar}{bar}| {n_fmt}/{total_fmt} [{elapsed}<{remaining}]', position=0) as t_global:

        # 1️⃣ Selezione file ZIP tramite finestra grafica
        print("🪟 [1] Seleziona il file ZIP...")
        root = Tk()
        root.withdraw()
        zip_path = filedialog.askopenfilename(
            title="Seleziona il file ZIP", filetypes=[("Zip files", "*.zip")]
        )
        if not zip_path:
            print("❌ Nessun file ZIP selezionato. Interruzione.")
            return
        print(f"📦 [1] File ZIP selezionato: {zip_path}")
        t_global.set_postfix_str("Selezione archivio OK")
        t_global.update(1)

        # 2️⃣ Estrazione archivio ZIP
        print("📂 [2] Apertura archivio ZIP e lista file contenuti...")
        temp_dir = tempfile.mkdtemp()
        try:
            with zipfile.ZipFile(zip_path, 'r') as zipref:
                print("🟢 Archivio ZIP aperto con successo.")
                print("🗂️  File presenti nello ZIP:")
                for f in zipref.namelist():
                    print(f"  - {f}")
                zipref.extractall(temp_dir)
            print(f"✅ [2] Estrazione completata in: {temp_dir}")
        except Exception as e:
            print(f"❌ Errore durante l'apertura o estrazione del file ZIP: {e}")
            return
        t_global.set_postfix_str("Archivio estratto")
        t_global.update(1)

        # 3️⃣ Analisi dei file estratti (ricerca shapefile completa e robusta)
        print("🔍 [3] Analisi dei file estratti (ricerca shapefile completa e robusta)...")
        shapefiles = []
        for rootdir, dirs, files in os.walk(temp_dir):
            files_map = {f.lower(): f for f in files}
            for file in files:
                if file.lower().endswith(".shp"):
                    base_name = os.path.splitext(file)[0]
                    base_name_lower = base_name.lower()
                    required_extensions = [".shx", ".dbf"]
                    missing = []
                    for ext in required_extensions:
                        expected = base_name_lower + ext
                        if expected not in files_map:
                            missing.append(ext)
                    if missing:
                        print(f"⚠️ Shapefile incompleto: {file} - Mancano: {missing}")
                    else:
                        print(f"✅ Shapefile valido trovato: {file}")
                        shapefiles.append(os.path.join(rootdir, file))
        t_global.set_postfix_str("Shapefile analizzati")
        t_global.update(1)

        # 4️⃣ Conversione e salvataggio dei singoli shapefile
        if shapefiles:
            print(f"📤 Inizio conversione {len(shapefiles)} shapefile in CSV dedicati...")
            # Cartella di uscita (stessa dello zip)
            output_folder = os.path.dirname(zip_path)
            for idx, shp_path in enumerate(shapefiles, 1):
                shp_basename = os.path.splitext(os.path.basename(shp_path))[0]
                print(f"\n[{idx}/{len(shapefiles)}] ➡️ Processamento {shp_basename}.shp")
                try:
                    # Lettura shapefile
                    gdf = gpd.read_file(shp_path)
                    print(f"   ➕ Letto: {shp_path} ({len(gdf)} record)")

                    # Colonna di origine dati
                    gdf["ORIGINE_DATI"] = os.path.basename(shp_path)

                    # Serializzazione colonne D20 in DATI_VARIABILI (a chunk)
                    d20_cols = [col for col in gdf.columns if col.startswith("D20")]
                    if d20_cols:
                        print(f"   📋 Serializzo colonne D20: {d20_cols} a JSON...")
                        gdf["DATI_VARIABILI"] = serializza_dati_variabili(gdf, d20_cols)
                        print(f"   ✅ Serializzazione completata.")
                    else:
                        gdf["DATI_VARIABILI"] = ["{}"] * len(gdf)

                    # Formattazione numerica secondo convenzione italiana
                    print("   🔢 Applico formattazione numerica italiana...")
                    num_cols = gdf.select_dtypes(include=["float", "int"]).columns
                    for col in num_cols:
                        gdf[col] = gdf[col].apply(format_italian)
                    print("   ✅ Formattazione numerica completata.")

                    # Ordine colonne: prima tutte le altre, poi geometry, ORIGINE_DATI, DATI_VARIABILI
                    colonne_extra = ["geometry", "ORIGINE_DATI", "DATI_VARIABILI"]
                    colonne_finali = [c for c in gdf.columns if c not in colonne_extra] + [c for c in colonne_extra if c in gdf.columns]

                    # Esportazione CSV a chunk
                    output_csv = os.path.join(output_folder, f"{shp_basename}.csv")
                    print(f"   💾 Esporto CSV: {output_csv}")
                    salva_csv_chunked(gdf, output_csv, colonne_finali)
                    print(f"   🟢 CSV generato con successo: {output_csv}")

                except Exception as e:
                    print(f"   ❌ Errore durante la conversione di {shp_path}: {e}")
            t_global.set_postfix_str("Tutti gli shapefile processati")
            t_global.update(1)
        else:
            print("❌ Nessuno shapefile completo trovato nell'archivio.")

        # 5️⃣ Pulizia finale della cartella temporanea
        print("🧹 Pulizia dei file temporanei...")
        shutil.rmtree(temp_dir)
        print("🏁 Fine del processo.")
        t_global.set_postfix_str("Processo completato")
        t_global.update(1)

if __name__ == "__main__":
    main()