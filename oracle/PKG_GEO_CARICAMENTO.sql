CREATE OR REPLACE PACKAGE        PKG_GEO_CARICAMENTO IS

  -- Dichiaro il tipo record per l'uso pubblico se necessario (opzionale)
  TYPE t_rec IS RECORD (
    CODE                DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.CODE%TYPE,
    HEIGHT              DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.HEIGHT%TYPE,
    H_STDEV             DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.H_STDEV%TYPE,
    VEL                 DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.VEL%TYPE,
    V_STDEV             DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.V_STDEV%TYPE,
    COHE                DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.COHE%TYPE,
    GEOMETRY            DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.GEOMETRY%TYPE,
    ORIGINE_DATO        DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.ORIGINE_DATO%TYPE,
    DATI_VARIABILI      DWHADM.GEO_DATI_INTERFEROMETRICI_PRV.DATI_VARIABILI%TYPE,
    GEOM_OBJ            SDO_GEOMETRY
  );

  -- Dichiaro procedure e funzione
  PROCEDURE write_log(
      p_id_log       IN OUT DWHADM.GEO_CARICAMENTO_LOG.ID_LOG%TYPE,
      p_data_inizio  IN TIMESTAMP,
      p_data_fine    IN TIMESTAMP,
      p_file         IN VARCHAR2,
      p_letti       IN NUMBER,
      p_ok           IN NUMBER,
      p_ko           IN NUMBER,
      p_msg          IN VARCHAR2,
      p_is_new       IN BOOLEAN
  );

  PROCEDURE carica_file(p_file IN VARCHAR2);

  PROCEDURE carica_tutti_file;
  
  PROCEDURE aggiorna_geo_anagrafica_bbox;

  -- Dichiaro la funzione di conversione WKT->SDO_GEOMETRY
  FUNCTION F_GEO_SAFE_WKT_TO_GEOM(
      wkt        IN VARCHAR2,
      p_validate IN NUMBER DEFAULT 0
  ) RETURN SDO_GEOMETRY;

END PKG_GEO_CARICAMENTO;
/


CREATE OR REPLACE PACKAGE BODY        PKG_GEO_CARICAMENTO AS

 
  TYPE t_base IS TABLE OF t_rec;

  -- Log procedure autonoma (rimane invariata)
  PROCEDURE write_log(
      p_id_log       IN OUT DWHADM.GEO_CARICAMENTO_LOG.ID_LOG%TYPE,
      p_data_inizio  IN TIMESTAMP,
      p_data_fine    IN TIMESTAMP,
      p_file         IN VARCHAR2,
      p_letti       IN NUMBER,
      p_ok           IN NUMBER,
      p_ko           IN NUMBER,
      p_msg          IN VARCHAR2,
      p_is_new       IN BOOLEAN
  ) IS
    PRAGMA AUTONOMOUS_TRANSACTION;
  BEGIN
    IF p_is_new THEN
      INSERT INTO DWHADM.GEO_CARICAMENTO_LOG
          (ID_LOG, DATA_INIZIO, DATA_FINE, ORIGINE_DATO,
           RECORD_LETTI, RECORD_OK, RECORD_KO, MESSAGGIO)
      VALUES
          (DWHADM.ISEQ$$_267578.NEXTVAL,
           p_data_inizio, NULL, p_file,
           NULL, NULL, NULL, p_msg)
      RETURNING ID_LOG INTO p_id_log;
    ELSE
      UPDATE DWHADM.GEO_CARICAMENTO_LOG
         SET DATA_FINE    = p_data_fine,
             RECORD_LETTI = p_letti,
             RECORD_OK    = p_ok,
             RECORD_KO    = p_ko,
             MESSAGGIO    = p_msg
       WHERE ID_LOG = p_id_log;
    END IF;
    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      ROLLBACK; -- ignoro errori log
  END write_log;

  -- Procedura caricamento singolo file
  PROCEDURE carica_file(p_file IN VARCHAR2) IS

    CURSOR c_base IS
      SELECT CODE, HEIGHT, H_STDEV, VEL, V_STDEV, COHE,
             GEOMETRY, ORIGINE_DATO, DATI_VARIABILI,
             F_GEO_SAFE_WKT_TO_GEOM(GEOMETRY, 1) AS GEOM_OBJ
        FROM DWHADM.GEO_DATI_INTERFEROMETRICI_PRV
       WHERE ORIGINE_DATO = p_file
         AND DATI_VARIABILI IS NOT NULL;

    l_batch         t_base := t_base();
    l_batch_notnull t_base := t_base();
    l_batch_null    t_base := t_base();

    c_batch_size CONSTANT PLS_INTEGER := 10000;

    v_log_id   DWHADM.GEO_CARICAMENTO_LOG.ID_LOG%TYPE;
    v_start    TIMESTAMP;
    v_letti    NUMBER := 0;
    v_inseriti NUMBER := 0;
    v_scartati NUMBER := 0;
    v_msg      VARCHAR2(4000);

  BEGIN
    v_start := SYSTIMESTAMP;
    v_msg := 'Inizio caricamento file ' || p_file;
    write_log(v_log_id, v_start, NULL, p_file, NULL, NULL, NULL, v_msg, TRUE);

    OPEN c_base;
    LOOP
      FETCH c_base BULK COLLECT INTO l_batch LIMIT c_batch_size;
      EXIT WHEN l_batch.COUNT = 0;

      v_letti := v_letti + l_batch.COUNT;

      l_batch_notnull.DELETE;
      l_batch_null.DELETE;

      FOR i IN 1 .. l_batch.COUNT LOOP
        IF l_batch(i).GEOM_OBJ IS NOT NULL THEN
          l_batch_notnull.EXTEND;
          l_batch_notnull(l_batch_notnull.COUNT) := l_batch(i);
        ELSE
          l_batch_null.EXTEND;
          l_batch_null(l_batch_null.COUNT) := l_batch(i);
        END IF;
      END LOOP;

      IF l_batch_notnull.COUNT > 0 THEN
        FORALL i IN 1 .. l_batch_notnull.COUNT SAVE EXCEPTIONS
          INSERT /*+ APPEND */ INTO DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL
            (CODE, HEIGHT, H_STDEV, VEL, V_STDEV, COHE,
             GEOMETRY, ORIGINE_DATO, ANNO_CARICAMENTO, GEOM_SDO, DATI_VARIABILI)
          VALUES
            (l_batch_notnull(i).CODE,
             l_batch_notnull(i).HEIGHT,
             l_batch_notnull(i).H_STDEV,
             l_batch_notnull(i).VEL,
             l_batch_notnull(i).V_STDEV,
             l_batch_notnull(i).COHE,
             l_batch_notnull(i).GEOMETRY,
             l_batch_notnull(i).ORIGINE_DATO,
             TO_NUMBER(TO_CHAR(SYSDATE, 'YYYY')),
             l_batch_notnull(i).GEOM_OBJ,
             l_batch_notnull(i).DATI_VARIABILI);
        v_inseriti := v_inseriti + SQL%ROWCOUNT;
      END IF;

      IF l_batch_null.COUNT > 0 THEN
        FORALL i IN 1 .. l_batch_null.COUNT SAVE EXCEPTIONS
          INSERT /*+ APPEND */ INTO DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL_SCARTI
            (CODE, HEIGHT, H_STDEV, VEL, V_STDEV, COHE,
             GEOMETRY, ORIGINE_DATO, GEOM_SDO, ERRORE, DATI_VARIABILI)
          VALUES
            (l_batch_null(i).CODE,
             l_batch_null(i).HEIGHT,
             l_batch_null(i).H_STDEV,
             l_batch_null(i).VEL,
             l_batch_null(i).V_STDEV,
             l_batch_null(i).COHE,
             l_batch_null(i).GEOMETRY,
             l_batch_null(i).ORIGINE_DATO,
             NULL,
             'Geometria non convertibile o non valida',
             l_batch_null(i).DATI_VARIABILI);
        v_scartati := v_scartati + SQL%ROWCOUNT;
      END IF;

      COMMIT;

      v_msg := 'Caricamento in corso... ' || v_letti || ' letti, ' || v_inseriti || ' inseriti, ' || v_scartati || ' scartati.';
      write_log(v_log_id, v_start, SYSTIMESTAMP, p_file, v_letti, v_inseriti, v_scartati, v_msg, FALSE);
    END LOOP;

    CLOSE c_base;

    v_msg := 'Caricamento completato: ' || v_letti || ' record letti, ' || v_inseriti || ' inseriti, ' || v_scartati || ' scartati.';
    write_log(v_log_id, v_start, SYSTIMESTAMP, p_file, v_letti, v_inseriti, v_scartati, v_msg, FALSE);

  EXCEPTION
    WHEN OTHERS THEN
      v_msg := 'Errore durante caricamento file ' || p_file || ': ' || SQLERRM;
      write_log(v_log_id, v_start, SYSTIMESTAMP, p_file, v_letti, v_inseriti, v_scartati, v_msg, FALSE);
      ROLLBACK;
      RAISE;
  END carica_file;

  -- Procedura carica tutti file (uguale)
  PROCEDURE carica_tutti_file IS
    CURSOR c_file IS
      SELECT DISTINCT ORIGINE_DATO
        FROM DWHADM.GEO_DATI_INTERFEROMETRICI_PRV
       WHERE DATI_VARIABILI IS NOT NULL
         AND ORIGINE_DATO <> 'PSP_CSK_HI_05_HH_RA_20110518_20210929_TORBIDO_MORMANNO.shp'         
       ORDER BY ORIGINE_DATO;

  BEGIN
    FOR rec IN c_file LOOP
      carica_file(rec.ORIGINE_DATO);
    END LOOP;
  END carica_tutti_file;


 PROCEDURE aggiorna_geo_anagrafica_bbox IS
  CURSOR c_geo IS
    SELECT origine_dato
    FROM DWHADM.GEO_ANAGRAFICA
    WHERE min_lat IS NULL
      AND max_lat IS NULL
      AND min_lon IS NULL
      AND max_lon IS NULL;

  v_id_log       DWHADM.GEO_CARICAMENTO_LOG.ID_LOG%TYPE;
  v_data_inizio  TIMESTAMP;
  v_data_fine    TIMESTAMP;
  v_file         VARCHAR2(4000);
  v_letti        NUMBER := 0;
  v_ok           NUMBER := 0;
  v_ko           NUMBER := 0;
  v_msg          VARCHAR2(4000);
BEGIN
  FOR r IN c_geo LOOP
    v_letti := 1;
    v_file := r.origine_dato;
    v_data_inizio := SYSTIMESTAMP;

    -- Scrittura iniziale log
    PKG_GEO_CARICAMENTO.write_log(
      p_id_log      => v_id_log,
      p_data_inizio => v_data_inizio,
      p_data_fine   => NULL,
      p_file        => v_file,
      p_letti       => v_letti,
      p_ok          => 0,
      p_ko          => 0,
      p_msg         => 'Inizio aggiornamento bounding box',
      p_is_new      => TRUE
    );

    BEGIN
      MERGE INTO DWHADM.GEO_ANAGRAFICA a
      USING (
        SELECT
          t.origine_dato,
          MIN(SDO_GEOM.SDO_MIN_MBR_ORDINATE(t.geom_sdo, 2)) AS min_lat,
          MAX(SDO_GEOM.SDO_MAX_MBR_ORDINATE(t.geom_sdo, 2)) AS max_lat,
          MIN(SDO_GEOM.SDO_MIN_MBR_ORDINATE(t.geom_sdo, 1)) AS min_lon,
          MAX(SDO_GEOM.SDO_MAX_MBR_ORDINATE(t.geom_sdo, 1)) AS max_lon
        FROM DWHADM.GEO_DATI_INTERFEROMETRICI_SPATIAL t
        WHERE t.origine_dato = r.origine_dato
        GROUP BY t.origine_dato
      ) d
      ON (a.origine_dato = d.origine_dato)
      WHEN MATCHED THEN UPDATE SET
        a.min_lat = d.min_lat,
        a.max_lat = d.max_lat,
        a.min_lon = d.min_lon,
        a.max_lon = d.max_lon;

      COMMIT;
      v_ok := 1;
      v_msg := 'Aggiornamento OK';
    EXCEPTION
      WHEN OTHERS THEN
        v_ko := 1;
        v_msg := 'Errore: ' || SQLERRM;
    END;

    v_data_fine := SYSTIMESTAMP;

    -- Aggiornamento log
    PKG_GEO_CARICAMENTO.write_log(
      p_id_log      => v_id_log,
      p_data_inizio => v_data_inizio,
      p_data_fine   => v_data_fine,
      p_file        => v_file,
      p_letti       => v_letti,
      p_ok          => v_ok,
      p_ko          => v_ko,
      p_msg         => v_msg,
      p_is_new      => FALSE
    );
  END LOOP;
END aggiorna_geo_anagrafica_bbox;



FUNCTION F_GEO_SAFE_WKT_TO_GEOM(
    wkt IN VARCHAR2,
    p_validate IN NUMBER DEFAULT 0
) RETURN SDO_GEOMETRY IS
    v_geom SDO_GEOMETRY;
BEGIN
    BEGIN
        v_geom := SDO_UTIL.FROM_WKTGEOMETRY(wkt);
        v_geom.SDO_SRID := 4326;

        IF p_validate > 0 THEN
            IF v_geom IS NULL OR v_geom.SDO_GTYPE IS NULL THEN
                RETURN NULL;
            END IF;

            IF p_validate = 2 THEN
                IF SDO_GEOM.VALIDATE_GEOMETRY_WITH_CONTEXT(v_geom, 0.0001) <> 'TRUE' THEN
                    RETURN NULL;
                END IF;
            END IF;
        END IF;

        RETURN v_geom;

    EXCEPTION
        WHEN OTHERS THEN
            RETURN NULL;
    END;
END F_GEO_SAFE_WKT_TO_GEOM;




END PKG_GEO_CARICAMENTO;
/
