Imports System.Collections.Generic
Imports System.Data
Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows
Imports System.Windows.Media.Imaging
Imports Microsoft.Web.WebView2.Core


Namespace DatiInterfOracleSpatial
    ''' <summary>
    ''' Finestra overlay GIS-like: carica solo bounds all'avvio, punti solo su richiesta e in base allo zoom.
    ''' </summary>
    Partial Public Class GeoDataOverlayWindow
        Inherits Window

#Region "Campi e proprietà"
        Private _isMapInitialized As Boolean = False
        Private _isOverlayActive As Boolean = False
        Private _debugWindow As TextBox
        Private _oracleHelper As OracleConnectionHelper
        Private _config As New MapConfiguration()
        Private _allLoadedBounds As New List(Of BoundingBox)
        Private _lastLoadedBounds As BoundingBox
        Private _currentPoints As New List(Of GeoPoint)()
        Private Const COLOR_SCALE_MIN As Double = -5
        Private Const COLOR_SCALE_MAX As Double = 5
        Private _isLoadingOverlay As Boolean = False

        ' Dizionario per label custom degli attributi
        Private ReadOnly _attributeLabels As New Dictionary(Of String, String) From {
            {"VEL", "Velocità media"},
            {"HEIGHT", "Quota (m s.l.m.)"},
            {"COHE", "Coerenza"}',            
        }

        ' Inserisci tra i campi privati della classe
        Private ReadOnly _visibleAttributes As New List(Of String) From {
               "HEIGHT", "H_STDEV", "VEL", "V_STDEV", "COHE" ' ...aggiungi qui solo i campi che vuoi mostrare
            }
#End Region

#Region "Inizializzazione"
        Public Sub New()
            InitializeComponent()
            CreaFinestraDebug()
            LogDebug("Inizializzazione finestra...")
            InitializeWebView()
            _oracleHelper = New OracleConnectionHelper()
            LogDebug("OracleConnectionHelper inizializzato")
        End Sub

        ' Carica i valori iniziali all'avvio
        Private Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
            AggiornaFiltri()
        End Sub
        Private Sub CreaFinestraDebug()
            Dim debugPanel As New DockPanel()
            DockPanel.SetDock(debugPanel, Dock.Bottom)
            debugPanel.Height = 150
            debugPanel.LastChildFill = True

            Dim header As New TextBlock()
            header.Text = "Debug Log"
            header.Background = System.Windows.Media.Brushes.LightGray
            header.Padding = New Thickness(5)
            DockPanel.SetDock(header, Dock.Top)
            debugPanel.Children.Add(header)

            _debugWindow = New TextBox()
            _debugWindow.IsReadOnly = True
            _debugWindow.VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            _debugWindow.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            _debugWindow.FontFamily = New System.Windows.Media.FontFamily("Consolas")
            _debugWindow.Background = System.Windows.Media.Brushes.Black
            _debugWindow.Foreground = System.Windows.Media.Brushes.LightGreen
            debugPanel.Children.Add(_debugWindow)

            Dim mainGrid = TryCast(Content, Grid)
            If mainGrid IsNot Nothing Then
                mainGrid.RowDefinitions.Add(New RowDefinition() With {.Height = New GridLength(150)})
                Grid.SetRow(debugPanel, 3)
                mainGrid.Children.Add(debugPanel)
            End If
        End Sub

        Private Async Sub InitializeWebView()
            LogDebug("Inizializzazione WebView...")
            Try
                Await WebView.EnsureCoreWebView2Async()
                LogDebug("WebView2 inizializzato correttamente")
                AddHandler WebView.CoreWebView2.NavigationCompleted, AddressOf CoreWebView2_NavigationCompleted

                Dim exePath As String = Process.GetCurrentProcess().MainModule.FileName
                Dim exeDir As String = Path.GetDirectoryName(exePath)
                LogDebug($"Directory esecuzione: {exeDir}")

                Dim htmlFile As String = Path.Combine(exeDir, "GeoDataMap.html")
                Dim jsFile As String = Path.Combine(exeDir, "GeoDataMap.js")

                If Not File.Exists(htmlFile) OrElse Not File.Exists(jsFile) Then
                    MessageBox.Show("File HTML/JS mancanti. Assicurati che siano presenti nella directory di output.", "File mancanti", MessageBoxButton.OK, MessageBoxImage.Error)
                    Return
                End If

                WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "local.resources", exeDir, CoreWebView2HostResourceAccessKind.Allow)
                WebView.CoreWebView2.Navigate("https://local.resources/GeoDataMap.html")
                AddHandler WebView.CoreWebView2.WebMessageReceived, AddressOf CoreWebView2_WebMessageReceived
            Catch ex As Exception
                LogDebug($"ERRORE nell'inizializzazione WebView: {ex.Message}")
                StatusText.Text = "Errore nell'inizializzazione WebView"
            End Try
        End Sub
#End Region

#Region "Gestione UI"
        Private Async Sub BtnCaricaDati_Click(sender As Object, e As RoutedEventArgs)
            Try
                StatusText.Text = "Caricamento bounds da Oracle Spatial..."
                LogDebug("Caricamento bounds filtrati")

                ' Costruisci i filtri
                Dim filtri As New Dictionary(Of String, List(Of String))
                If LstSt.SelectedItems.Count > 0 Then filtri("ST") = LstSt.SelectedItems.Cast(Of String).ToList()
                If LstStrada.SelectedItems.Count > 0 Then filtri("STRADA") = LstStrada.SelectedItems.Cast(Of String).ToList()
                If LstKmSito.SelectedItems.Count > 0 Then filtri("KM_SITO") = LstKmSito.SelectedItems.Cast(Of String).ToList()
                If LstSensore.SelectedItems.Count > 0 Then filtri("SENSORE") = LstSensore.SelectedItems.Cast(Of String).ToList()
                If LstPeriodo.SelectedItems.Count > 0 Then filtri("PERIODO") = LstPeriodo.SelectedItems.Cast(Of String).ToList()
                If LstTipo.SelectedItems.Count > 0 Then filtri("TIPO") = LstTipo.SelectedItems.Cast(Of String).ToList()
                If LstDirezione.SelectedItems.Count > 0 Then filtri("DIREZIONE") = LstDirezione.SelectedItems.Cast(Of String).ToList()

                ' Ottieni tutti gli ORIGINE_DATO filtrati
                Dim origini = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "ORIGINE_DATO", filtri)
                Await CaricaBoundsDaDB(origini)
                StatusText.Text = "Bounds caricati. Premi 'Crea Overlay' per visualizzare i punti."
            Catch ex As Exception
                LogDebug($"ERRORE durante il caricamento bounds: {ex.Message}")
                MessageBox.Show($"Errore durante il caricamento bounds: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
                StatusText.Text = "Errore nel caricamento bounds"
            End Try
        End Sub

        Private Async Sub BtnCreaOverlay_Click(sender As Object, e As RoutedEventArgs)
            If _allLoadedBounds Is Nothing OrElse _allLoadedBounds.Count = 0 Then
                MessageBox.Show("Caricare prima i bounds.", "Bounds mancanti", MessageBoxButton.OK, MessageBoxImage.Warning)
                Return
            End If
            If Not _isMapInitialized Then
                MessageBox.Show("La mappa non è pronta.", "Mappa non pronta", MessageBoxButton.OK, MessageBoxImage.Warning)
                Return
            End If

            Try
                Dim zoomLevelScript = Await WebView.CoreWebView2.ExecuteScriptAsync("map.getZoom()")
                Dim boundsScript = Await WebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(map.getBounds().toJSON())")
                Dim zoomLevel As Double = _config.DefaultZoomLevel
                If Not String.IsNullOrEmpty(zoomLevelScript) AndAlso zoomLevelScript <> "null" Then
                    zoomLevel = Double.Parse(zoomLevelScript, System.Globalization.CultureInfo.InvariantCulture)
                End If

                Dim viewportBox As BoundingBox = Nothing
                If boundsScript.StartsWith("""") AndAlso boundsScript.EndsWith("""") Then
                    boundsScript = boundsScript.Substring(1, boundsScript.Length - 2)
                    boundsScript = boundsScript.Replace("\""", """") ' Rimuove escape
                End If
                If Not String.IsNullOrEmpty(boundsScript) AndAlso boundsScript <> "null" Then
                    Dim jsBounds = JsonSerializer.Deserialize(Of JsBounds)(boundsScript)
                    viewportBox = New BoundingBox With {
        .North = jsBounds.north,
        .South = jsBounds.south,
        .East = jsBounds.east,
        .West = jsBounds.west
    }
                End If

                Dim puntiTotali As New List(Of GeoPoint)
                For Each bound In _allLoadedBounds
                    Dim intersezione = IntersectBounds(bound, viewportBox)
                    If intersezione IsNot Nothing Then
                        _lastLoadedBounds = intersezione
                        Dim punti = Await CaricaPuntiDaDB(intersezione, zoomLevel)
                        puntiTotali.AddRange(punti)
                    End If
                Next
                _lastLoadedBounds = CalcolaBounds(puntiTotali)
                Await RenderPointsAsync(puntiTotali, zoomLevel)
                MostraLegendaColorbar(COLOR_SCALE_MIN, COLOR_SCALE_MAX)
                StatusText.Text = $"Visualizzati {puntiTotali.Count} punti (zoom {zoomLevel:F1})"
            Catch ex As Exception
                LogDebug($"ERRORE durante la creazione overlay: {ex.Message}")
                StatusText.Text = "Errore nella creazione overlay"
            End Try
        End Sub
        Private Async Sub BtnRimuoviOverlay_Click(sender As Object, e As RoutedEventArgs)
            Try
                LogDebug("Richiesta rimozione overlay")
                Await WebView.CoreWebView2.ExecuteScriptAsync("removeWebGLOverlay(); removeGroundOverlays();")
                _isOverlayActive = False
                StatusText.Text = "Overlay rimosso"
                LegendBorder.Visibility = Visibility.Hidden
            Catch ex As Exception
                LogDebug($"ERRORE durante la rimozione overlay: {ex.Message}")
            End Try
        End Sub
#End Region

#Region "Caricamento bounds"
        ''' <summary>
        ''' Carica solo i bounds globali dal database e li visualizza come rettangolo.
        ''' </summary>
        Private Async Function CaricaBoundsDaDB(origini As List(Of String)) As Task
            'pulisce la mappa prima del nuovo caricamento
            Await WebView.CoreWebView2.ExecuteScriptAsync("removeWebGLOverlay(); removeGroundOverlays(); clearDeckBounds && clearDeckBounds();")
            Try
                If origini Is Nothing OrElse origini.Count = 0 Then
                    LogDebug("Nessun ORIGINE_DATO selezionato.")
                    Return
                End If
                Dim inClause = String.Join(",", origini.Select(Function(o) $"'{o.Replace("'", "''")}'"))
                'Dim query As String = $"
                '                        SELECT
                '                          ORIGINE_DATO,
                '                          MIN(SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.005).SDO_POINT.Y) AS MIN_LAT,
                '                          MAX(SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.005).SDO_POINT.Y) AS MAX_LAT,
                '                          MIN(SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.005).SDO_POINT.X) AS MIN_LON,
                '                          MAX(SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.005).SDO_POINT.X) AS MAX_LON
                '                        FROM GEO_DATI_INTERFEROMETRICI_SPATIAL
                '                        WHERE ORIGINE_DATO IN ({inClause})
                '                        GROUP BY ORIGINE_DATO
                '                    "
                Dim query As String = $"
                                        SELECT
                                          ORIGINE_DATO,
                                           MIN_LAT,
                                           MAX_LAT,
                                           MIN_LON,
                                           MAX_LON
                                        FROM GEO_ANAGRAFICA
                                        WHERE ORIGINE_DATO IN ({inClause})                        
                                    "

                Dim dt = _oracleHelper.ExecuteQuery(query)
                _allLoadedBounds.Clear()
                Dim boundsList As New List(Of Object)
                If dt.Rows.Count > 0 Then
                    For Each row As DataRow In dt.Rows
                        Dim bounds As New BoundingBox With {
                    .South = Convert.ToDouble(row("MIN_LAT"), Globalization.CultureInfo.InvariantCulture),
                    .North = Convert.ToDouble(row("MAX_LAT"), Globalization.CultureInfo.InvariantCulture),
                    .West = Convert.ToDouble(row("MIN_LON"), Globalization.CultureInfo.InvariantCulture),
                    .East = Convert.ToDouble(row("MAX_LON"), Globalization.CultureInfo.InvariantCulture)
                }
                        _allLoadedBounds.Add(bounds)
                        Dim origine As String = row("ORIGINE_DATO").ToString()
                        boundsList.Add(New With {
                    .north = bounds.North,
                    .south = bounds.South,
                    .east = bounds.East,
                    .west = bounds.West,
                    .origine = origine
                })
                    Next
                    Dim boundsArrayJson = JsonSerializer.Serialize(boundsList)
                    Await WebView.CoreWebView2.ExecuteScriptAsync($"drawDeckBounds({boundsArrayJson});")
                    LogDebug("Bounds filtrati caricati e visualizzati.")
                Else
                    LogDebug("Nessun bounds trovato.")
                End If
            Catch ex As Exception
                LogDebug($"ERRORE caricamento bounds: {ex.Message}")
            End Try
        End Function

#End Region

#Region "Overlay e visualizzazione"
        ''' <summary>
        ''' Carica e visualizza solo i punti visibili e campionati in base allo zoom.
        ''' </summary>

        Private Async Function UpdateMapOverlayAsync(bounds As BoundingBox, zoomLevel As Double) As Task
            If _isLoadingOverlay Then
                LogDebug("UpdateMapOverlayAsync: caricamento già in corso, richiesta ignorata.")
                Return
            End If
            _isLoadingOverlay = True

            Dim hideSpinnerNeeded As Boolean = False

            Try
                ' Nascondi sempre lo spinner prima di mostrarlo (evita blocchi residui)
                Await WebView.CoreWebView2.ExecuteScriptAsync("hideMapLoadingSpinner();")
                LogDebug("Chiamo showMapLoadingSpinner da .NET")
                Await WebView.CoreWebView2.ExecuteScriptAsync("showMapLoadingSpinner();")
                'Await Task.Delay(1000) ' <-- solo per test, rimuovi in produzione!
                hideSpinnerNeeded = True

                LogDebug($"Update overlay: bounds N={bounds.North}, S={bounds.South}, E={bounds.East}, W={bounds.West}, zoom={zoomLevel}")
                Dim punti = Await CaricaPuntiDaDB(bounds, zoomLevel)
                _currentPoints = punti

                If punti.Count = 0 Then
                    StatusText.Text = "Nessun punto da visualizzare nell'area corrente"
                    LogDebug("Nessun punto da visualizzare")
                    Return
                End If

                Dim sogliaRaster = 10000
                If punti.Count > sogliaRaster Then
                    LogDebug("Visualizzazione raster base64 (overlay)")
                    Dim imgBase64 = GeneraImmagineBase64(punti, bounds, COLOR_SCALE_MIN, COLOR_SCALE_MAX)
                    Await WebView.CoreWebView2.ExecuteScriptAsync($"addGroundOverlay('{imgBase64}', {{
                north: {bounds.North.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                south: {bounds.South.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                east: {bounds.East.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                west: {bounds.West.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            }});")
                Else
                    Await RenderPointsAsync(punti, zoomLevel)
                End If

                _isOverlayActive = True
                StatusText.Text = $"Visualizzati {punti.Count} punti (zoom {zoomLevel:F1})"
            Catch ex As Exception
                _isOverlayActive = False
                LogDebug($"ERRORE update overlay: {ex.Message}")
                StatusText.Text = "Errore overlay"
            Finally
                _isLoadingOverlay = False
            End Try

            ' Nascondi lo spinner JS sulla mappa (fuori dal Try...Finally)
            If hideSpinnerNeeded Then
                Try
                    Await WebView.CoreWebView2.ExecuteScriptAsync("hideMapLoadingSpinner();")
                Catch exHide As Exception
                    LogDebug($"ERRORE nel nascondere lo spinner: {exHide.Message}")
                End Try
            End If
        End Function

        ''' <summary>
        ''' Query Oracle per caricare solo i punti visibili e campionati.
        ''' Gestione robusta di errori Oracle (incluso ORA-01013) e dati non validi.
        ''' </summary>
        ''' <summary>
        ''' Carica i punti visibili dal database Oracle, gestendo errori Oracle e dati non validi.
        ''' </summary>
        Private Async Function CaricaPuntiDaDB(bounds As BoundingBox, zoomLevel As Double) As Task(Of List(Of GeoPoint))
            Return Await Task.Run(Function()
                                      Dim result As New List(Of GeoPoint)()
                                      Try
                                          ' Determina il massimo numero di punti da caricare in base allo zoom
                                          Dim maxPunti = DeterminaMaxPuntiPerZoom(zoomLevel)
                                          Dim query As String
                                          Dim samplePercentNullable = _config.GetSamplePercent(zoomLevel)

                                          ' Costruzione query: sample solo se richiesto dallo zoom
                                          If Not samplePercentNullable.HasValue OrElse zoomLevel >= 17 Then
                                              query = $"
                                                        SELECT /*+ PARALLEL(4) */
                                                            CODE, VEL,
                                                            CENTROIDE_X AS LONGITUDE,
                                                            CENTROIDE_Y AS LATITUDE
                                                        FROM (
                                                            SELECT CODE, VEL,
                                                                   SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.X AS CENTROIDE_X,
                                                                   SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.Y AS CENTROIDE_Y,
                                                                   DATI_VARIABILI, GEOM_SDO
                                                            FROM GEO_DATI_INTERFEROMETRICI_SPATIAL
                                                            WHERE DATI_VARIABILI IS NOT NULL
                                                              AND DBMS_LOB.GETLENGTH(DATI_VARIABILI) > 0
                                                              AND SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.X IS NOT NULL
                                                              AND SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.Y IS NOT NULL
                                                        ) t
                                                        WHERE SDO_RELATE(
                                                            t.GEOM_SDO,
                                                            SDO_GEOMETRY(2003, 4326, NULL,
                                                                SDO_ELEM_INFO_ARRAY(1,1003,3),
                                                                SDO_ORDINATE_ARRAY(
                                                                    {bounds.West.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                                                                    {bounds.South.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                                                                    {bounds.East.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                                                                    {bounds.North.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                                                                )
                                                            ),
                                                            'mask=ANYINTERACT querytype=WINDOW'
                                                        ) = 'TRUE'
                                                        AND ROWNUM <= {maxPunti}
                                                        "
                                          Else
                                              Dim samplePercent = samplePercentNullable.Value
                                              query = $"
                                                        SELECT /*+ PARALLEL(4) */
                                                            CODE, VEL,
                                                            CENTROIDE_X AS LONGITUDE,
                                                            CENTROIDE_Y AS LATITUDE
                                                        FROM (
                                                            SELECT CODE, VEL,
                                                                   SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.X AS CENTROIDE_X,
                                                                   SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.Y AS CENTROIDE_Y,
                                                                   GEOM_SDO
                                                            FROM GEO_DATI_INTERFEROMETRICI_SPATIAL SAMPLE({samplePercent})
                                                            WHERE DATI_VARIABILI IS NOT NULL
                                                              AND DBMS_LOB.GETLENGTH(DATI_VARIABILI) > 0
                                                              AND SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.X IS NOT NULL
                                                              AND SDO_GEOM.SDO_CENTROID(GEOM_SDO, 0.001).SDO_POINT.Y IS NOT NULL
                                                        ) t
                                                        WHERE SDO_RELATE(
                                                            t.GEOM_SDO,
                                                            SDO_GEOMETRY(2003, 4326, NULL,
                                                                SDO_ELEM_INFO_ARRAY(1,1003,3),
                                                                SDO_ORDINATE_ARRAY(
                                                                    {bounds.West.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                                                                    {bounds.South.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                                                                    {bounds.East.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                                                                    {bounds.North.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                                                                )
                                                            ),
                                                            'mask=ANYINTERACT querytype=WINDOW'
                                                        ) = 'TRUE'
                                                        AND ROWNUM <= {maxPunti}
                                                        "
                                          End If

                                          LogDebug("Query punti: " & query)

                                          Dim dt As DataTable = Nothing
                                          Try
                                              ' Esecuzione query con gestione specifica per ORA-01013 (query annullata)
                                              dt = _oracleHelper.ExecuteQuery(query)
                                          Catch ex As Oracle.ManagedDataAccess.Client.OracleException
                                              If ex.Number = 1013 Then
                                                  LogDebug("Query annullata dall'utente o timeout Oracle (ORA-01013). Nessun dato caricato.")
                                                  Return result ' Lista vuota, non propagare errore
                                              Else
                                                  LogDebug($"ERRORE Oracle: {ex.Message}")
                                                  Throw
                                              End If
                                          End Try

                                          If dt Is Nothing Then Return result

                                          LogDebug("Righe restituite dalla query: " & dt.Rows.Count)

                                          Dim count As Integer = 0
                                          For Each row As DataRow In dt.Rows
                                              Try
                                                  ' Conversione sicura dei dati con controllo su DBNull e TryParse
                                                  Dim code As String = If(row("CODE") IsNot DBNull.Value, row("CODE").ToString(), "")
                                                  Dim vel As Double = 0
                                                  Dim lon As Double = 0
                                                  Dim lat As Double = 0

                                                  vel = ParseDoubleFlexible(row("VEL"))
                                                  lon = ParseDoubleFlexible(row("LONGITUDE"))
                                                  lat = ParseDoubleFlexible(row("LATITUDE"))
                                                  Dim velOk = Not Double.IsNaN(vel)
                                                  Dim lonOk = Not Double.IsNaN(lon)
                                                  Dim latOk = Not Double.IsNaN(lat)

                                                  ' Log dei primi 10 record per debug rapido (con valori convertiti)
                                                  If count < 10 Then
                                                      LogDebug($"[DEBUG DATI] CODE={code}, VEL={vel}, LONG={lon}, LAT={lat}")
                                                      count += 1
                                                  End If

                                                  If velOk AndAlso lonOk AndAlso latOk Then
                                                      ' Escludi valori non validi (NaN, Infinity)
                                                      If Not (Double.IsNaN(vel) OrElse Double.IsInfinity(vel) OrElse
                                                            Double.IsNaN(lon) OrElse Double.IsInfinity(lon) OrElse
                                                            Double.IsNaN(lat) OrElse Double.IsInfinity(lat)) Then

                                                          Dim p As New GeoPoint With {
                                                            .Id = code,
                                                            .Value = vel,
                                                            .Longitude = lon,
                                                            .Latitude = lat
                                                        }
                                                          result.Add(p)
                                                      Else
                                                          LogDebug($"Punto scartato per valore non valido: CODE={code}, VEL={row("VEL")}, LONG={row("LONGITUDE")}, LAT={row("LATITUDE")}")
                                                      End If
                                                  Else
                                                      LogDebug($"Punto scartato per conversione fallita: CODE={code}, VEL={row("VEL")}, LONG={row("LONGITUDE")}, LAT={row("LATITUDE")}")
                                                  End If

                                                  ' Log dei primi 10 record per debug rapido
                                                  If count < 10 Then
                                                      LogDebug($"[DEBUG DATI] CODE={code}, VEL={row("VEL")}, LONG={row("LONGITUDE")}, LAT={row("LATITUDE")}")
                                                      count += 1
                                                  End If
                                              Catch ex As Exception
                                                  LogDebug($"ERRORE conversione punto: CODE={row("CODE")}, VEL={row("VEL")}, LONG={row("LONGITUDE")}, LAT={row("LATITUDE")}, Errore: {ex.Message}")
                                              End Try
                                          Next
                                      Catch ex As Exception
                                          LogDebug($"ERRORE query punti: {ex.Message}")
                                      End Try
                                      Return result
                                  End Function)
        End Function

        Private Async Function RenderPointsAsync(points As List(Of GeoPoint), zoomLevel As Double) As Task
            Try
                If points Is Nothing OrElse points.Count = 0 Then
                    LogDebug("Nessun punto da visualizzare, salta RenderPointsAsync.")
                    Return
                End If
                Dim puntiJSON = PreparePointsJson(points)
                Dim markerRadius As Double = CalcolaGrandezzaMarker(zoomLevel)
                Dim boundsJson = JsonSerializer.Serialize(New With {
            .north = _lastLoadedBounds.North,
            .south = _lastLoadedBounds.South,
            .east = _lastLoadedBounds.East,
            .west = _lastLoadedBounds.West
        })
                Dim script = $"ensureDeckReady().then(() => createWebGLOverlay('{puntiJSON}', {markerRadius}, {zoomLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {boundsJson}));"
                Await WebView.CoreWebView2.ExecuteScriptAsync(script)
            Catch ex As Exception
                LogDebug($"ERRORE render points: {ex.Message}")
            End Try
        End Function
        'seralizzo solo i punti validi
        Private Function PreparePointsJson(points As List(Of GeoPoint)) As String
            Dim validPoints = points.Where(Function(p) _
        Not (Double.IsNaN(p.Longitude) OrElse Double.IsInfinity(p.Longitude) _
        OrElse Double.IsNaN(p.Latitude) OrElse Double.IsInfinity(p.Latitude) _
        OrElse Double.IsNaN(p.Value) OrElse Double.IsInfinity(p.Value)) _
        AndAlso p.Longitude >= -180 AndAlso p.Longitude <= 180 _
        AndAlso p.Latitude >= -90 AndAlso p.Latitude <= 90
    ).Select(Function(p)
                 Dim lon = If(Double.IsInfinity(p.Longitude) OrElse Double.IsNaN(p.Longitude), 0, p.Longitude)
                 Dim lat = If(Double.IsInfinity(p.Latitude) OrElse Double.IsNaN(p.Latitude), 0, p.Latitude)
                 Dim val = If(Double.IsInfinity(p.Value) OrElse Double.IsNaN(p.Value), 0, p.Value)
                 Dim colore = ColorUtils.CalcolaColoreRainbow(val, COLOR_SCALE_MIN, COLOR_SCALE_MAX)
                 Return New Object() {
            lon,
            lat,
            val,
            New Integer() {colore.R, colore.G, colore.B, colore.Alpha}
        }
             End Function).ToList()
            LogDebug($"[DEBUG] Punti validi serializzati: {validPoints.Count}")
            If validPoints.Count = 0 Then Return "[]"
            Return JsonSerializer.Serialize(validPoints)
        End Function

        Private Function GeneraImmagineBase64(punti As List(Of GeoPoint), bounds As BoundingBox, minValue As Double, maxValue As Double) As String
            Dim width As Integer = 1024
            Dim height As Integer = 1024

            If bounds.East = bounds.West OrElse bounds.North = bounds.South Then
                LogDebug("ERRORE: bounds non validi per rasterizzazione (East=West o North=South)")
                Return ""
            End If

            Dim bmp As New Bitmap(width, height)
            Using g As Graphics = Graphics.FromImage(bmp)
                g.Clear(Color.Transparent)
                For Each punto In punti
                    If punto.Longitude >= bounds.West AndAlso punto.Longitude <= bounds.East AndAlso
               punto.Latitude >= bounds.South AndAlso punto.Latitude <= bounds.North Then

                        Dim x = CInt(Math.Round((punto.Longitude - bounds.West) / (bounds.East - bounds.West) * width))
                        Dim y = CInt(Math.Round((bounds.North - punto.Latitude) / (bounds.North - bounds.South) * height))
                        Dim colore = ColorUtils.CalcolaColoreRainbow(punto.Value, minValue, maxValue)
                        Dim c As Color = Color.FromArgb(colore.Alpha, colore.R, colore.G, colore.B)
                        If x >= 0 AndAlso x < width AndAlso y >= 0 AndAlso y < height Then
                            bmp.SetPixel(x, y, c)
                        End If
                    End If
                Next
            End Using
            Using ms As New MemoryStream()
                bmp.Save(ms, Imaging.ImageFormat.Png)
                Dim base64 = Convert.ToBase64String(ms.ToArray())
                Return $"data:image/png;base64,{base64}"
            End Using
        End Function
#End Region

#Region "Utilità e supporto"
        Private Sub LogDebug(message As String)
            Dim timestampedMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}"
            Debug.WriteLine(timestampedMessage)
            If _debugWindow IsNot Nothing Then
                Dispatcher.Invoke(Sub()
                                      _debugWindow.AppendText(timestampedMessage & Environment.NewLine)
                                      _debugWindow.ScrollToEnd()
                                  End Sub)
            End If
        End Sub

        Private Function GetKeysAsStringList(dict As Dictionary(Of String, Object)) As List(Of String)
            Dim keys As New List(Of String)
            For Each k As String In dict.Keys
                keys.Add(k)
            Next
            Return keys
        End Function

        Private Function CalcolaBounds(punti As List(Of GeoPoint)) As BoundingBox
            Dim minLat As Double = Double.MaxValue
            Dim maxLat As Double = Double.MinValue
            Dim minLng As Double = Double.MaxValue
            Dim maxLng As Double = Double.MinValue
            For Each punto In punti
                minLat = Math.Min(minLat, punto.Latitude)
                maxLat = Math.Max(maxLat, punto.Latitude)
                minLng = Math.Min(minLng, punto.Longitude)
                maxLng = Math.Max(maxLng, punto.Longitude)
            Next
            Dim latMargin As Double = (maxLat - minLat) * 0.05
            Dim lngMargin As Double = (maxLng - minLng) * 0.05
            Return New BoundingBox With {
                .North = maxLat + latMargin,
                .South = minLat - latMargin,
                .East = maxLng + lngMargin,
                .West = minLng - lngMargin
            }
        End Function

        Private Function CalcolaGrandezzaMarker(zoomLevel As Double) As Double
            If zoomLevel >= 17 Then
                Return 3.0
            ElseIf zoomLevel >= 15 Then
                Return 3.5
            ElseIf zoomLevel >= 12 Then
                Return 3.5
            ElseIf zoomLevel >= 9 Then
                Return 4.0
            ElseIf zoomLevel >= 7 Then
                Return 4.5
            Else
                Return 3.0
            End If
        End Function

        Private Function DeterminaMaxPuntiPerZoom(zoomLevel As Double) As Integer
            If zoomLevel >= _config.HighZoomThreshold Then
                Return _config.MaxPointsAtHighZoom
            ElseIf zoomLevel >= _config.MediumZoomThreshold Then
                Return _config.MaxPointsAtMediumZoom
            Else
                Return _config.MaxPointsAtLowZoom
            End If
        End Function

        Private Sub MostraLegendaColorbar(minValue As Double, maxValue As Double)
            Dim width As Integer = 180
            Dim height As Integer = 20
            Dim bmp As New Bitmap(width, height)
            For x = 0 To width - 1
                Dim valore = minValue + (maxValue - minValue) * x / (width - 1)
                Dim colore = ColorUtils.CalcolaColoreRainbow(valore, minValue, maxValue)
                Dim c As System.Drawing.Color = System.Drawing.Color.FromArgb(colore.Alpha, colore.R, colore.G, colore.B)
                For y = 0 To height - 1
                    bmp.SetPixel(x, y, c)
                Next
            Next
            Using ms As New MemoryStream()
                bmp.Save(ms, Imaging.ImageFormat.Png)
                ms.Position = 0
                Dim img As New BitmapImage()
                img.BeginInit()
                img.CacheOption = BitmapCacheOption.OnLoad
                img.StreamSource = ms
                img.EndInit()
                img.Freeze()
                LegendImage.Source = img
            End Using
            LegendMinLabel.Text = minValue.ToString("0")
            LegendMaxLabel.Text = maxValue.ToString("+0;-0")
            LegendBorder.Visibility = Visibility.Visible
        End Sub

        ''' <summary>
        ''' Trova il punto più vicino alle coordinate del click e mostra i suoi attributi
        ''' </summary>
        Private Sub TrovaEMostraPuntoVicino(latitude As Double, longitude As Double)
            If _currentPoints Is Nothing OrElse _currentPoints.Count = 0 Then
                LogDebug("Nessun dato disponibile per la ricerca")
                MessageBox.Show("Nessun dato caricato. Caricare prima i dati.", "Dati mancanti", MessageBoxButton.OK, MessageBoxImage.Warning)
                Return
            End If

            If _lastLoadedBounds Is Nothing Then
                LogDebug("Bounds non disponibili per il controllo")
                Return
            End If

            If latitude < _lastLoadedBounds.South OrElse latitude > _lastLoadedBounds.North OrElse
               longitude < _lastLoadedBounds.West OrElse longitude > _lastLoadedBounds.East Then
                LogDebug($"Click fuori dai bounds: Lat={latitude}, Lon={longitude}")
                LogDebug($"Bounds: N={_lastLoadedBounds.North}, S={_lastLoadedBounds.South}, E={_lastLoadedBounds.East}, W={_lastLoadedBounds.West}")
                Return
            End If

            LogDebug($"Click dentro i bounds, cerco il marker più vicino")

            ' Usa il quadtree per trovare i punti nel bound e poi il più vicino
            Dim visibili = _currentPoints
            Dim puntiConDistanza = visibili.Select(Function(p) New With {
                .Punto = p,
                .Distanza = CalcolaDistanzaGeo(latitude, longitude, p.Latitude, p.Longitude)
            }).OrderBy(Function(p) p.Distanza).ToList()

            If puntiConDistanza.Count > 0 Then
                Dim puntoVicino = puntiConDistanza.First()
                LogDebug($"Marker più vicino trovato: ID={puntoVicino.Punto.Id}, Distanza={puntoVicino.Distanza * 1000:F1} metri")

                If Not String.IsNullOrEmpty(puntoVicino.Punto.Id) AndAlso puntoVicino.Punto.Attributes.Count = 0 Then
                    LogDebug($"Caricamento dettagli per punto ID: {puntoVicino.Punto.Id}")
                    puntoVicino.Punto.Attributes = CaricaDettagliPunto(puntoVicino.Punto.Id)
                End If

                MostraPopupInfoPunto(New List(Of Object) From {puntoVicino})
            Else
                LogDebug("Nessun marker disponibile nel bound")
            End If
        End Sub

        Private Function CalcolaDistanzaGeo(lat1 As Double, lon1 As Double, lat2 As Double, lon2 As Double) As Double
            Const RaggioTerrestre As Double = 6371 ' km
            Dim lat1Rad = lat1 * Math.PI / 180
            Dim lon1Rad = lon1 * Math.PI / 180
            Dim lat2Rad = lat2 * Math.PI / 180
            Dim lon2Rad = lon2 * Math.PI / 180
            Dim dLat = lat2Rad - lat1Rad
            Dim dLon = lon2Rad - lon1Rad
            Dim a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2)
            Dim c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a))
            Return RaggioTerrestre * c
        End Function

        Private Sub MostraPopupInfoPunto(puntiVicini As IEnumerable(Of Object))
            Try
                LogDebug("Inizio visualizzazione dettagli punto")

                Dim existingPopup = TryCast(FindName("InfoPopup"), System.Windows.Controls.Primitives.Popup)
                If existingPopup IsNot Nothing Then
                    existingPopup.IsOpen = False
                    UnregisterName("InfoPopup")
                    UnregisterName("InfoPanel")
                End If

                Dim popup As New System.Windows.Controls.Primitives.Popup With {
            .Name = "InfoPopup",
            .IsOpen = False,
            .StaysOpen = True,
            .AllowsTransparency = True,
            .PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
            .Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute
        }

                Dim border As New Border With {
            .BorderBrush = New SolidColorBrush(System.Windows.Media.Colors.Gray),
            .BorderThickness = New Thickness(1),
            .Background = New SolidColorBrush(System.Windows.Media.Colors.WhiteSmoke),
            .CornerRadius = New CornerRadius(4),
            .Padding = New Thickness(0),
            .MaxHeight = 450,
            .Width = 350,
            .HorizontalAlignment = HorizontalAlignment.Right,
            .VerticalAlignment = VerticalAlignment.Top,
            .Effect = New System.Windows.Media.Effects.DropShadowEffect With {
                .BlurRadius = 8,
                .ShadowDepth = 3,
                .Opacity = 0.3
            }
        }

                Dim contentGrid As New Grid()
                contentGrid.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
                contentGrid.RowDefinitions.Add(New RowDefinition With {.Height = New GridLength(1, GridUnitType.Star)})

                Dim headerPanel As New Grid With {
            .Background = New SolidColorBrush(System.Windows.Media.Colors.LightGray)
        }

                Dim titleTextBlock As New TextBlock With {
            .Text = "Informazioni Punto",
            .FontWeight = FontWeights.Bold,
            .Padding = New Thickness(10, 8, 10, 8),
            .VerticalAlignment = VerticalAlignment.Center
        }
                headerPanel.Children.Add(titleTextBlock)

                Dim closeButton As New Button With {
            .Content = "✕",
            .Width = 30,
            .Background = New SolidColorBrush(System.Windows.Media.Colors.Transparent),
            .BorderThickness = New Thickness(0),
            .HorizontalAlignment = HorizontalAlignment.Right,
            .Cursor = Cursors.Hand
        }
                AddHandler closeButton.Click, Sub(sender, e) popup.IsOpen = False
                headerPanel.Children.Add(closeButton)
                Grid.SetRow(headerPanel, 0)
                contentGrid.Children.Add(headerPanel)

                Dim scrollViewer As New ScrollViewer With {
            .VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            .HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            .Margin = New Thickness(0)
        }

                Dim infoPanel As New StackPanel With {
            .Name = "InfoPanel",
            .Margin = New Thickness(10)
        }

                If puntiVicini.Count > 0 Then
                    Dim punto = puntiVicini.First().Punto
                    Dim distanza = puntiVicini.First().Distanza

                    infoPanel.Children.Add(CreateHeaderTextBlock("CODE:", System.Windows.Media.Colors.DarkBlue))
                    infoPanel.Children.Add(CreateValueTextBlock($"{punto.Id}"))

                    infoPanel.Children.Add(CreateHeaderTextBlock("Posizione (WGS84):", System.Windows.Media.Colors.DarkBlue))
                    infoPanel.Children.Add(CreateValueTextBlock(
                    $"Lat:{punto.Latitude:F6}(°); Long:{punto.Longitude:F6}(°)"
            ))

                    infoPanel.Children.Add(CreateHeaderTextBlock("Valore VEL:", System.Windows.Media.Colors.DarkBlue))
                    infoPanel.Children.Add(CreateValueTextBlock($"{punto.Value:F3}"))

                    If punto.Attributes IsNot Nothing AndAlso punto.Attributes.Count > 0 Then
                        infoPanel.Children.Add(CreateHeaderTextBlock("Attributi:", System.Windows.Media.Colors.DarkBlue))

                        Dim attributeGrid As New Grid()
                        attributeGrid.Margin = New Thickness(0, 5, 0, 5)
                        attributeGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = GridLength.Auto})
                        attributeGrid.ColumnDefinitions.Add(New ColumnDefinition With {.Width = New GridLength(1, GridUnitType.Star)})

                        Dim row As Integer = 0
                        'LogDebug("Chiavi attributi disponibili: " & String.Join(",", GetKeysAsStringList(punto.Attributes)))
                        'LogDebug("Attributi richiesti: " & String.Join(",", _visibleAttributes))
                        For Each key In _visibleAttributes
                            If punto.Attributes.ContainsKey(key) Then
                                'LogDebug("Valore " & key & ": " & Convert.ToString(punto.Attributes(key)))
                                attributeGrid.RowDefinitions.Add(New RowDefinition With {.Height = GridLength.Auto})
                                Dim value = punto.Attributes(key)
                                Dim label As String = If(_attributeLabels.ContainsKey(key), _attributeLabels(key), key)

                                Dim keyBlock = New TextBlock With {
                                                                    .Text = label & ":",
                                                                    .FontWeight = FontWeights.Bold,
                                                                    .Margin = New Thickness(0, 2, 5, 2),
                                                                    .VerticalAlignment = VerticalAlignment.Top
                                                                }
                                Grid.SetRow(keyBlock, row)
                                Grid.SetColumn(keyBlock, 0)
                                attributeGrid.Children.Add(keyBlock)

                                Dim valueText = If(value?.ToString(), "")
                                If valueText.Length > 100 Then
                                    valueText = valueText.Substring(0, 97) & "..."
                                End If

                                Dim valueBlock = New TextBlock With {
                                                                        .Text = valueText,
                                                                        .TextWrapping = TextWrapping.Wrap,
                                                                        .Margin = New Thickness(0, 2, 0, 2)
                                                                    }
                                Grid.SetRow(valueBlock, row)
                                Grid.SetColumn(valueBlock, 1)
                                attributeGrid.Children.Add(valueBlock)

                                row += 1
                            End If
                        Next
                        Dim hasD2Attributes As Boolean = False
                        For Each k In punto.Attributes.Keys
                            If k IsNot Nothing AndAlso k.ToString().StartsWith("D2", StringComparison.OrdinalIgnoreCase) Then
                                hasD2Attributes = True
                                Exit For
                            End If
                        Next
                        infoPanel.Children.Add(attributeGrid)
                        'LogDebug($"Attributi caricati: {String.Join(",", GetKeysAsStringList(punto.Attributes))}")
                        If hasD2Attributes Then
                            Dim graphButton As New Button With {
                        .Content = "Visualizza Grafico",
                        .Margin = New Thickness(0, 10, 0, 10),
                        .Padding = New Thickness(10, 5, 10, 5),
                        .Background = New SolidColorBrush(System.Windows.Media.Colors.LightBlue),
                        .BorderBrush = New SolidColorBrush(System.Windows.Media.Colors.DarkBlue),
                        .HorizontalAlignment = HorizontalAlignment.Left
                    }

                            AddHandler graphButton.Click, Sub(sender, e)
                                                              Try
                                                                  Dim ds20Values As New List(Of Double)()
                                                                  Dim ds20Labels As New List(Of String)()
                                                                  For Each key As String In punto.Attributes.Keys
                                                                      If key.StartsWith("D2", StringComparison.OrdinalIgnoreCase) Then
                                                                          Dim value = punto.Attributes(key)
                                                                          Dim val As Double
                                                                          If Double.TryParse(value?.ToString(), Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture, val) Then
                                                                              ds20Labels.Add(key)
                                                                              ds20Values.Add(val)
                                                                          End If
                                                                      End If
                                                                  Next

                                                                  ' Ricava H se presente tra gli attributi
                                                                  Dim hValue As String = ""
                                                                  If punto.Attributes.ContainsKey("HEIGHT") Then
                                                                      hValue = punto.Attributes("HEIGHT").ToString()
                                                                  End If

                                                                  Dim infoPunto As String = $"Lat:{punto.Latitude:F6}(°); Long:{punto.Longitude:F6}(°); H:{hValue} (m s.l.m.)"
                                                                  Dim chartWindow As New Ds20ChartWindow(ds20Labels, ds20Values, punto.Value, infoPunto)
                                                                  chartWindow.Owner = Me
                                                                  'chartWindow.Topmost = True
                                                                  chartWindow.ShowDialog()
                                                              Catch ex As Exception
                                                                  LogDebug($"Errore apertura grafico: {ex.Message}")
                                                                  MessageBox.Show("Errore nella visualizzazione del grafico", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
                                                              End Try
                                                          End Sub

                            infoPanel.Children.Add(graphButton)
                        End If
                    End If
                End If

                scrollViewer.Content = infoPanel
                Grid.SetRow(scrollViewer, 1)
                contentGrid.Children.Add(scrollViewer)
                border.Child = contentGrid
                popup.Child = border

                RegisterName("InfoPopup", popup)
                RegisterName("InfoPanel", infoPanel)

                popup.PlacementTarget = Me
                popup.HorizontalOffset = Me.ActualWidth - 370
                popup.VerticalOffset = 60

                popup.IsOpen = True

                Try
                    Dim puntoMigliore = puntiVicini.First().Punto
                    Dim latString = Convert.ToString(puntoMigliore.Latitude, Globalization.CultureInfo.InvariantCulture)
                    Dim lonString = Convert.ToString(puntoMigliore.Longitude, Globalization.CultureInfo.InvariantCulture)
                    Dim script = $"highlightPoint({latString}, {lonString});"
                    WebView.CoreWebView2.ExecuteScriptAsync(script)
                Catch ex As Exception
                    LogDebug($"Errore nell'evidenziare il punto sulla mappa: {ex.Message}")
                End Try
            Catch ex As Exception
                LogDebug($"ERRORE CRITICO nella visualizzazione delle informazioni: {ex.Message}")
            End Try
        End Sub
        Private Function CreateHeaderTextBlock(text As String, color As System.Windows.Media.Color) As TextBlock
            Return New TextBlock With {
                .Text = text,
                .FontWeight = FontWeights.Bold,
                .Foreground = New SolidColorBrush(color),
                .Margin = New Thickness(0, 8, 0, 0)
            }
        End Function

        Private Function CreateValueTextBlock(text As String) As TextBlock
            Return New TextBlock With {
                .Text = text,
                .Margin = New Thickness(5, 2, 0, 2),
                .TextWrapping = TextWrapping.Wrap
            }
        End Function

        Private Function CaricaDettagliPunto(pointId As String) As Dictionary(Of String, Object)
            Dim attributes As New Dictionary(Of String, Object)()
            Try
                If String.IsNullOrEmpty(pointId) Then
                    LogDebug("AVVISO: CODE punto vuoto o null")
                    Return attributes
                End If
                Dim safeId As String = pointId.Replace("'", "''")
                Dim query As String = $"SELECT CODE,HEIGHT,H_STDEV,VEL,V_STDEV,COHE,GEOMETRY,ORIGINE_DATO,DATI_VARIABILI FROM GEO_DATI_INTERFEROMETRICI_SPATIAL WHERE CODE = '{safeId}'"
                'LogDebug($"Esecuzione query dettagli: {query}")
                Dim dt = _oracleHelper.ExecuteQuery(query)
                If dt Is Nothing Then Return attributes

                If dt.Rows.Count > 0 Then
                    Dim row = dt.Rows(0)
                    For Each col As DataColumn In dt.Columns
                        If row(col) IsNot DBNull.Value AndAlso col.ColumnName <> "DATI_VARIABILI" Then
                            Try
                                attributes(col.ColumnName) = row(col)
                            Catch ex As Exception
                                LogDebug($"Errore nell'aggiungere l'attributo {col.ColumnName}: {ex.Message}")
                            End Try
                        End If
                    Next

                    ' Gestione DATI_VARIABILI (JSON)
                    If dt.Columns.Contains("DATI_VARIABILI") AndAlso row("DATI_VARIABILI") IsNot DBNull.Value Then
                        Try
                            Dim jsonString As String = row("DATI_VARIABILI").ToString()
                            Dim dict = JsonSerializer.Deserialize(Of Dictionary(Of String, Object))(jsonString)
                            If dict IsNot Nothing Then
                                For Each kvp In dict
                                    attributes(kvp.Key) = kvp.Value
                                Next
                            End If
                        Catch ex As Exception
                            LogDebug($"Errore parsing DATI_VARIABILI JSON: {ex.Message}")
                        End Try
                    End If
                End If
            Catch ex As Exception
                LogDebug($"ERRORE nel caricamento dettagli punto: {ex.Message}")
            End Try
            Return attributes
        End Function

        'calcolare l’intersezione tra due bounds:
        Private Function IntersectBounds(b1 As BoundingBox, b2 As BoundingBox) As BoundingBox
            Dim north = Math.Min(b1.North, b2.North)
            Dim south = Math.Max(b1.South, b2.South)
            Dim east = Math.Min(b1.East, b2.East)
            Dim west = Math.Max(b1.West, b2.West)
            If north > south AndAlso east > west Then
                Return New BoundingBox With {.North = north, .South = south, .East = east, .West = west}
            Else
                Return Nothing
            End If
        End Function

        ' Aggiorna i filtri a cascata
        Private Sub AggiornaFiltri(Optional livello As Integer = 0)
            Dim filtri As New Dictionary(Of String, List(Of String))
            If livello > 0 AndAlso LstSt.SelectedItems.Count > 0 Then filtri("ST") = LstSt.SelectedItems.Cast(Of String).ToList()
            If livello > 1 AndAlso LstStrada.SelectedItems.Count > 0 Then filtri("STRADA") = LstStrada.SelectedItems.Cast(Of String).ToList()
            If livello > 2 AndAlso LstKmSito.SelectedItems.Count > 0 Then filtri("KM_SITO") = LstKmSito.SelectedItems.Cast(Of String).ToList()
            If livello > 3 AndAlso LstSensore.SelectedItems.Count > 0 Then filtri("SENSORE") = LstSensore.SelectedItems.Cast(Of String).ToList()
            If livello > 4 AndAlso LstPeriodo.SelectedItems.Count > 0 Then filtri("PERIODO") = LstPeriodo.SelectedItems.Cast(Of String).ToList()
            If livello > 5 AndAlso LstTipo.SelectedItems.Count > 0 Then filtri("TIPO") = LstTipo.SelectedItems.Cast(Of String).ToList()
            If livello > 6 AndAlso LstDirezione.SelectedItems.Count > 0 Then filtri("DIREZIONE") = LstDirezione.SelectedItems.Cast(Of String).ToList()

            If livello <= 0 Then LstSt.ItemsSource = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "ST", New Dictionary(Of String, List(Of String)))
            If livello <= 1 Then LstStrada.ItemsSource = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "STRADA", filtri)
            If livello <= 2 Then LstKmSito.ItemsSource = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "KM_SITO", filtri)
            If livello <= 3 Then LstSensore.ItemsSource = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "SENSORE", filtri)
            If livello <= 4 Then LstPeriodo.ItemsSource = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "PERIODO", filtri)
            If livello <= 5 Then LstTipo.ItemsSource = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "TIPO", filtri)
            If livello <= 6 Then LstDirezione.ItemsSource = _oracleHelper.GetDistinctValuesMulti("GEO_ANAGRAFICA", "DIREZIONE", filtri)
        End Sub

        ' Evento per ogni filtro
        Private Sub Filtro_SelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Select Case True
                Case sender Is LstSt
                    AggiornaFiltri(1)
                Case sender Is LstStrada
                    AggiornaFiltri(2)
                Case sender Is LstKmSito
                    AggiornaFiltri(3)
                Case sender Is LstSensore
                    AggiornaFiltri(4)
                Case sender Is LstPeriodo
                    AggiornaFiltri(5)
                Case sender Is LstTipo
                    AggiornaFiltri(6)
                Case sender Is LstDirezione
                    ' Nessun filtro successivo
            End Select
        End Sub

        ' Funzione helper per parsing robusto di double con separatore decimale variabile
        Private Function ParseDoubleFlexible(input As Object) As Double
            If input Is Nothing OrElse input Is DBNull.Value Then Return 0
            Dim s As String = input.ToString().Trim()
            If String.IsNullOrEmpty(s) Then Return 0
            ' Normalizza sempre la virgola in punto
            s = s.Replace(",", ".")
            Dim result As Double = 0
            If Double.TryParse(s, Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture, result) Then
                Return result
            End If
            ' Tentativo fallback con cultura corrente
            If Double.TryParse(s, Globalization.NumberStyles.Any, Globalization.CultureInfo.CurrentCulture, result) Then
                Return result
            End If
            Return 0
        End Function
#End Region

#Region "Comunicazione con WebView"
        Private Sub CoreWebView2_NavigationCompleted(sender As Object, e As CoreWebView2NavigationCompletedEventArgs)
            If e.IsSuccess Then
                LogDebug("Navigazione completata con successo")
                StatusText.Text = "Mappa caricata correttamente"
            Else
                LogDebug($"ERRORE nella navigazione: {e.WebErrorStatus}")
                StatusText.Text = "Errore nel caricamento della mappa"
            End If
        End Sub

        Private Sub CoreWebView2_WebMessageReceived(sender As Object, e As CoreWebView2WebMessageReceivedEventArgs)
            LogDebug($"[WebMessageReceived] Azione: {e.WebMessageAsJson}")
            Dim message = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson)
            Dim action = message.RootElement.GetProperty("action").GetString()

            Select Case action
                Case "map_ready"
                    _isMapInitialized = True
                    LogDebug("Mappa Google inizializzata e pronta")
                    StatusText.Text = "Mappa pronta"
                    Try
                        WebView.CoreWebView2.OpenDevToolsWindow()
                        LogDebug("DevTools aperto automaticamente")
                    Catch ex As Exception
                        LogDebug($"ERRORE apertura DevTools: {ex.Message}")
                    End Try

                Case "debug"
                    LogDebug($"Debug JS: {message.RootElement.GetProperty("message").GetString()}")

                Case "error"
                    LogDebug($"ERRORE JS: {message.RootElement.GetProperty("message").GetString()}")

                Case "webgl_created"
                    _isOverlayActive = True
                    LogDebug($"Layer WebGL creato con {message.RootElement.GetProperty("pointCount").GetInt32()} punti")

                Case "marker_click"
                    Dim latitude = message.RootElement.GetProperty("latitude").GetDouble()
                    Dim longitude = message.RootElement.GetProperty("longitude").GetDouble()
                    LogDebug($"Click su marker: Lat={latitude}, Lon={longitude}")
                    TrovaEMostraPuntoVicino(latitude, longitude)

                Case "zoom_changed", "bounds_changed"
                    LogDebug($"Evento {action} ricevuto")
                    ' Aggiorna overlay solo se attivo e autoaggiornamento richiesto
                    If ChkAutoAggiorna IsNot Nothing AndAlso ChkAutoAggiorna.IsChecked = True AndAlso _isOverlayActive Then
                        Dim zoomLevel As Double = _config.DefaultZoomLevel
                        If message.RootElement.TryGetProperty("zoom", Nothing) Then
                            zoomLevel = message.RootElement.GetProperty("zoom").GetDouble()
                        End If
                        Dim bounds = message.RootElement.GetProperty("bounds")
                        Dim viewportBox As New BoundingBox With {
    .North = ParseDoubleFlexible(bounds.GetProperty("north").ToString()),
    .South = ParseDoubleFlexible(bounds.GetProperty("south").ToString()),
    .East = ParseDoubleFlexible(bounds.GetProperty("east").ToString()),
    .West = ParseDoubleFlexible(bounds.GetProperty("west").ToString())
}
                        Static updateTimer As System.Threading.Timer = Nothing
                        If updateTimer IsNot Nothing Then updateTimer.Dispose()
                        updateTimer = New System.Threading.Timer(
            Sub(state)
                Dispatcher.Invoke(Async Sub()
                                      Try
                                          ' Carica overlay per tutti i bounds visibili nella viewport
                                          Dim puntiTotali As New List(Of GeoPoint)
                                          For Each bound In _allLoadedBounds
                                              Dim intersezione = IntersectBounds(bound, viewportBox)
                                              If intersezione IsNot Nothing Then
                                                  _lastLoadedBounds = intersezione
                                                  Dim punti = Await CaricaPuntiDaDB(intersezione, zoomLevel)
                                                  puntiTotali.AddRange(punti)
                                              End If
                                          Next
                                          _lastLoadedBounds = CalcolaBounds(puntiTotali)
                                          Await RenderPointsAsync(puntiTotali, zoomLevel)
                                          MostraLegendaColorbar(COLOR_SCALE_MIN, COLOR_SCALE_MAX)
                                          StatusText.Text = $"Visualizzati {puntiTotali.Count} punti (zoom {zoomLevel:F1})"
                                      Catch ex As Exception
                                          LogDebug($"ERRORE autoaggiornamento overlay: {ex.Message}")
                                          StatusText.Text = "Errore autoaggiornamento overlay"
                                      End Try
                                  End Sub)
            End Sub,
            Nothing, _config.AutoUpdateDelayMs, Timeout.Infinite)
                    End If

                Case "map_click"
                    If _isOverlayActive Then
                        Dim latitude = message.RootElement.GetProperty("latitude").GetDouble()
                        Dim longitude = message.RootElement.GetProperty("longitude").GetDouble()
                        LogDebug($"Click sulla mappa: Lat={latitude}, Lon={longitude}")
                        TrovaEMostraPuntoVicino(latitude, longitude)
                    End If

                Case Else
                    LogDebug($"[WebMessageReceived] Azione NON gestita: {action}")
            End Select
        End Sub
#End Region
#Region "Classi di supporto"
        Public Class GeoPoint
            Public Property Id As String
            Public Property Latitude As Double
            Public Property Longitude As Double
            Public Property Value As Double
            Public Property Attributes As New Dictionary(Of String, Object)
        End Class

        Public Class BoundingBox
            Public Property North As Double
            Public Property South As Double
            Public Property East As Double
            Public Property West As Double
        End Class

        Public Class MapConfiguration
            Public Property DefaultZoomLevel As Double = 6.0
            Public Property MaxPointsAtLowZoom As Integer = 100000
            Public Property MaxPointsAtMediumZoom As Integer = 100000
            Public Property MaxPointsAtHighZoom As Integer = 100000
            Public Property AutoUpdateDelayMs As Integer = 1500
            Public Property MarkerOpacity As Double = 0.8
            Public Property HighZoomThreshold As Double = 15.0
            Public Property MediumZoomThreshold As Double = 12.0
            Public Property LowZoomThreshold As Double = 9.0

            Public Property SamplePercentLowZoom As Integer = 5
            Public Property SamplePercentMediumZoom As Integer = 20
            Public Property SamplePercentHighZoom As Integer = 50

            ' Funzione per ottenere il valore di SAMPLE in base allo zoom
            Public Function GetSamplePercent(zoomLevel As Double) As Integer?
                If zoomLevel >= HighZoomThreshold Then
                    Return Nothing ' Nessun sample per zoom alti
                ElseIf zoomLevel >= MediumZoomThreshold Then
                    Return SamplePercentMediumZoom
                Else
                    Return SamplePercentLowZoom
                End If
            End Function
        End Class


        Public Class JsBounds
            Public Property north As Double
            Public Property south As Double
            Public Property east As Double
            Public Property west As Double
        End Class
#End Region

    End Class
End Namespace