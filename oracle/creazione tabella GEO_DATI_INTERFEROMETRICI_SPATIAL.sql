-------------------------------------------------
-- CREAZIONE TABELLA GEO_DATI_INTERFEROMETRICI_SPATIAL
-- Versione con partizionamento INTERVAL per gestione automatica degli anni
-------------------------------------------------
CREATE TABLE DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL
(
    -- Identificativo del punto interferometrico
    CODE              VARCHAR2(50 BYTE),

    -- Altezza stimata del punto
    HEIGHT            NUMBER(38,2),

    -- Deviazione standard dell'altezza
    H_STDEV           NUMBER(38,2),

    -- Velocità del punto (es. subsidenza)
    VEL               NUMBER(38,2),

    -- Deviazione standard della velocità
    V_STDEV           NUMBER(38,2),

    -- Coerenza interferometrica (qualità del dato)
    COHE              NUMBER(38,2),

    -- Geometria in formato testuale (WKT o simile)
    GEOMETRY          VARCHAR2(128 BYTE),

    -- Origine o fonte del dato
    ORIGINE_DATO      VARCHAR2(1000 BYTE),

    -- Anno di caricamento nel DWH (usato per partizionamento)
    ANNO_CARICAMENTO  NUMBER(4) NOT NULL,

    -- Geometria spaziale Oracle (SDO_GEOMETRY)
    GEOM_SDO          MDSYS.SDO_GEOMETRY,

    -- Informazioni aggiuntive in formato JSON
    DATI_VARIABILI    CLOB,

    -- Vincolo per garantire che il CLOB contenga JSON valido
    CONSTRAINT CK_DATI_VARIABILI_JSON CHECK (DATI_VARIABILI IS JSON)
)
TABLESPACE DWHADM_DATA  -- Tablespace principale per la tabella

-- Configurazione storage per il campo CLOB DATI_VARIABILI
LOB (DATI_VARIABILI) STORE AS SECUREFILE (
    TABLESPACE DWHADM_DATA
    ENABLE STORAGE IN ROW       -- Se il dato è piccolo, lo memorizza direttamente nella riga
    CHUNK 8192                  -- Dimensione del blocco di storage
    NOCACHE                     -- Non mantiene i dati in cache (risparmio memoria)
    NOCOMPRESS                  -- Nessuna compressione (evita overhead CPU)
    KEEP_DUPLICATES             -- Mantiene duplicati binari
)

-- Configurazione storage per VARRAY SDO_ELEM_INFO (parte della geometria)
VARRAY "GEOM_SDO"."SDO_ELEM_INFO" STORE AS SECUREFILE LOB (
    TABLESPACE DWHADM_DATA
    ENABLE STORAGE IN ROW
    CHUNK 8192
    NOCACHE
    NOCOMPRESS
    KEEP_DUPLICATES
)

-- Configurazione storage per VARRAY SDO_ORDINATES (coordinate X,Y,Z)
VARRAY "GEOM_SDO"."SDO_ORDINATES" STORE AS SECUREFILE LOB (
    TABLESPACE DWHADM_DATA
    ENABLE STORAGE IN ROW
    CHUNK 8192
    NOCACHE
    NOCOMPRESS
    KEEP_DUPLICATES
)

-- Partizionamento automatico per anno di caricamento
PARTITION BY RANGE (ANNO_CARICAMENTO)
INTERVAL (1)  -- Crea automaticamente nuove partizioni per ogni anno
(
    -- Partizione iniziale per dati precedenti al 2023
    PARTITION P_LT_2023 VALUES LESS THAN (2023) TABLESPACE DWHADM_DATA
);

-------------------------------------------------
-- Impostazione valore di default per ANNO_CARICAMENTO
-- Inserisce automaticamente l'anno corrente se non specificato
-------------------------------------------------
ALTER TABLE DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL
  MODIFY ANNO_CARICAMENTO DEFAULT TO_NUMBER(TO_CHAR(SYSDATE,'YYYY'));

-------------------------------------------------
-- INDICI
-- Migliorano le performance delle query su campi chiave
-------------------------------------------------

-- Indice locale sul campo CODE (identificativo del dato)
CREATE INDEX DWHADM.GEO_DATI_INTERF_SP_CODE_IDX
  ON DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL (CODE)
  LOCAL
  TABLESPACE DWHADM_DATA;

-- Indice locale sul campo ORIGINE_DATO (testuale)
CREATE INDEX DWHADM.IDX_GEO_INTERF_ORIGINE_DATO
  ON DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL (ORIGINE_DATO)
  LOCAL
  TABLESPACE DWHADM_DATA;

-- Indice spaziale su GEOM_SDO per query geografiche
CREATE INDEX DWHADM.GEO_DATI_INTERF_SP_GIDX
  ON DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL (GEOM_SDO)
  INDEXTYPE IS MDSYS.SPATIAL_INDEX;
