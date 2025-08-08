MERGE INTO DWHADM.GEO_ANAGRAFICA a
USING (
  SELECT
    t.origine_dato,
    MIN(SDO_GEOM.SDO_MIN_MBR_ORDINATE(t.geom_sdo, 2)) AS min_lat,
    MAX(SDO_GEOM.SDO_MAX_MBR_ORDINATE(t.geom_sdo, 2)) AS max_lat,
    MIN(SDO_GEOM.SDO_MIN_MBR_ORDINATE(t.geom_sdo, 1)) AS min_lon,
    MAX(SDO_GEOM.SDO_MAX_MBR_ORDINATE(t.geom_sdo, 1)) AS max_lon
  FROM dwhadm.geo_dati_interferometrici_spatial t
  WHERE t.origine_dato = 'PSP_CSK_HI_05_HH_RA_HI_03_HH_RD_20170303_20210929_RANTA_BALZATELLE_EW.shp'
  GROUP BY t.origine_dato
) d
ON (a.origine_dato = d.origine_dato)
WHEN MATCHED THEN UPDATE SET
  a.min_lat = d.min_lat,
  a.max_lat = d.max_lat,
  a.min_lon = d.min_lon,
  a.max_lon = d.max_lon;
