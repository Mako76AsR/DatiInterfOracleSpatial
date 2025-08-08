import pandas as pd
from sqlalchemy import create_engine
import oracledb
from tkinter import Tk, filedialog, simpledialog
from tqdm import tqdm
import sys
import json
import os
import zipfile
import tempfile
import shutil
import psutil
from concurrent.futures import ThreadPoolExecutor, as_completed
import csv

def converti_numero_italiano(val):
    """
    Converte una stringa numerica in formato italiano (es: '1.234,56')
    in un float utilizzabile da Oracle (es: 1234.56).
    Restituisce None se non convertibile.
    """
    try:
        if pd.isna(val) or str(val).strip() in ["", "nan", "None"]:
            return None
        val = str(val).replace('.', '').replace(',', '.')
        return float(val)
    except Exception:
        return None

def converti_json_numeri_italiani(json_str):
    """
    Converte tutti i valori numerici in formato italiano all'interno di un JSON (stringa).
    Restituisce una stringa JSON con i numeri convertiti a float standard.
    """
    try:
        if pd.isna(json_str) or str(json_str).strip() in ["", "nan", "None"]:
            return "{}"
        d = json.loads(json_str)
        for k, v in d.items():
            if isinstance(v, str):
                try:
                    num = v.replace('.', '').replace(',', '.')
                    d[k] = float(num)
                except Exception:
                    pass
        return json.dumps(d, ensure_ascii=False)
    except Exception:
        return json_str

def scegli_modalita():
    """
    Permette all'utente di scegliere la modalità di importazione tramite finestra.
    """
    msg = "Seleziona la modalità di importazione:\n\n1. Singolo file CSV\n2. Cartella con CSV\n3. Archivio ZIP con CSV"
    root = Tk()
    root.withdraw()
    scelta = simpledialog.askinteger("Scelta modalità", msg, minvalue=1, maxvalue=3)
    root.destroy()
    if scelta == 1:
        return "file"
    elif scelta == 2:
        return "cartella"
    elif scelta == 3:
        return "zip"
    else:
        print("❌ Scelta non valida. Uscita.")
        sys.exit(1)

def seleziona_input(tipo):
    """
    Guida l'utente nella selezione del file, cartella o ZIP.
    """
    root = Tk()
    root.withdraw()
    if tipo == "file":
        file_path = filedialog.askopenfilename(
            title="Seleziona il file CSV da importare",
            filetypes=[("CSV Files", "*.csv")]
        )
        root.destroy()
        return [file_path] if file_path else [], None
    elif tipo == "cartella":
        folder_path = filedialog.askdirectory(
            title="Seleziona la cartella contenente i file CSV"
        )
        root.destroy()
        if not folder_path:
            return [], None
        file_list = [os.path.join(folder_path, f) for f in os.listdir(folder_path) if f.lower().endswith('.csv')]
        return file_list, None
    elif tipo == "zip":
        zip_path = filedialog.askopenfilename(
            title="Seleziona il file ZIP contenente i file CSV",
            filetypes=[("ZIP Archives", "*.zip")]
        )
        root.destroy()
        if not zip_path:
            return [], None
        temp_dir = tempfile.mkdtemp()
        with zipfile.ZipFile(zip_path, "r") as zip_ref:
            zip_ref.extractall(temp_dir)
        file_list = []
        for dirpath, _, filenames in os.walk(temp_dir):
            file_list.extend([os.path.join(dirpath, f) for f in filenames if f.lower().endswith('.csv')])
        return file_list, temp_dir
    else:
        return [], None

def determina_num_worker():
    """
    Determina dinamicamente il numero massimo di thread in base alle CPU disponibili.
    """
    cpu_count = psutil.cpu_count(logical=False) or os.cpu_count() or 2
    max_worker = max(1, min(4, cpu_count - 1))  # Limita a max 4 thread per sicurezza
    return max_worker

def importa_csv_su_oracle(file_csv, conn_str, nome_tabella, mappa_colonne, colonne_numeriche):
    """
    Importa un singolo file CSV su Oracle, committando solo se tutto il file va a buon fine.
    Restituisce un dizionario con esito, righe importate e messaggio di errore.
    """
    print(f"\n=== Inizio importazione file: {file_csv} ===")
    chunk_size = 50000
    n_imported = 0
    # Ogni thread crea la propria connessione per sicurezza
    engine = create_engine(conn_str)
    try:
        with engine.begin() as conn:  # Gestione transazione
            try:
                with open(file_csv, 'r', encoding="utf-8") as f:
                    total_rows = sum(1 for _ in f) - 1
            except Exception:
                total_rows = None
            reader = pd.read_csv(file_csv, chunksize=chunk_size, iterator=True, encoding="utf-8", sep='|')
            with tqdm(total=total_rows, desc=f"Caricamento {os.path.basename(file_csv)}", ncols=90, unit="righe") as pbar:
                for chunk in reader:
                    colonne_da_escludere = [col for col in chunk.columns if col.startswith("D20")]
                    if colonne_da_escludere:
                        chunk.drop(columns=colonne_da_escludere, inplace=True)
                    for col in colonne_numeriche:
                        if col in chunk.columns:
                            chunk[col] = chunk[col].apply(converti_numero_italiano)
                    if 'DATI_VARIABILI' in chunk.columns:
                        chunk['DATI_VARIABILI'] = chunk['DATI_VARIABILI'].apply(converti_json_numeri_italiani)
                    colonne_presenti = [col for col in mappa_colonne if col in chunk.columns]
                    mappatura_filtrata = {col: mappa_colonne[col] for col in colonne_presenti}
                    if mappatura_filtrata:
                        chunk.rename(columns=mappatura_filtrata, inplace=True)
                    chunk.to_sql(nome_tabella, con=conn, if_exists='append', index=False)
                    n_imported += len(chunk)
                    pbar.update(len(chunk))
            # Commit automatico se nessuna eccezione
            print(f"🏁 Importazione completata. Righe totali importate: {n_imported}")
            return {"file": file_csv, "esito": "OK", "righe_importate": n_imported, "errore": ""}
    except Exception as e:
        print(f"❌ Errore durante l'importazione di {file_csv}: {e}")
        return {"file": file_csv, "esito": "ERRORE", "righe_importate": n_imported, "errore": str(e)}
    finally:
        engine.dispose()

def scrivi_log(log_attivita, nome_file="riepilogo_import_log.csv"):
    """
    Scrive un file CSV di log con esiti e dettagli delle importazioni.
    """
    with open(nome_file, 'w', newline='', encoding='utf-8') as csvfile:
        fieldnames = ['file', 'esito', 'righe_importate', 'errore']
        writer = csv.DictWriter(csvfile, fieldnames=fieldnames)
        writer.writeheader()
        for row in log_attivita:
            writer.writerow(row)
    print(f"📝 Log attività scritto in: {nome_file}")

def main():
    print("=== INIZIO SCRIPT IMPORTAZIONE CSV SU ORACLE (PARALLELO) ===")

    # 1️⃣ Selezione modalità input
    tipo_input = scegli_modalita()
    file_csv_list, temp_dir = seleziona_input(tipo_input)
    if not file_csv_list:
        print("❌ Nessun file selezionato. Uscita.")
        sys.exit(1)
    print(f"✅ File da importare: {file_csv_list}")

    # 2️⃣ Parametri Oracle (da modificare secondo ambiente)
    username = "dwhadm"
    password = "dwhadm"
    host = "ecsnp-zjydh-scan.snvcnnplindbcli.vcnnplin.oraclevcn.com"
    port = 1521
    service = "dwhsvil"
    nome_tabella = "geo_dati_interferometrici_prv"
    conn_str = f'oracle+oracledb://{username}:{password}@{host}:{port}/?service_name={service}'

    mappa_colonne = {
        'CODE': 'CODE',
        'HEIGHT': 'HEIGHT',
        'H_STDEV': 'H_STDEV',
        'VEL': 'VEL',
        'V_STDEV': 'V_STDEV',
        'COHE': 'COHE',
        'geometry': 'GEOMETRY',
        '__source__': 'ORIGINE_DATO',
        'ORIGINE_DATI': 'ORIGINE_DATO',
        'DATI_VARIABILI': 'DATI_VARIABILI'
    }
    colonne_numeriche = ['VEL', 'HEIGHT', 'H_STDEV', 'V_STDEV', 'COHE']

    # 3️⃣ Determina thread disponibili
    #max_workers = determina_num_worker()
    #forzo il numero dei palleli ad 1 perchè da errore
    max_workers =1
    print(f"🚦 Numero di thread usati: {max_workers}")

    # 4️⃣ Esecuzione parallela importazione
    log_attivita = []
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        future_to_file = {
            executor.submit(
                importa_csv_su_oracle, file_csv, conn_str,
                nome_tabella, mappa_colonne, colonne_numeriche
            ): file_csv for file_csv in file_csv_list
        }
        for future in as_completed(future_to_file):
            risultato = future.result()
            log_attivita.append(risultato)

    # 5️⃣ Log riepilogativo
    scrivi_log(log_attivita)

    # 6️⃣ Pulizia risorse temporanee (se ZIP)
    if temp_dir:
        shutil.rmtree(temp_dir)
        print("🧹 Cartella temporanea eliminata.")

    print("=== FINE SCRIPT ===")

if __name__ == "__main__":
    main()