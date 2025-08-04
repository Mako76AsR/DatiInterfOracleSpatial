Imports System.Data
Imports System.Globalization
Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Web
Imports Esri.ArcGISRuntime.Geometry
Imports Esri.ArcGISRuntime.UI.Controls
Imports Microsoft.Web.WebView2.Core
Imports Microsoft.Win32
Imports Newtonsoft.Json

Public Class GoogleMapsWindow
    ' === CAMPI DI STATO E CONFIGURAZIONE ===
    Private _webViewInitialized As Boolean = False
    Private _dbLoaded As Boolean = False
    Private _csvData As New List(Of CsvPoint)
    Private _viewpointChangedTimer As Threading.Timer
    Private _isClusteringEnabled As Boolean = True
    Private _loadingData As Boolean = False
    Private _batchSize As Integer = 500 ' Numero di punti da caricare per batch
    Private _isFirstLoad As Boolean = True
    Private _maxRecordLimit As Integer = 5000 ' Valore predefinito


    ' === AGGIUNGI QUESTE VARIABILI A LIVELLO DI CLASSE PER SOSTITUIRE LE VARIABILI STATIC ===
    Private _lastZoom As Double = 0
    Private _lastBounds As Newtonsoft.Json.Linq.JObject = Nothing
    Private _lastQueryTime As DateTime = DateTime.MinValue
    Private _isFirstLoadForViewChange As Boolean = True

    ' === STRUTTURA DATI PER PUNTI ===
    Public Class CsvPoint
        Public Property Latitude As Double
        Public Property Longitude As Double
        Public Property Attributes As New Dictionary(Of String, Object)

        Public Function ToJson() As String
            Dim obj As New With {
                .lat = Latitude,
                .lng = Longitude,
                .attributes = Attributes
            }
            Return JsonConvert.SerializeObject(obj)
        End Function
    End Class

    ' === INIZIALIZZAZIONE FINESTRA ===
    Private Async Sub Window_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Await InitializeWebView()
    End Sub


    ' === INIZIALIZZAZIONE WEBVIEW2 ===
    ' === INIZIALIZZAZIONE WEBVIEW2 ===

    Private Async Function InitializeWebView() As Task
        Try
            Await WebView.EnsureCoreWebView2Async()

            ' Abilita DevTools
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = True

            ' Opzionale: apri DevTools automaticamente all'avvio
            WebView.CoreWebView2.OpenDevToolsWindow()

            ' Imposta gestore di messaggi JavaScript->VB.NET
            AddHandler WebView.CoreWebView2.WebMessageReceived, AddressOf WebView_WebMessageReceived

            ' Aggiungi un handler per NavigationCompleted per applicare patch JavaScript
            AddHandler WebView.CoreWebView2.NavigationCompleted, AddressOf WebView_NavigationCompleted

            ' Carica la pagina HTML con Google Maps
            LoadGoogleMapsHtml()

            _webViewInitialized = True
            Await MiglioraHeatmap()

            ' AGGIUNTA: Installa sistema di monitoraggio marker
            Await Task.Delay(2000) ' Attendi che la mappa sia completamente inizializzata
            Await WebView.ExecuteScriptAsync("
            // Sistema di monitoraggio e recupero visibilità marker
            console.log('>>> JS: Installazione sistema di monitoraggio marker');
            
            // Aggiungi contatore visibilità nella UI
            let markerMonitor = document.createElement('div');
            markerMonitor.id = 'markerMonitor';
            markerMonitor.style.position = 'absolute';
            markerMonitor.style.top = '50px';
            markerMonitor.style.right = '10px';
            markerMonitor.style.background = 'rgba(255,255,255,0.8)';
            markerMonitor.style.padding = '5px';
            markerMonitor.style.borderRadius = '3px';
            markerMonitor.style.fontSize = '12px';
            markerMonitor.style.zIndex = '1000';
            markerMonitor.style.display = 'none';
            document.body.appendChild(markerMonitor);
            
            // Pulsante emergenza
            let fixButton = document.createElement('button');
            fixButton.textContent = 'Forza visibilità marker';
            fixButton.style.position = 'absolute';
            fixButton.style.top = '10px';
            fixButton.style.right = '10px';
            fixButton.style.zIndex = '1000';
            fixButton.style.padding = '5px 10px';
            fixButton.onclick = function() {
                forceMarkersVisible();
            };
            document.body.appendChild(fixButton);
            
            // Funzione per forzare visibilità dei marker
            window.forceMarkersVisible = function() {
                console.log('>>> JS: FORZA VISIBILITÀ MARKER MANUALE');
                if (markers && markers.length > 0) {
                    markers.forEach(m => {
                        if (m) {
                            try {
                                m.setVisible(true);
                                m.setMap(map);
                            } catch(e) {
                                console.error('Errore set marker visible:', e);
                            }
                        }
                    });
                    
                    updateMarkerMonitor();
                    
                    // Forza anche resize e redraw
                    if (map) {
                        google.maps.event.trigger(map, 'resize');
                        console.log('>>> JS: Resize mappa forzato');
                    }
                    
                    if (isClusteringEnabled) {
                        // Disabilita clustering e poi riabilitalo
                        if (markersCluster) {
                            markersCluster.clearMarkers();
                            markersCluster = null;
                        }
                        setTimeout(() => enableClustering(), 200);
                    }
                } else {
                    console.log('>>> JS: Nessun marker da forzare visibile');
                }
            };
            
            // Funzione per aggiornare monitor
            function updateMarkerMonitor() {
                const monitor = document.getElementById('markerMonitor');
                if (!monitor) return;
                
                if (!markers || markers.length === 0) {
                    monitor.textContent = 'Nessun marker';
                    monitor.style.display = 'none';
                    return;
                }
                
                monitor.style.display = 'block';
                const totalMarkers = markers.length;
                const visibleMarkers = markers.filter(m => m && m.getMap() === map).length;
                monitor.textContent = `Marker: ${visibleMarkers}/${totalMarkers} visibili`;
                monitor.style.color = visibleMarkers === totalMarkers ? 'green' : 'red';
            }
            
            // Sistema di monitoraggio periodico
            setInterval(function() {
                updateMarkerMonitor();
                
                // Auto-correzione se necessario
                if (markers && markers.length > 0) {
                    const totalMarkers = markers.length;
                    const visibleMarkers = markers.filter(m => m && m.getMap() === map).length;
                    
                    if (visibleMarkers === 0 && totalMarkers > 0) {
                        console.log('>>> JS: CORREZIONE AUTOMATICA - Marker invisibili rilevati');
                        forceMarkersVisible();
                    }
                }
            }, 2000);
            
            // Sostituisci la funzione originale di aggiunta marker
            const originalAddSinglePoint = addSinglePoint;
            addSinglePoint = function(point) {
                try {
                    const marker = new google.maps.Marker({
                        position: { 
                            lat: Number(point.lat), 
                            lng: Number(point.lng) 
                        },
                        map: map,
                        optimized: false, // Importante per visibilità
                        icon: getMeanVelIcon(point.attributes.MEAN_VEL),
                        visible: true,    // Forza visibilità
                        zIndex: 9999      // Alta priorità
                    });
                    
                    // Store attributes with the marker
                    marker.attributes = point.attributes;
                    
                    // Add click listener
                    marker.addListener('click', function() {
                        showInfoWindow(this);
                    });
                    
                    markers.push(marker);
                    
                    // Verifica che il marker sia realmente sulla mappa
                    setTimeout(function() {
                        if (marker && marker.getMap() !== map) {
                            console.log('>>> JS: Correggo marker invisibile');
                            marker.setMap(map);
                        }
                    }, 50);
                    
                } catch(e) {
                    console.error('>>> JS: Errore creazione marker:', e);
                }
            };
        ")




            Await PreserveMarkers()


        Catch ex As Exception
            MessageBox.Show($"Errore durante l'inizializzazione di WebView2: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try
    End Function

    ' Aggiungi questo nuovo metodo per gestire l'evento NavigationCompleted
    Private Async Sub WebView_NavigationCompleted(sender As Object, e As CoreWebView2NavigationCompletedEventArgs)
        If e.IsSuccess Then
            ' Aspetta un momento per assicurarsi che tutti gli script siano caricati
            Await Task.Delay(500)

            ' Patch completa per bloccare zoom automatici indesiderati
            Dim jsCode As String = "
            try {
                // Blocco completo di zoom automatici indesiderati
                let isDataLoading = false;
                let originalViewportState = null;
                
                // 1. Salva lo stato originale del viewport quando inizia il caricamento
                const originalAddPoints = addPoints;
                addPoints = function(points) {
                    if (!isMapReady) return;
                    
                    isDataLoading = true;
                    
                    // Salva lo stato prima di qualsiasi modifica
                    originalViewportState = {
                        center: map.getCenter(),
                        zoom: map.getZoom()
                    };
                    
                    // Disattiva temporaneamente gli eventi bounds_changed
                    const originalNotifyBoundsChanged = notifyBoundsChanged;
                    notifyBoundsChanged = function() {
                        console.log('Notifiche cambio viewport temporaneamente disattivate');
                    };
                    
                    // Chiama la funzione originale
                    originalAddPoints(points);
                    
                    // Ripristina lo stato originale e riattiva le notifiche dopo il caricamento
                    setTimeout(function() {
                        if (originalViewportState) {
                            map.setCenter(originalViewportState.center);
                            map.setZoom(originalViewportState.zoom);
                        }
                        
                        // Riattiva notifiche dopo aver ripristinato lo stato
                        setTimeout(function() {
                            notifyBoundsChanged = originalNotifyBoundsChanged;
                            isDataLoading = false;
                            console.log('Notifiche viewport ripristinate');
                        }, 500);
                    }, 100);
                };
                
                // 2. Previeni completamente zoom automatici dopo il primo caricamento
                let viewportAlreadySet = false;
                const originalSetViewport = setViewport;
                setViewport = function(north, east, south, west) {
                    if (viewportAlreadySet) {
                        console.log('Zoom automatico bloccato: viewport già impostato');
                        return;
                    }
                    
                    console.log('Imposto viewport per la prima volta');
                    originalSetViewport(north, east, south, west);
                    viewportAlreadySet = true;
                };
                
                // 3. Blocca cambiamenti durante altre operazioni di rendering
                const originalRenderHeatmap = renderHeatmap;
                renderHeatmap = function(points) {
                    isDataLoading = true;
                    
                    // Salva stato corrente
                    originalViewportState = {
                        center: map.getCenter(),
                        zoom: map.getZoom()
                    };
                    
                    // Rendering
                    originalRenderHeatmap(points);
                    
                    // Ripristina stato e riattiva eventi
                    setTimeout(function() {
                        if (originalViewportState) {
                            map.setCenter(originalViewportState.center);
                            map.setZoom(originalViewportState.zoom);
                        }
                        isDataLoading = false;
                    }, 300);
                };
                
                // 4. Migliora il funzionamento del clustering e visibilità dei marker
                const originalEnableClustering = enableClustering;
                enableClustering = function() {
                    try {
                        console.log('>>> JS: Chiamata migliorata a enableClustering, markers: ' + markers.length);
                        
                        // Assicurati che tutti i marker siano sulla mappa prima del clustering
                        markers.forEach(function(marker) {
                            if (marker && marker.setMap) {
                                marker.setMap(map);
                            }
                        });
                        
                        // Ora chiama la funzione originale
                        originalEnableClustering();
                    } catch (err) {
                        console.error('>>> JS: Errore in enableClustering migliorato:', err);
                    }
                };
                
                console.log('Sistema anti-zoom automatico e miglioramento marker installato');
            } catch (err) {
                console.error('Errore installando sistema anti-zoom:', err);
            }
        "

            Await WebView.ExecuteScriptAsync(jsCode)
        End If
    End Sub
    ' metodo per installare una heatmap migliorata
    Private Async Function MiglioraHeatmap() As Task
        Dim jsCode As String = "
    // Modifica la funzione renderHeatmap per usare colori basati sul valore MEAN_VEL
    renderHeatmap = function(points) {
        renderMode = 'heatmap';
        clearAllLayers();
        showLoading('Generazione heatmap avanzata...');
        
        // Prepara struttura per calcolare valori medi per area
        const gridSize = 0.001; // Regola questa dimensione della griglia in base alle tue esigenze
        const grid = {};
        
        // Prima passata: raggruppa i punti per celle della griglia e calcola medie
        points.forEach(point => {
            const lat = Math.round(point.lat / gridSize) * gridSize;
            const lng = Math.round(point.lng / gridSize) * gridSize;
            const key = `${lat},${lng}`;
            const meanVel = parseFloat(point.attributes && point.attributes.MEAN_VEL !== undefined ? 
                                      point.attributes.MEAN_VEL : 0);
            
            if (!grid[key]) {
                grid[key] = { 
                    count: 0, 
                    totalMeanVel: 0, 
                    lat: lat,
                    lng: lng
                };
            }
            
            if (!isNaN(meanVel)) {
                grid[key].count++;
                grid[key].totalMeanVel += meanVel;
            }
        });
        
        // Converti in array di punti per heatmap con peso e colore basati su MEAN_VEL
        const gradientColors = {
            '-5': '#A52A2A',   // Brown (< -4.5)
            '-4': '#8B0000',   // DarkRed (-4.5 to -3.5)
            '-3': '#FF0000',   // Red (-3.5 to -2.5)
            '-2': '#FF4500',   // OrangeRed (-2.5 to -1.5)
            '-1': '#FFA500',   // Orange (-1.5 to -0.5)
            '0': '#FFFF00',    // Yellow (-0.5 to 0.5)
            '1': '#008000',    // Green (0.5 to 1.5)
            '2': '#00FF00',    // Lime (1.5 to 2.5)
            '3': '#00FFFF',    // Cyan (2.5 to 3.5)
            '4': '#00BFFF',    // DeepSkyBlue (3.5 to 4.5)
            '5': '#0000FF'     // Blue (> 4.5)
        };
        
        // Crea la heatmap con colori personalizzati
        const heatmapData = [];
        
        for (const key in grid) {
            if (grid[key].count > 0) {
                const avgMeanVel = grid[key].totalMeanVel / grid[key].count;
                
                // Calcola intensità basata sul valore MEAN_VEL
                const intensity = Math.min(1, (Math.abs(avgMeanVel) + 0.5) / 5);
                
                heatmapData.push({
                    location: new google.maps.LatLng(grid[key].lat, grid[key].lng),
                    weight: intensity,
                    meanVel: avgMeanVel
                });
            }
        }
        
        // Crea i colori del gradiente
        const gradient = [];
        const colorKeys = Object.keys(gradientColors).map(Number).sort((a, b) => a - b);
        
        // Normalizza i colori per il gradiente
        colorKeys.forEach((key, index) => {
            const position = index / (colorKeys.length - 1);
            gradient.push(`${gradientColors[key]} ${position}`);
        });
        
        // Crea la heatmap con gradiente personalizzato
        heatmap = new google.maps.visualization.HeatmapLayer({
            data: heatmapData,
            map: map,
            radius: 20,
            opacity: 0.8,
            gradient: gradient
        });
        
        hideLoading();
        updateStats(`Heatmap basata su ${points.length} punti con valori MEAN_VEL`);
    };
    console.log('Funzione heatmap migliorata installata');
    "

        Await WebView.ExecuteScriptAsync(jsCode)
    End Function


    ' === CARICAMENTO HTML GOOGLE MAPS ===
    Private Sub LoadGoogleMapsHtml()
        ' Usa una chiave API Google Maps valida
        Dim apiKey As String = "AIzaSyAeleu3f7WRCB8m62b3R6MQH-RQmY_ZfNI"

        Dim htmlContent As String = $"
<!DOCTYPE html>
<html>
<head>
    <title>Google Maps</title>
    <meta name=""viewport"" content=""initial-scale=1.0"">
    <meta charset=""utf-8"">
    <style>
        html, body, #map {{
            height: 100%;
            margin: 0;
            padding: 0;
        }}
        .loading {{
            position: absolute;
            top: 10px;
            left: 50%;
            transform: translateX(-50%);
            background-color: #fff;
            padding: 10px;
            border-radius: 5px;
            box-shadow: 0 2px 5px rgba(0,0,0,0.3);
            z-index: 1000;
        }}
        #zoomMessage {{
            position: absolute;
            bottom: 40px;
            left: 50%;
            transform: translateX(-50%);
            background-color: rgba(0,0,0,0.7);
            color: white;
            padding: 8px 15px;
            border-radius: 20px;
            z-index: 1000;
            font-size: 14px;
            transition: opacity 0.3s;
            opacity: 0;
            pointer-events: none;
        }}
        #stats {{
            position: absolute;
            bottom: 10px;
            right: 10px;
            background-color: rgba(255,255,255,0.8);
            padding: 5px;
            border-radius: 3px;
            font-size: 12px;
            z-index: 1000;
        }}
    </style>
    <script src=""https://maps.googleapis.com/maps/api/js?key={apiKey}&libraries=visualization""></script>
    <script>
        let map;
        let markers = [];
        let markersCluster = null;  // MODIFICATO: markerClusterer -> markersCluster
        let currentBounds = null;
        let isMapReady = false;
        let isClusteringEnabled = true;
        let isMapDragging = false;
        let isMapZooming = false;
        let messageTimeout;
        let pointLayer = null;
        let heatmap = null;
        let renderMode = 'markers'; // 'markers', 'webgl', o 'heatmap'
        let markerRenderLimit = 5000; // valore di default, sarà aggiornato da .NET
        
        function initMap() {{
            map = new google.maps.Map(document.getElementById(""map""), {{
                center: {{ lat: 41.9028, lng: 12.4964 }}, // Centro Italia
                zoom: 6,
                maxZoom: 18,
                mapTypeId: google.maps.MapTypeId.ROADMAP,
                mapTypeControl: true,
                scaleControl: true,
                fullscreenControl: true
            }});
            
            // Aggiungi div per messaggio zoom
            let zoomMessage = document.createElement('div');
            zoomMessage.id = 'zoomMessage';
            zoomMessage.textContent = 'Rilascia per aggiornare la vista';
            document.body.appendChild(zoomMessage);
            
            // Aggiungi div per statistiche
            let statsDiv = document.createElement('div');
            statsDiv.id = 'stats';
            document.body.appendChild(statsDiv);
            
            isMapReady = true;
            
            // Migliora la gestione degli eventi di zoom/pan
            map.addListener(""dragstart"", function() {{
                isMapDragging = true;
                showZoomMessage();
            }});
            
            map.addListener(""dragend"", function() {{
                isMapDragging = false;
                hideZoomMessage();
                notifyBoundsChanged();
            }});
            
            map.addListener(""zoom_changed"", function() {{
                isMapZooming = true;
                showZoomMessage();
                
                // Utilizza un timeout più breve per zoom
                clearTimeout(messageTimeout);
                messageTimeout = setTimeout(function() {{
                    isMapZooming = false;
                    hideZoomMessage();
                    notifyBoundsChanged();
                }}, 300);
            }});
            
            // Aggiorna solo quando lo spostamento della mappa è significativo
            let lastNotifiedBounds = null;
            let boundsChangedTimer;
            
            map.addListener(""bounds_changed"", function() {{
                if (isMapDragging || isMapZooming) return;
                
                clearTimeout(boundsChangedTimer);
                boundsChangedTimer = setTimeout(function() {{
                    const newBounds = map.getBounds();
                    
                    // Verifica se il cambio di viewport è significativo
                    if (lastNotifiedBounds) {{
                        const ne1 = lastNotifiedBounds.getNorthEast();
                        const sw1 = lastNotifiedBounds.getSouthWest();
                        const ne2 = newBounds.getNorthEast();
                        const sw2 = newBounds.getSouthWest();
                        
                        // Calcola la differenza percentuale - ridotta al 5% per maggiore sensibilità
                        const diffX = Math.abs((ne2.lng() - ne1.lng()) / (ne1.lng() - sw1.lng())) * 100;
                        const diffY = Math.abs((ne2.lat() - ne1.lat()) / (ne1.lat() - sw1.lat())) * 100;
                        
                        // Notifica solo se il cambio è > 5% (era 10%)
                        if (diffX < 5 && diffY < 5) return;
                    }}
                    
                    lastNotifiedBounds = newBounds;
                    notifyBoundsChanged();
                }}, 200); // Ridotto a 200ms per maggiore reattività (era 300ms)
            }});
        }}
        
        function showZoomMessage() {{
            const msg = document.getElementById('zoomMessage');
            if (msg) {{
                msg.style.opacity = '1';
            }}
        }}
        
        function hideZoomMessage() {{
            const msg = document.getElementById('zoomMessage');
            if (msg) {{
                msg.style.opacity = '0';
            }}
        }}
        
        function notifyBoundsChanged() {{
            if (!isMapReady) return;
            
            currentBounds = map.getBounds();
            const center = map.getCenter();
            const zoom = map.getZoom();
            
            // Notifica a .NET che la mappa è stata spostata
            window.chrome.webview.postMessage({{
                type: ""viewChanged"",
                bounds: {{
                    north: currentBounds.getNorthEast().lat(),
                    east: currentBounds.getNorthEast().lng(),
                    south: currentBounds.getSouthWest().lat(),
                    west: currentBounds.getSouthWest().lng()
                }},
                zoom: zoom
            }});
        }}
        
        // Aggiunge un array di punti alla mappa con batching
        function addPoints(points) {{
            if (!isMapReady) return;
            
            clearAllLayers();
            
            const totalPoints = points.length;
            updateStats(`Punti totali: ${{totalPoints}}`);
            
            // Determina automaticamente il miglior metodo di rendering
            //if (totalPoints > markerRenderLimit) {{
             //   renderPointsWebGL(points);
            //    return;
            //}} else if (totalPoints > 1000) {{
            //    renderHeatmap(points);
            //    return;
           // }}
            
            // Metodo standard di batching per meno di 1000 punti
            renderMode = 'markers';
            showLoading(`Preparazione caricamento ${{totalPoints}} punti...`);
            
            // Batch adding to improve performance
            const batchSize = 200;
            let processedPoints = 0;
            
            function processBatch() {{
                const batch = points.slice(processedPoints, processedPoints + batchSize);
                if (batch.length === 0) {{
                    if (isClusteringEnabled && markers.length > 0) {{
                        enableClustering();
                    }}
                    hideLoading();
                    return;
                }}
                
                batch.forEach(point => {{
                    addSinglePoint(point);
                }});
                
                processedPoints += batch.length;
                const progress = Math.min(100, Math.round((processedPoints / totalPoints) * 100));
                updateLoading(`Caricamento: ${{progress}}% (${{processedPoints}}/${{totalPoints}})`);
                
                // Process next batch asynchronously
                setTimeout(processBatch, 10);
            }}
            
            setTimeout(processBatch, 50);
        }}
        
        function addSinglePoint(point) {{
            const marker = new google.maps.Marker({{
                position: {{ lat: Number(point.lat), lng: Number(point.lng) }},
                map: map,
                optimized: true,
                icon: getMeanVelIcon(point.attributes.MEAN_VEL)
            }});
            
            // Store attributes with the marker
            marker.attributes = point.attributes;
            
            // Add click listener
            marker.addListener(""click"", function() {{
                showInfoWindow(this);
            }});
            
            markers.push(marker);
        }}
        
        // WebGL renderer per grandi set di dati
        function renderPointsWebGL(points) {{
            renderMode = 'webgl';
            clearAllLayers();
            showLoading('Inizializzazione renderer WebGL per grandi dataset...');
            
            // Se abbiamo già un layer, rimuoviamolo
            if (pointLayer) {{
                pointLayer.setMap(null);
                pointLayer = null;
            }}
            
            // Crea un nuovo layer CanvasLayer per rendering ottimizzato
            pointLayer = new google.maps.OverlayView();
            
            // Array di punti da renderizzare
            const dataPoints = points;
            const pointColors = getColorArray(dataPoints);
            
            // Override dei metodi di OverlayView
            pointLayer.onAdd = function() {{
                this.canvas = document.createElement('canvas');
                this.canvas.style.position = 'absolute';
                this.canvas.style.left = '0';
                this.canvas.style.top = '0';
                this.canvas.style.pointerEvents = 'none';
                
                this.div = document.createElement('div');
                this.div.appendChild(this.canvas);
                this.getPanes().overlayLayer.appendChild(this.div);
                
                // Aggiungi listener per click sulla mappa
                this.listeners = [];
                const overlay = this;
                
                // Gestisci click per trovare punto più vicino
                this.listeners.push(
                    google.maps.event.addDomListener(map, 'click', function(event) {{
                        const clickPoint = {{
                            x: event.pixel.x,
                            y: event.pixel.y
                        }};
                        
                        // Cerca il punto più vicino
                        const nearestPoint = findNearestPoint(clickPoint, overlay.projectedPoints);
                        if (nearestPoint && nearestPoint.distance < 10) {{ // 10px di tolleranza
                            // Invia messaggio a .NET
                            window.chrome.webview.postMessage({{
                                type: ""markerClicked"",
                                data: {{
                                    lat: dataPoints[nearestPoint.index].lat,
                                    lng: dataPoints[nearestPoint.index].lng,
                                    attributes: dataPoints[nearestPoint.index].attributes || {{ MEAN_VEL: dataPoints[nearestPoint.index].val }}
                                }}
                            }});
                        }}
                    }})
                );
            }};
            
            // Draw del canvas
            pointLayer.draw = function() {{
                const projection = this.getProjection();
                const zoom = map.getZoom();
                const bounds = map.getBounds();
                
                // Calcola dimensioni canvas
                const sw = projection.fromLatLngToDivPixel(bounds.getSouthWest());
                const ne = projection.fromLatLngToDivPixel(bounds.getNorthEast());
                const width = ne.x - sw.x;
                const height = sw.y - ne.y;
                
                // Dimensiona il canvas
                this.canvas.width = width;
                this.canvas.height = height;
                this.canvas.style.width = width + 'px';
                this.canvas.style.height = height + 'px';
                this.div.style.left = sw.x + 'px';
                this.div.style.top = ne.y + 'px';
                
                // Ottieni il contesto 2D
                const ctx = this.canvas.getContext('2d');
                ctx.clearRect(0, 0, width, height);
                
                // Determinazione della dimensione dei punti in base allo zoom
                const pointRadius = (zoom > 14) ? 4 : (zoom > 10) ? 3 : 2;
                
                // Prepara array dei punti proiettati per click detection
                this.projectedPoints = [];
                
                // Rendering ottimizzato
                const visiblePoints = [];
                for (let i = 0; i < dataPoints.length; i++) {{
                    const point = dataPoints[i];
                    if (!bounds.contains(new google.maps.LatLng(point.lat, point.lng))) continue;
                    
                    const pixel = projection.fromLatLngToDivPixel(new google.maps.LatLng(point.lat, point.lng));
                    const x = pixel.x - sw.x;
                    const y = pixel.y - ne.y;
                    
                    // Salva coordinate per click detection
                    this.projectedPoints.push({{ x, y, index: i }});
                    
                    // Salva per batch rendering
                    visiblePoints.push({{ x, y, color: pointColors[i] }});
                }}
                
                // Aggiorna statistiche
                updateStats(`Punti totali: ${{dataPoints.length}}, Punti visibili: ${{visiblePoints.length}}`);
                
                // Batch rendering di tutti i punti
                for (const point of visiblePoints) {{
                    ctx.beginPath();
                    ctx.arc(point.x, point.y, pointRadius, 0, Math.PI * 2);
                    ctx.fillStyle = point.color;
                    ctx.fill();
                    ctx.strokeStyle = '#000000';
                    ctx.lineWidth = 0.5;
                    ctx.stroke();
                }}
                
                hideLoading();
            }};
            
            // Helper per trovare il punto più vicino a un click
            function findNearestPoint(clickPoint, points) {{
                let nearest = null;
                let minDistance = Infinity;
                
                for (let i = 0; i < points.length; i++) {{
                    const dx = clickPoint.x - points[i].x;
                    const dy = clickPoint.y - points[i].y;
                    const distance = Math.sqrt(dx * dx + dy * dy);
                    
                    if (distance < minDistance) {{
                        minDistance = distance;
                        nearest = {{ index: points[i].index, distance: distance }};
                    }}
                }}
                
                return nearest;
            }}
            
            // Helper per generare colori basati sul valore MEAN_VEL
            function getColorArray(points) {{
                return points.map(point => {{
                    const val = parseFloat(point.val || (point.attributes ? point.attributes.MEAN_VEL : 0));
                    if (isNaN(val)) return '#808080';
                    
                    if (val >= 4.5) return '#0000FF'; // Blue
                    else if (val >= 3.5) return '#00BFFF'; // DeepSkyBlue
                    else if (val >= 2.5) return '#00FFFF'; // Cyan
                    else if (val >= 1.5) return '#00FF00'; // Lime
                    else if (val >= 0.5) return '#008000'; // Green
                    else if (val >= -0.5) return '#FFFF00'; // Yellow
                    else if (val >= -1.5) return '#FFA500'; // Orange
                    else if (val >= -2.5) return '#FF4500'; // OrangeRed
                    else if (val >= -3.5) return '#FF0000'; // Red
                    else if (val >= -4.5) return '#8B0000'; // DarkRed
                    else return '#A52A2A'; // Brown
                }});
            }}
            
            // Aggiungi il layer alla mappa
            pointLayer.setMap(map);
        }}
        
        // Renderizza come heatmap per dataset di medie dimensioni
        function renderHeatmap(points) {{
            renderMode = 'heatmap';
            clearAllLayers();
            showLoading('Generazione heatmap...');
            
            const heatmapData = [];
            
            // Converti i punti in formato heatmap
            points.forEach(point => {{
                const meanVel = parseFloat(point.val || (point.attributes ? point.attributes.MEAN_VEL : 0));
                // Peso basato sul valore (usa il valore assoluto per far risaltare i valori estremi)
                const weight = Math.min(1, Math.abs(meanVel) / 5); 
                
                heatmapData.push({{
                    location: new google.maps.LatLng(point.lat, point.lng),
                    weight: weight
                }});
            }});
            
            // Crea la heatmap
            heatmap = new google.maps.visualization.HeatmapLayer({{
                data: heatmapData,
                map: map,
                radius: 20,
                opacity: 0.7
            }});
            
            hideLoading();
            updateStats(`Punti totali: ${{points.length}} (Heatmap)`);
        }}
        
        // Pulisci tutti i layer (markers, WebGL canvas, heatmap)
        function clearAllLayers() {{
            clearMarkers();
            
            if (pointLayer) {{
                pointLayer.setMap(null);
                pointLayer = null;
            }}
            
            if (heatmap) {{
                heatmap.setMap(null);
                heatmap = null;
            }}
        }}
        
        // Show native Google Maps info window
        function showInfoWindow(marker) {{
            // Create info content - usando apici singoli per evitare problemi di escape
            let content = '<div style=""max-width: 300px; max-height: 200px; overflow: auto;""><h4>Dettagli punto</h4>';
            
            // Check for DS20 values
            let hasDS20 = false;
            for (const key in marker.attributes) {{
                if (key.startsWith(""D2"")) {{
                    hasDS20 = true;
                    break;
                }}
            }}
            
            // Add regular attributes
            for (const key in marker.attributes) {{
                if (!key.startsWith(""D2"")) {{
                    content += '<strong>' + key + ':</strong> ' + marker.attributes[key] + '<br>';
                }}
            }}
            
            // Add button for DS20 if needed
            if (hasDS20) {{
                content += '<br><button onclick=""sendDS20DataToApp(this)"">Mostra grafico serie temporale</button>';
            }}
            
            content += '</div>';
            
            // Create and show info window
            const infoWindow = new google.maps.InfoWindow({{
                content: content
            }});
            
            // Store marker data in the DOM element for the button click
            infoWindow.addListener(""domready"", function() {{
                const buttons = document.querySelectorAll(""button"");
                buttons.forEach(button => {{
                    if (button.textContent === ""Mostra grafico serie temporale"") {{
                        button.markerData = marker.attributes;
                    }}
                }});
            }});
            
            infoWindow.open(map, marker);
        }}
        
        // Send DS20 data to .NET app
        function sendDS20DataToApp(button) {{
            if (!button.markerData) return;
            
            window.chrome.webview.postMessage({{
                type: ""showDS20Chart"",
                data: {{ attributes: button.markerData }}
            }});
        }}
        
        // Mostra indicatore di caricamento
        function showLoading(text) {{
            let loadingDiv = document.getElementById(""loading"");
            if (!loadingDiv) {{
                loadingDiv = document.createElement(""div"");
                loadingDiv.id = ""loading"";
                loadingDiv.className = ""loading"";
                document.body.appendChild(loadingDiv);
            }}
            loadingDiv.textContent = text || ""Caricamento..."";
            loadingDiv.style.display = ""block"";
        }}
        
        // Aggiorna testo loading
        function updateLoading(text) {{
            const loadingDiv = document.getElementById(""loading"");
            if (loadingDiv) {{
                loadingDiv.textContent = text;
            }}
        }}
        
        // Nascondi indicatore di caricamento
        function hideLoading() {{
            const loadingDiv = document.getElementById(""loading"");
            if (loadingDiv) {{
                loadingDiv.style.display = ""none"";
            }}
        }}
        
        // Aggiorna statistiche
        function updateStats(text) {{
            const statsDiv = document.getElementById(""stats"");
            if (statsDiv) {{
                statsDiv.textContent = text;
            }}
        }}
        
        // Abilita il clustering dei marker
        function enableClustering() {{
    try {{
        if (markersCluster) {{
            markersCluster.clearMarkers();
        }}

        const gridSize = calculateGridSize(map.getZoom());
        if (gridSize <= 1) {{
            // Non fare clustering a zoom elevato
            return;
        }}

        // Controllo compatibilità MarkerClusterer v2+
        if (window.markerClusterer && window.markerClusterer.MarkerClusterer) {{
            markersCluster = new window.markerClusterer.MarkerClusterer({{
                map: map,
                markers: markers,
                gridSize: gridSize,
                maxZoom: 15,
                minimumClusterSize: 3,
                imagePath: ""https://developers.google.com/maps/documentation/javascript/examples/markerclusterer/m""
            }});
        }} else {{
            console.error(""MarkerClusterer non trovato! Controlla il caricamento della libreria."");
        }}
    }} catch (err) {{
        console.error(""Error enabling clustering:"", err);
    }}
}}
        
        // Calcola dimensione griglia in base al livello di zoom
        function calculateGridSize(zoom) {{
            if (zoom < 7) return 80;
            if (zoom < 10) return 60;
            if (zoom < 13) return 40;
            if (zoom < 15) return 20;
            return 1; // disabilita clustering ad alti zoom
        }}
        
        // Rimuove tutti i marker dalla mappa
        function clearMarkers() {{
            if (markersCluster) {{  // MODIFICATO: markerClusterer -> markersCluster
                markersCluster.clearMarkers();
                markersCluster = null;
            }}
            
            markers.forEach(marker => {{
                marker.setMap(null);
            }});
            
            markers = [];
        }}
        
        // Restituisce l'icona in base al valore MEAN_VEL
        function getMeanVelIcon(meanVelValue) {{
            if (meanVelValue === undefined || meanVelValue === null) return null;
            
            // Assicurati che meanVel sia un numero
            const meanVel = parseFloat(meanVelValue);
            if (isNaN(meanVel)) return null;
            
            let color = ""#808080""; // Grigio per default
            
            if (meanVel >= 4.5) color = ""#0000FF""; // Blue
            else if (meanVel >= 3.5) color = ""#00BFFF""; // DeepSkyBlue
            else if (meanVel >= 2.5) color = ""#00FFFF""; // Cyan
            else if (meanVel >= 1.5) color = ""#00FF00""; // Lime
            else if (meanVel >= 0.5) color = ""#008000""; // Green
            else if (meanVel >= -0.5) color = ""#FFFF00""; // Yellow
            else if (meanVel >= -1.5) color = ""#FFA500""; // Orange
            else if (meanVel >= -2.5) color = ""#FF4500""; // OrangeRed
            else if (meanVel >= -3.5) color = ""#FF0000""; // Red
            else if (meanVel >= -4.5) color = ""#8B0000""; // DarkRed
            else color = ""#A52A2A""; // Brown
            
            return {{
                path: google.maps.SymbolPath.CIRCLE,
                fillColor: color,
                fillOpacity: 0.8,
                strokeWeight: 1,
                strokeColor: ""#000000"",
                scale: 8
            }};
        }}
        
        // Imposta la vista su un'area specifica
        function setViewport(north, east, south, west) {{
            const bounds = new google.maps.LatLngBounds(
                new google.maps.LatLng(south, west),
                new google.maps.LatLng(north, east)
            );
            map.fitBounds(bounds);
        }}
        
        // Attiva/disattiva clustering
        function toggleClustering(enabled) {{
            isClusteringEnabled = enabled;
            if (enabled && markers.length > 0 && renderMode === 'markers') {{
                enableClustering();
            }} else if (!enabled && markersCluster) {{  // MODIFICATO: markerClusterer -> markersCluster
                markersCluster.clearMarkers();
                markersCluster = null;
            }}
        }}
        
        // Cambia metodo di rendering
        function changeRenderMode(mode, points) {{
            if (!points || points.length === 0) return;
            
            if (mode === 'markers') {{
                addPoints(points);
            }} else if (mode === 'webgl') {{
                renderPointsWebGL(points);
            }} else if (mode === 'heatmap') {{
                renderHeatmap(points);
            }}
        }}
    </script>
    <script src=""https://unpkg.com/@googlemaps/markerclusterer/dist/index.min.js""></script>
</head>
<body onload=""initMap()"">
    <div id=""map""></div>
</body>
</html>"

        WebView.CoreWebView2.NavigateToString(htmlContent)
    End Sub
    ' === GESTORE MESSAGGI DA JAVASCRIPT ===

    Private Async Sub WebView_WebMessageReceived(sender As Object, e As CoreWebView2WebMessageReceivedEventArgs)
        Try
            Dim message = JsonConvert.DeserializeObject(Of Dictionary(Of String, Object))(e.WebMessageAsJson)

            Select Case message("type").ToString()
                Case "viewChanged"
                    ' Gestisci cambio viewport solo se non stiamo già caricando dati
                    If Not _loadingData AndAlso _dbLoaded Then
                        Dim bounds = CType(message("bounds"), Newtonsoft.Json.Linq.JObject)
                        Dim zoom = Convert.ToDouble(message("zoom"))

                        ' RIMUOVI LE DICHIARAZIONI STATIC E USA LE VARIABILI DI CLASSE
                        ' Static lastZoom As Double = 0            <- RIMOSSO
                        ' Static lastBounds As Newtonsoft.Json.Linq.JObject = Nothing <- RIMOSSO
                        ' Static lastQueryTime As DateTime = DateTime.MinValue <- RIMOSSO
                        ' Static isFirstLoad As Boolean = True <- RIMOSSO

                        ' MODIFICA: Aggiungi un ritardo significativo dopo la prima visualizzazione
                        ' per evitare ricaricamenti immediati che potrebbero cancellare i dati esistenti
                        Dim minDelayAfterFirstLoad As Double = 5 ' Aumentato a 5 secondi
                        If DateTime.Now.Subtract(_lastQueryTime).TotalSeconds < minDelayAfterFirstLoad Then
                            Debug.WriteLine($"Ignoro l'evento viewChanged: troppo presto dopo il caricamento precedente ({DateTime.Now.Subtract(_lastQueryTime).TotalSeconds:F1} sec)")
                            Return
                        End If

                        ' Calcola se l'area è cambiata
                        Dim areaChanged As Boolean = False
                        If _lastBounds IsNot Nothing Then
                            Dim oldWidth = Math.Abs(Convert.ToDouble(_lastBounds("east")) - Convert.ToDouble(_lastBounds("west")))
                            Dim oldHeight = Math.Abs(Convert.ToDouble(_lastBounds("north")) - Convert.ToDouble(_lastBounds("south")))
                            Dim newWidth = Math.Abs(Convert.ToDouble(bounds("east")) - Convert.ToDouble(bounds("west")))
                            Dim newHeight = Math.Abs(Convert.ToDouble(bounds("north")) - Convert.ToDouble(bounds("south")))

                            ' Rendi la soglia MENO restrittiva (30% invece di 70%)
                            Dim areaRatio = (newWidth * newHeight) / (oldWidth * oldHeight)
                            areaChanged = (areaRatio < 0.7 OrElse areaRatio > 1.3) ' Più sensibile ai cambiamenti
                        End If

                        ' MODIFICA: Riduci la sensibilità dei ricaricamenti
                        Dim shouldReload As Boolean = _isFirstLoadForViewChange OrElse
                   (Math.Abs(zoom - _lastZoom) >= 2) OrElse ' Cambiato da 1 a 2 livelli di zoom per ridurre ricaricamenti
                   areaChanged OrElse
                   (DateTime.Now.Subtract(_lastQueryTime).TotalSeconds > 30) ' Aumentato da 15 a 30 secondi

                        Debug.WriteLine($"Zoom: {zoom}, LastZoom: {_lastZoom}, AreaChanged: {areaChanged}, ShouldReload: {shouldReload}")

                        If shouldReload Then
                            _lastZoom = zoom
                            _lastBounds = bounds
                            _lastQueryTime = DateTime.Now
                            _isFirstLoadForViewChange = False

                            If _viewpointChangedTimer IsNot Nothing Then
                                _viewpointChangedTimer.Dispose()
                            End If

                            ' Aumenta il ritardo del timer
                            _viewpointChangedTimer = New Threading.Timer(Sub(state)
                                                                             Dispatcher.Invoke(Sub() CaricaDatiDaDB(bounds, zoom))
                                                                         End Sub, Nothing, 800, Timeout.Infinite) ' Aumentato a 800ms
                        End If
                    End If

                Case "markerClicked"
                    ' Gestisci il click su un marker
                    Dim data = CType(message("data"), Newtonsoft.Json.Linq.JObject)
                    ShowPointInfo(data)

                Case "showDS20Chart"
                    ' Gestisci richiesta di visualizzazione grafico DS20
                    Dim data = CType(message("data"), Newtonsoft.Json.Linq.JObject)
                    Dim attributes = data("attributes")
                    ShowDS20Chart(attributes)

                Case "recreateMarkers"
                    ' Ricrea tutti i marker da zero
                    If _csvData.Count > 0 Then
                        Debug.WriteLine(">>> Ricreazione forzata di tutti i marker")
                        Await VisualizzaPuntiSenzaAdjustViewport()
                    End If
            End Select
        Catch ex As Exception
            Debug.WriteLine($"Errore nel processare il messaggio da WebView: {ex.Message}")
        End Try
    End Sub

    ' === SELEZIONE E CARICAMENTO CSV ===
    Private Async Sub BtnCaricaCSV_Click(sender As Object, e As RoutedEventArgs)
        _dbLoaded = False
        Dim dlg As New OpenFileDialog With {
        .Filter = "File CSV (*.csv)|*.csv",
        .Title = "Seleziona un file CSV"
    }

        If dlg.ShowDialog() = True Then
            Await CaricaPuntiDaCSVAsync(dlg.FileName)
            Await WebView.ExecuteScriptAsync("markerRenderLimit = 1000;") ' <-- MODIFICA QUI
            Await WebView.ExecuteScriptAsync("isClusteringEnabled = true;")
            Await VisualizzaPuntiSuMappa()
        End If
    End Sub

    ' === PARSING CSV E CREAZIONE OGGETTI PUNTO ===
    Private Async Function CaricaPuntiDaCSVAsync(filePath As String) As Task
        _csvData.Clear()
        _loadingData = True
        Dim hideLoadingAfterError As Boolean = False

        Try
            Await WebView.ExecuteScriptAsync("showLoading('Lettura file CSV...')")

            Dim totalLines As Integer = File.ReadAllLines(filePath).Length
            Dim processedLines As Integer = 0
            Dim validPoints As Integer = 0

            Using reader As New StreamReader(filePath, Encoding.UTF8)
                Dim headerLine As String = Await reader.ReadLineAsync()
                If String.IsNullOrEmpty(headerLine) Then
                    Throw New Exception("Il file CSV è vuoto o non contiene intestazioni")
                End If

                Dim headers As String() = headerLine.Split(","c).Select(Function(h) h.Trim()).ToArray()
                Dim latIndex As Integer = TrovaIndiceColonna(headers, {"lat", "latitude", "latitudine"})
                Dim lonIndex As Integer = TrovaIndiceColonna(headers, {"lon", "lng", "long", "longitude", "longitudine"})

                If latIndex = -1 OrElse lonIndex = -1 Then
                    Throw New Exception("Impossibile trovare le colonne di latitudine e longitudine nel CSV")
                End If

                ' Trova l'indice della colonna MEAN_VEL in modo case-insensitive
                Dim meanVelIndex As Integer = -1
                For i As Integer = 0 To headers.Length - 1
                    If String.Equals(headers(i), "MEAN_VEL", StringComparison.OrdinalIgnoreCase) Then
                        meanVelIndex = i
                        Exit For
                    End If
                Next

                Dim line As String
                While (InlineAssignHelper(line, Await reader.ReadLineAsync())) IsNot Nothing
                    processedLines += 1

                    If processedLines Mod 1000 = 0 Then
                        Await WebView.ExecuteScriptAsync($"updateLoading('Elaborazione CSV: {processedLines}/{totalLines} righe ({validPoints} punti validi)')")
                        Await Task.Delay(1)
                    End If

                    Dim values As String() = line.Split(","c)
                    If values.Length < Math.Max(latIndex, lonIndex) + 1 Then Continue While

                    Dim lat As Double, lon As Double
                    If Double.TryParse(values(latIndex), NumberStyles.Any, CultureInfo.InvariantCulture, lat) AndAlso
                   Double.TryParse(values(lonIndex), NumberStyles.Any, CultureInfo.InvariantCulture, lon) Then

                        Dim point As New CsvPoint With {
                        .Latitude = lat,
                        .Longitude = lon
                    }

                        ' Aggiungi tutti gli attributi disponibili come stringa
                        For i As Integer = 0 To Math.Min(headers.Length, values.Length) - 1
                            If i <> latIndex AndAlso i <> lonIndex Then
                                point.Attributes(headers(i)) = values(i)
                            End If
                        Next

                        ' Normalizza tutti gli attributi come stringa numerica se possibile
                        For Each key In point.Attributes.Keys.ToList()
                            Dim valStr = point.Attributes(key)?.ToString()
                            Dim valDouble As Double
                            If Double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, valDouble) Then
                                point.Attributes(key) = valDouble.ToString(CultureInfo.InvariantCulture)
                            Else
                                point.Attributes(key) = valStr
                            End If
                        Next

                        ' Gestione case-insensitive per MEAN_VEL
                        Dim meanVelKey As String = Nothing
                        For Each key In point.Attributes.Keys
                            If String.Equals(key, "MEAN_VEL", StringComparison.OrdinalIgnoreCase) Then
                                meanVelKey = key
                                Exit For
                            End If
                        Next

                        If meanVelKey IsNot Nothing Then
                            Debug.WriteLine("MEAN_VEL: " & point.Attributes(meanVelKey))
                            Dim tmp As Double
                            If Double.TryParse(point.Attributes(meanVelKey).ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, tmp) Then
                                point.Attributes("MEAN_VEL") = tmp.ToString(CultureInfo.InvariantCulture)
                            Else
                                point.Attributes("MEAN_VEL") = "0"
                            End If
                            ' Se la chiave trovata non è esattamente "MEAN_VEL", rimuovila per evitare duplicati
                            If meanVelKey <> "MEAN_VEL" Then
                                point.Attributes.Remove(meanVelKey)
                            End If
                        ElseIf meanVelIndex <> -1 AndAlso meanVelIndex < values.Length Then
                            ' Se la colonna esiste ma non è stata aggiunta come attributo (ad esempio se coincide con lat/lon)
                            Dim tmp As Double
                            If Double.TryParse(values(meanVelIndex), NumberStyles.Any, CultureInfo.InvariantCulture, tmp) Then
                                point.Attributes("MEAN_VEL") = tmp.ToString(CultureInfo.InvariantCulture)
                            Else
                                point.Attributes("MEAN_VEL") = "0"
                            End If
                        Else
                            point.Attributes("MEAN_VEL") = "0"
                        End If

                        _csvData.Add(point)
                        validPoints += 1
                    End If
                End While
            End Using

            Await WebView.ExecuteScriptAsync($"updateLoading('Elaborazione CSV completata: {validPoints} punti validi')")
            Await Task.Delay(500)
            MessageBox.Show($"{_csvData.Count} punti caricati in memoria.", "Completato", MessageBoxButton.OK, MessageBoxImage.Information)
        Catch ex As Exception
            MessageBox.Show($"Errore durante la lettura del CSV: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
            hideLoadingAfterError = True
        Finally
            _loadingData = False
            If hideLoadingAfterError Then
                Task.Run(Async Function()
                             Await Task.Delay(100)
                             Await Dispatcher.InvokeAsync(Async Function()
                                                              Await WebView.ExecuteScriptAsync("hideLoading()")
                                                          End Function)
                         End Function)
            End If
        End Try
    End Function

    Private Async Sub BtnForceMarkers_Click(sender As Object, e As RoutedEventArgs)
        If Not _webViewInitialized Then Return

        ' Disabilita il pulsante durante l'operazione
        BtnForceMarkers.IsEnabled = False
        BtnForceMarkers.Content = "Forzatura in corso..."

        Try
            Await ForceMarkersVisible()

            ' Breve ritardo per dare tempo al sistema di applicare le modifiche
            Await Task.Delay(2000)

            ' Verifica se i marker sono visibili
            Dim result = Await VerifyMarkersVisible()
            If Not result Then
                ' Se ancora non visibili, forza una ricreazione completa
                Debug.WriteLine(">>> Markers ancora non visibili, ricreazione completa")
                Await VisualizzaPuntiSenzaAdjustViewport()
            End If
        Catch ex As Exception
            Debug.WriteLine($">>> Errore durante forzatura marker: {ex.Message}")
        Finally
            ' Riabilita il pulsante
            BtnForceMarkers.IsEnabled = True
            BtnForceMarkers.Content = "Forza Visibilità Marker"
        End Try
    End Sub


    ' === VISUALIZZAZIONE PUNTI SU GOOGLE MAPS ===
    ' Modifica questo metodo
    Private Async Function VisualizzaPuntiSuMappa() As Task
        If Not _webViewInitialized OrElse _csvData.Count = 0 Then
            Debug.WriteLine($"VisualizzaPuntiSuMappa: Nessun punto da visualizzare (_webViewInitialized={_webViewInitialized}, _csvData.Count={_csvData.Count})")
            Return
        End If

        Try
            Debug.WriteLine($"VisualizzaPuntiSuMappa: Visualizzazione di {_csvData.Count} punti")

            ' Prima visualizza i punti SENZA modificare il viewport
            Await VisualizzaPuntiSenzaAdjustViewport()

            ' Poi, SOLO al primo caricamento, imposta il viewport per centrare i punti
            If _isFirstLoad Then
                _isFirstLoad = False

                ' Imposta la vista (assicurati formato corretto)
                If _csvData.Count > 0 Then
                    Dim north = _csvData.Max(Function(p) p.Latitude)
                    Dim south = _csvData.Min(Function(p) p.Latitude)
                    Dim east = _csvData.Max(Function(p) p.Longitude)
                    Dim west = _csvData.Min(Function(p) p.Longitude)

                    ' Usa toString per garantire formato corretto
                    Dim cmd = $"setViewport({north.ToString(CultureInfo.InvariantCulture)}, " &
                          $"{east.ToString(CultureInfo.InvariantCulture)}, " &
                          $"{south.ToString(CultureInfo.InvariantCulture)}, " &
                          $"{west.ToString(CultureInfo.InvariantCulture)})"

                    Debug.WriteLine($"SetViewport (formato corretto): {cmd}")
                    Await WebView.ExecuteScriptAsync(cmd)
                End If
            End If
        Catch ex As Exception
            MessageBox.Show($"Errore durante la visualizzazione dei punti: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try
    End Function
    ' Funzione per correggere la visualizzazione dei marker
    Private Async Function FixMarkerVisibility() As Task
        Dim fixScript As String = "
        console.log('>>> JS: Applicazione fix per marker invisibili');
        
        // Forza la visualizzazione di tutti i marker esistenti
        if (markers && markers.length > 0) {
            console.log('>>> JS: Forzatura visualizzazione di ' + markers.length + ' marker');
            markers.forEach(function(marker) {
                if (marker && marker.setMap) {
                    marker.setMap(map);
                }
            });
            
            // Forza l'aggiornamento del clustering se abilitato
            if (isClusteringEnabled) {
                setTimeout(function() {
                    enableClustering();
                    console.log('>>> JS: Clustering riapplicato');
                }, 100);
            }
            
            console.log('>>> JS: Forzatura completata');
        } else {
            console.log('>>> JS: Nessun marker da forzare');
        }
    "

        Await WebView.ExecuteScriptAsync(fixScript)
    End Function

    ' Funzione per verificare la visibilità dei marker
    Private Async Function VerifyMarkersVisible() As Task(Of Boolean)
        Dim result = Await WebView.ExecuteScriptAsync("
        (function() {
            try {
                if (!markers || markers.length === 0) {
                    return JSON.stringify({status: 'NO_MARKERS', count: 0});
                }
                
                const visibleCount = markers.filter(m => m && m.getMap() === map).length;
                console.log('>>> JS: Verifica marker - Totali: ' + markers.length + ', Visibili: ' + visibleCount);
                
                return JSON.stringify({
                    total: markers.length,
                    visible: visibleCount,
                    status: visibleCount > 0 ? 'OK' : 'ERRORE'
                });
            } catch(e) {
                console.error('>>> JS: Errore verifica marker:', e);
                return JSON.stringify({status: 'ERROR', message: e.toString()});
            }
        })();
    ")

        Debug.WriteLine($">>> Verifica marker: {result}")
        Return result.Contains("""status"":""OK""")
    End Function


    ' Miglioramento della visualizzazione dei marker ad alto zoom

    Private Async Function VisualizzaPuntiSenzaAdjustViewport() As Task
        If Not _webViewInitialized OrElse _csvData.Count = 0 Then
            Debug.WriteLine(">>> VisualizzaPuntiSenzaAdjustViewport: Nessun punto da visualizzare")
            Return
        End If

        Try
            Debug.WriteLine($">>> VisualizzaPuntiSenzaAdjustViewport: Inizio visualizzazione di {_csvData.Count} punti")

            ' Imposta culture invariant per garantire formattazione corretta dei numeri
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture

            ' Impostazioni JSON per formattazione corretta dei numeri
            Dim serializerSettings As New JsonSerializerSettings With {
            .Culture = CultureInfo.InvariantCulture,
            .FloatFormatHandling = FloatFormatHandling.String
        }

            ' Serializza lat/lng come numeri
            Dim points = _csvData.Select(Function(p) New With {
            .lat = p.Latitude,
            .lng = p.Longitude,
            .attributes = p.Attributes
        }).ToList()

            Dim pointsJson = JsonConvert.SerializeObject(points, serializerSettings)

            ' Ottieni il livello di zoom attuale
            Dim currentZoom As Integer = 0
            Try
                Dim zoomResult = Await WebView.ExecuteScriptAsync("map.getZoom();")
                If Not String.IsNullOrEmpty(zoomResult) AndAlso Not zoomResult.Equals("null") Then
                    currentZoom = Convert.ToInt32(zoomResult)
                End If
            Catch ex As Exception
                Debug.WriteLine("Errore nel recupero del livello di zoom: " & ex.Message)
                currentZoom = 0
            End Try

            ' Registra il numero di punti e lo zoom corrente
            Debug.WriteLine($">>> Numero di punti: {_csvData.Count}, Zoom attuale: {currentZoom}")

            ' Ritardo più lungo prima della pulizia per evitare flickering
            Await Task.Delay(500)

            ' Verifica stato dei marker prima di pulire
            Dim beforeClearMarkerCheck = Await WebView.ExecuteScriptAsync("
            if (markers && markers.length > 0) {
                console.log('>>> JS: Prima di clearAllLayers, markers count: ' + markers.length);
                return 'Markers: ' + markers.length;
            } else {
                return 'No markers';
            }
        ")
            Debug.WriteLine($">>> Prima di clearAllLayers: {beforeClearMarkerCheck}")

            ' Disabilita temporaneamente il clustering prima di pulire
            Await WebView.ExecuteScriptAsync("isClusteringEnabled = false;")

            ' Pulisci prima tutti i layer esistenti
            Await WebView.ExecuteScriptAsync("
            console.log('>>> JS: Pulizia layer in corso...');
            clearAllLayers();
            console.log('>>> JS: Pulizia layer completata');
        ")

            ' Attesa più lunga dopo la pulizia
            Await Task.Delay(700)

            ' Determina la modalità di rendering basata sul livello di zoom e numero di punti
            Dim renderMode As String
            Dim zoomThreshold As Integer = 15

            If currentZoom >= zoomThreshold Then
                ' Ad alto zoom, mostra sempre i marker singoli per vedere i dettagli
                renderMode = "markers"
                Debug.WriteLine(">>> Forzatura modalità marker per zoom elevato")
            ElseIf _csvData.Count > 5000 Then
                ' Se troppi punti e zoom non elevato, usa heatmap per performance
                renderMode = "heatmap"
                Debug.WriteLine(">>> Uso heatmap per set di dati grande")
            Else
                ' Con pochi punti, usa sempre i marker
                renderMode = "markers"
                Debug.WriteLine(">>> Uso marker per set di dati piccolo")
            End If

            Debug.WriteLine($">>> Modalità rendering selezionata: {renderMode}, _csvData.Count={_csvData.Count}, zoom={currentZoom}")

            ' MIGLIORAMENTO: Forza il refresh della mappa prima di aggiungere i nuovi punti
            Await WebView.ExecuteScriptAsync("
            console.log('>>> JS: Forzatura refresh mappa prima di aggiungere nuovi punti');
            if (map) {
                google.maps.event.trigger(map, 'resize');
            }
        ")

            ' Esegui la modalità di rendering selezionata
            If renderMode = "markers" Then
                ' SUPER-ROBUST MARKER CREATION
                Debug.WriteLine($">>> Invio {_csvData.Count} punti alla mappa come markers (approccio diretto e super-robusto)")

                ' Prepara la mappa e resetta tutto
                Await WebView.ExecuteScriptAsync("
                // Preparazione per markers
                console.log('>>> JS: Preparazione rendering marker');
                renderMode = 'markers';
                if (markers.length > 0) {
                    console.log('>>> JS: Reset array markers esistente, count=' + markers.length);
                }
                markers = []; // Reset array markers
                isClusteringEnabled = true; // Abilita clustering
                
                // Verifica stato mappa
                if (!map) {
                    console.error('>>> JS: ERRORE - Mappa non disponibile!');
                    return;
                }
                
                console.log('>>> JS: Mappa pronta, zoom=' + map.getZoom());
                showLoading('Creazione marker in corso...');
            ")

                ' Invia i punti direttamente uno per uno per set di dati piccoli
                If _csvData.Count <= 50 Then
                    Debug.WriteLine(">>> Utilizzo approccio diretto per piccolo set di dati")

                    For i As Integer = 0 To _csvData.Count - 1
                        Dim p = _csvData(i)
                        Dim pointJson = JsonConvert.SerializeObject(New With {
                        .lat = p.Latitude,
                        .lng = p.Longitude,
                        .attributes = p.Attributes
                    }, serializerSettings)

                        ' Creazione diretta di marker
                        Await WebView.ExecuteScriptAsync($"
                        try {{
                            let point = {pointJson};
                            let marker = new google.maps.Marker({{
                                position: {{ 
                                    lat: Number(point.lat), 
                                    lng: Number(point.lng) 
                                }},
                                map: map,
                                optimized: false,
                                icon: getMeanVelIcon(point.attributes.MEAN_VEL),
                                visible: true,
                                zIndex: 100
                            }});
                            
                            marker.attributes = point.attributes;
                            marker.addListener('click', function() {{
                                showInfoWindow(this);
                            }});
                            
                            markers.push(marker);
                            
                            console.log('>>> JS: Marker creato con successo: ' + (markers.length));
                        }} catch(e) {{
                            console.error('>>> JS: Errore creazione marker:', e);
                        }}
                    ")

                        ' Aggiorna ogni 10 punti
                        If i Mod 10 = 0 AndAlso i > 0 Then
                            Await WebView.ExecuteScriptAsync($"updateLoading('Aggiunta marker: {i + 1}/{_csvData.Count}');")
                        End If
                    Next

                    ' Aggiorna UI e applica clustering dopo
                    Await WebView.ExecuteScriptAsync("
                    hideLoading();
                    console.log('>>> JS: Completata creazione di ' + markers.length + ' marker individuali');
                    
                    // Forza visibilità finale
                    setTimeout(function() {
                        if (markers && markers.length > 0) {
                            markers.forEach(function(m) {
                                if (m) {
                                    m.setVisible(true);
                                    m.setMap(map);
                                }
                            });
                            
                            if (isClusteringEnabled) {
                                console.log('>>> JS: Applicazione clustering finale');
                                setTimeout(function() { 
                                    try {
                                        enableClustering();
                                    } catch(e) {
                                        console.error('>>> JS: Errore enableClustering:', e);
                                    }
                                }, 300);
                            }
                        }
                    }, 300);
                    
                    // Forza il resize finale della mappa
                    setTimeout(function() {
                        google.maps.event.trigger(map, 'resize');
                        console.log('>>> JS: Resize mappa finale');
                    }, 600);
                ")
                Else
                    ' Usa approccio a batch per dataset più grandi ma con miglioramento robustezza
                    Debug.WriteLine(">>> Utilizzo approccio a batch migliorato per dataset grande")

                    ' Script di batch marker ultra-robusto
                    Await WebView.ExecuteScriptAsync("
                    // Funzione super-robusta per aggiungere marker in batch
                    function addMarkersBatchRobust(points) {{
                        console.log('>>> JS: Inizio rendering ' + points.length + ' marker con metodo batch');
                        
                        const batchSize = 100;
                        let processedPoints = 0;
                        let successful = 0;
                        
                        function processBatch() {{
                            const batch = points.slice(processedPoints, processedPoints + batchSize);
                            if (batch.length === 0) {{
                                console.log('>>> JS: Completato batch processing, marker creati: ' + successful);
                                
                                // Dopo tutti i batch, forza visibilità e clustering
                                if (isClusteringEnabled && markers.length > 0) {{
                                    setTimeout(function() {{
                                        // Forza visibilità
                                        markers.forEach(function(m) {{
                                            if (m) {{
                                                m.setVisible(true);
                                                m.setMap(map);
                                            }}
                                        }});
                                        
                                        // Applica clustering
                                        setTimeout(function() {{
                                            try {{
                                                enableClustering();
                                                console.log('>>> JS: Clustering applicato su ' + markers.length + ' marker');
                                            }} catch(e) {{
                                                console.error('>>> JS: Errore in clustering finale:', e);
                                            }}
                                        }}, 300);
                                    }}, 200);
                                }}
                                
                                hideLoading();
                                return;
                            }}
                            
                            try {{
                                // Batch processing
                                batch.forEach(point => {{
                                    try {{
                                        const marker = new google.maps.Marker({{
                                            position: {{ 
                                                lat: Number(point.lat), 
                                                lng: Number(point.lng) 
                                            }},
                                            map: map,
                                            optimized: false,
                                            icon: getMeanVelIcon(point.attributes.MEAN_VEL),
                                            visible: true,
                                            zIndex: 100
                                        }});
                                        
                                        marker.attributes = point.attributes;
                                        marker.addListener('click', function() {{
                                            showInfoWindow(this);
                                        }});
                                        
                                        markers.push(marker);
                                        successful++;
                                    }} catch(e) {{
                                        console.error('>>> JS: Errore creazione singolo marker:', e);
                                    }}
                                }});
                                
                                processedPoints += batch.length;
                                const progress = Math.min(100, Math.round((processedPoints / points.length) * 100));
                                updateLoading(`Caricamento: ${{progress}}% (${{processedPoints}}/${{points.length}}) - ${{successful}} marker creati`);
                                
                                // Processa prossimo batch con ritardo per non bloccare UI
                                setTimeout(processBatch, 100);
                            }} catch(batchError) {{
                                console.error('>>> JS: Errore grave nel batch processing:', batchError);
                                // Procedi comunque al prossimo batch
                                processedPoints += batch.length;
                                setTimeout(processBatch, 200);
                            }}
                        }}
                        
                        // Avvia il primo batch dopo un breve ritardo
                        setTimeout(processBatch, 200);
                    }}
                    
                    // Chiama la funzione batch con i punti
                    addMarkersBatchRobust({pointsJson});
                ")
                End If

                ' Attendi più a lungo per il rendering completo
                Await Task.Delay(Math.Max(1000, _csvData.Count / 10))

                ' Esegui verifiche finali e forzatura visibilità
                Await WebView.ExecuteScriptAsync("
                // Verifica finale e correzione visibilità
                console.log('>>> JS: Verifica finale marker visibili');
                
                if (markers && markers.length > 0) {
                    const totalMarkers = markers.length;
                    const visibleMarkers = markers.filter(m => m && m.getMap() === map).length;
                    
                    console.log(`>>> JS: Verifica finale - ${visibleMarkers}/${totalMarkers} marker visibili`);
                    
                    if (visibleMarkers < totalMarkers) {
                        console.log('>>> JS: Correzione visibilità marker finale');
                        
                        markers.forEach(m => { 
                            if (m && (!m.getMap() || m.getMap() !== map)) {
                                m.setVisible(true);
                                m.setMap(map);
                            }
                        });
                        
                        // Forza resize mappa
                        setTimeout(function() {
                            google.maps.event.trigger(map, 'resize');
                        }, 200);
                    }
                    
                    // Aggiorna UI stats
                    updateStats(`${totalMarkers} marker sulla mappa (${visibleMarkers} visibili)`);
                }
                
                // Forza refresh finale
                if (map) {
                    map.panBy(1, 0);
                    setTimeout(function() { map.panBy(-1, 0); }, 100);
                }
            ")

            Else ' Modalità heatmap (codice esistente)
                Debug.WriteLine($">>> Invio {_csvData.Count} punti alla mappa come heatmap")
                Await WebView.ExecuteScriptAsync($"changeRenderMode('{renderMode}', {pointsJson});")

                ' Forza l'aggiornamento della heatmap
                Await Task.Delay(700)
                Await WebView.ExecuteScriptAsync("
                if (heatmap) {
                    console.log('>>> JS: Forzatura aggiornamento heatmap');
                    heatmap.setMap(null);
                    setTimeout(function() {
                        heatmap.setMap(map);
                        updateStats(`Heatmap con ${" & _csvData.Count & "} punti`);
                    }, 200);
                } else {
                    console.log('>>> JS: Heatmap non disponibile dopo renderMode');
                }
            ")
            End If

            Debug.WriteLine(">>> VisualizzaPuntiSenzaAdjustViewport completato con successo")
        Catch ex As Exception
            Debug.WriteLine($"ERRORE in VisualizzaPuntiSenzaAdjustViewport: {ex.Message}")
            Debug.WriteLine($"Stack trace: {ex.StackTrace}")
            MessageBox.Show($"Errore durante la visualizzazione dei punti: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try
    End Function


    ' Aggiungi questo metodo per prevenire la perdita dei marker
    Private Async Function PreserveMarkers() As Task
        ' Questo script implementa un sistema di backup e ripristino dei marker
        Dim preserveScript As String = "
        console.log('>>> JS: Installazione sistema di preservazione marker');
        
        // Crea un array di backup per i marker
        if (!window.markerBackup) {
            window.markerBackup = {
                points: [],
                hasBackup: false
            };
        }
        
        // Intercetta la funzione clearAllLayers per creare un backup
        const originalClearAllLayers = clearAllLayers;
        clearAllLayers = function() {
            // Salva un backup solo se ci sono marker e siamo in modalità marker
            if (markers && markers.length > 0 && renderMode === 'markers') {
                console.log('>>> JS: Creazione backup di ' + markers.length + ' marker prima della pulizia');
                
                // Estrai i dati essenziali da ogni marker
                window.markerBackup.points = markers.map(m => {
                    if (!m || !m.getPosition()) return null;
                    return {
                        lat: m.getPosition().lat(),
                        lng: m.getPosition().lng(),
                        attributes: m.attributes || {}
                    };
                }).filter(p => p !== null);
                
                window.markerBackup.hasBackup = true;
                console.log('>>> JS: Backup completato: ' + window.markerBackup.points.length + ' punti salvati');
            } else {
                console.log('>>> JS: Nessun marker da salvare nel backup');
            }
            
            // Chiamata alla funzione originale
            originalClearAllLayers();
        };
        
        // Estendi la funzione forceMarkersVisible per utilizzare il backup se necessario
        const originalForceMarkersVisible = window.forceMarkersVisible;
        window.forceMarkersVisible = function() {
            console.log('>>> JS: FORZA VISIBILITÀ MARKER avanzata');
            
            // Se ci sono marker, usa la funzione originale
            if (markers && markers.length > 0) {
                console.log('>>> JS: Usando ' + markers.length + ' marker esistenti');
                originalForceMarkersVisible();
                return;
            }
            
            // Se non ci sono marker ma abbiamo un backup, ricrea i marker dal backup
            if (window.markerBackup && window.markerBackup.hasBackup && window.markerBackup.points.length > 0) {
                console.log('>>> JS: Ricreazione marker dal backup: ' + window.markerBackup.points.length + ' punti');
                
                // Assicurati che la modalità sia marker
                renderMode = 'markers';
                
                // Crea nuovi marker dal backup
                markers = [];
                window.markerBackup.points.forEach(point => {
                    try {
                        const marker = new google.maps.Marker({
                            position: { 
                                lat: Number(point.lat), 
                                lng: Number(point.lng) 
                            },
                            map: map,
                            optimized: false,
                            icon: getMeanVelIcon(point.attributes.MEAN_VEL),
                            visible: true,
                            zIndex: 100
                        });
                        
                        marker.attributes = point.attributes;
                        marker.addListener('click', function() {
                            showInfoWindow(this);
                        });
                        
                        markers.push(marker);
                    } catch(e) {
                        console.error('>>> JS: Errore ricreazione marker:', e);
                    }
                });
                
                // Applica clustering se abilitato
                if (isClusteringEnabled && markers.length > 0) {
                    setTimeout(() => enableClustering(), 200);
                }
                
                console.log('>>> JS: Ricreati ' + markers.length + ' marker dal backup');
                updateStats(`${markers.length} marker ripristinati dal backup`);
            } else {
                console.log('>>> JS: Nessun marker da forzare visibile e nessun backup disponibile');
            }
        };
        
        // Implementa un sistema di recupero automatico
        setInterval(function() {
            if (renderMode === 'markers' && (!markers || markers.length === 0) && 
                window.markerBackup && window.markerBackup.hasBackup && window.markerBackup.points.length > 0) {
                console.log('>>> JS: AUTO-RECUPERO - Marker mancanti, ripristino dal backup');
                forceMarkersVisible();
            }
        }, 5000);
    "

        Await WebView.ExecuteScriptAsync(preserveScript)
    End Function


    ' Aggiungi questo metodo alla classe
    Private Async Function ForceMarkersVisible() As Task
        ' Questa è una soluzione radicale per forzare i marker a essere visibili
        Dim forceVisibilityScript As String = "
        console.log('>>> JS: FORZATURA VISIBILITÀ MARKER RADICALE');
        
        // Prima verifica se abbiamo marker
        if (!markers || markers.length === 0) {
            console.log('>>> JS: Nessun marker trovato, verifico backup');
            
            // Se non ci sono marker ma abbiamo i dati originali in VB.NET, forzane la ricreazione
            window.chrome.webview.postMessage({
                type: 'recreateMarkers',
                action: 'force'
            });
            return;
        }
        
        try {
            // Metodo 1: Riapplica i marker alla mappa
            console.log('>>> JS: Forzatura visibilità di ' + markers.length + ' marker (metodo 1)');
            markers.forEach(function(marker) {
                if (marker) {
                    marker.setVisible(true);
                    marker.setMap(map);
                }
            });
            
            // Metodo 2: Disattiva e riattiva il clustering
            if (markersCluster) {
                console.log('>>> JS: Reset clustering (metodo 2)');
                markersCluster.clearMarkers();
                markersCluster = null;
            }
            
            if (isClusteringEnabled && markers.length > 0) {
                setTimeout(function() {
                    try {
                        enableClustering();
                    } catch(e) {
                        console.error('>>> JS: Errore enableClustering:', e);
                    }
                }, 300);
            }
            
            // Metodo 3: Forza refresh completo della mappa
            console.log('>>> JS: Forza refresh mappa (metodo 3)');
            google.maps.event.trigger(map, 'resize');
            
            // Metodo 4: Piccolo movimento della mappa per forzare il ridisegno
            setTimeout(function() {
                const center = map.getCenter();
                map.panBy(1, 0);
                setTimeout(function() {
                    map.panBy(-1, 0);
                    setTimeout(function() {
                        map.setCenter(center);
                    }, 100);
                }, 100);
            }, 400);
            
            // Metodo 5: Aggiorna tutti i marker con icone nuove
            setTimeout(function() {
                console.log('>>> JS: Aggiornamento icone marker (metodo 5)');
                markers.forEach(function(marker) {
                    if (marker && marker.attributes && marker.attributes.MEAN_VEL !== undefined) {
                        const newIcon = getMeanVelIcon(marker.attributes.MEAN_VEL);
                        marker.setIcon(newIcon);
                    }
                });
            }, 700);
            
            // Aggiorna UI con il numero di marker visibili
            setTimeout(function() {
                const visibleMarkers = markers.filter(m => m && m.getMap() === map).length;
                updateStats(`${markers.length} marker sulla mappa (${visibleMarkers} visibili)`);
                console.log('>>> JS: Verifica finale - ' + visibleMarkers + '/' + markers.length + ' marker visibili');
            }, 1000);
            
        } catch(e) {
            console.error('>>> JS: Errore nella forzatura visibilità:', e);
            // Ultimo tentativo: ricrea i marker da zero
            window.chrome.webview.postMessage({
                type: 'recreateMarkers',
                action: 'force'
            });
        }
    "
    
    Await WebView.ExecuteScriptAsync(forceVisibilityScript)
    End Function


    ' === CARICAMENTO DATI DA ORACLE DB ===
    Private Async Sub BtnCaricaDaDB_Click(sender As Object, e As RoutedEventArgs)
        ' Chiedi all'utente il numero massimo di record da caricare
        Dim input As String = InputBox("Inserisci il numero massimo di record da caricare:" & vbCrLf &
                              "(Valore consigliato: 5000-20000)",
                              "Limite Record", _maxRecordLimit.ToString())

        ' Se l'utente annulla, mantieni il valore attuale
        If String.IsNullOrWhiteSpace(input) Then
            Return
        End If

        ' Valida l'input
        Dim newLimit As Integer
        If Not Integer.TryParse(input, newLimit) OrElse newLimit <= 0 Then
            MessageBox.Show("Inserire un valore numerico positivo.", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
            Return
        End If

        ' Avvisa se il valore è molto grande
        If newLimit > 50000 Then
            Dim result = MessageBox.Show($"Attenzione: hai richiesto di caricare fino a {newLimit} record." & vbCrLf &
                                "Valori molto alti potrebbero rallentare l'applicazione." & vbCrLf &
                                "Vuoi continuare?",
                                "Avviso", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            If result = MessageBoxResult.No Then
                Return
            End If
        End If

        ' Imposta il nuovo limite
        _maxRecordLimit = newLimit
        Await WebView.ExecuteScriptAsync($"markerRenderLimit = {_maxRecordLimit};")

        ' Disabilita temporaneamente eventi viewChanged
        Await WebView.ExecuteScriptAsync("
        console.log('>>> JS: Disabilito temporaneamente notifiche viewport per caricamento iniziale');
        window.tempDisableViewNotifications = true;
        window.lastQueryTime = new Date().getTime();
    ")

        ' Continua con il resto del metodo
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture
        Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture
        _loadingData = True
        Dim hideLoadingAfterError As Boolean = False
        Dim startTime As DateTime = DateTime.Now

        Try
            Await WebView.ExecuteScriptAsync("showLoading('Connessione al database Oracle...')")

            Dim dbHelper As New OracleConnectionHelper()
            Using conn = dbHelper.GetConnection()
                Await conn.OpenAsync()
                If conn.State <> ConnectionState.Open Then
                    MessageBox.Show("Connessione al database Oracle non riuscita.", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
                    Return
                End If
            End Using

            ' Calcola la dimensione della griglia per l'aggregazione
            Dim extent As Envelope = Nothing
            Dim gridSize As Integer = CalcolaGridSizeDaExtent(extent)

            ' Costruisci la query
            Dim query = "SELECT " &
                    "ROUND(LATITUDE * " & gridSize & ") / " & gridSize & " AS LATITUDE, " &
                    "ROUND(LONGITUDE * " & gridSize & ") / " & gridSize & " AS LONGITUDE, " &
                    "CAST(AVG(NVL(MEAN_VEL,0)) AS NUMBER(10,5)) AS MEAN_VEL, " &
                    "COUNT(*) AS NUM_PUNTI " &
                    "FROM GEO_DATI " &
                    "GROUP BY ROUND(LATITUDE * " & gridSize & ") / " & gridSize & ", ROUND(LONGITUDE * " & gridSize & ") / " & gridSize

            Dim parameters As New Dictionary(Of String, Object)

            Await WebView.ExecuteScriptAsync($"updateLoading('Esecuzione query iniziale con limite di {_maxRecordLimit} record...')")
            Dim dt = Await Task.Run(Function() dbHelper.ExecuteQuery(query, parameters))

            If dt.Rows.Count = 0 Then
                MessageBox.Show("Nessun dato trovato nel database.", "Informazione", MessageBoxButton.OK, MessageBoxImage.Information)
                Return
            End If

            Await WebView.ExecuteScriptAsync($"updateLoading('Elaborazione {dt.Rows.Count} record...')")
            _csvData.Clear()

            ' Utilizzare un contatore per mostrare l'avanzamento
            Dim counter = 0
            Dim validPoints = 0
            Dim totalRows = dt.Rows.Count
            Dim maxBatchSize = 1000 ' Aggiorna l'UI ogni 1000 record elaborati

            For Each row As DataRow In dt.Rows
                counter += 1

                ' Aggiorna l'interfaccia ogni tot righe per mostrare l'avanzamento
                If counter Mod maxBatchSize = 0 Then
                    Await WebView.ExecuteScriptAsync($"updateLoading('Elaborazione record: {counter}/{totalRows} ({validPoints} punti validi)')")
                    Await Task.Delay(1) ' Breve pausa per aggiornare l'UI
                End If

                Dim lat As Double, lon As Double
                If Double.TryParse(row("LATITUDE").ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, lat) AndAlso
               Double.TryParse(row("LONGITUDE").ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, lon) Then

                    Dim point As New CsvPoint With {
                    .Latitude = Convert.ToDouble(lat.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
                    .Longitude = Convert.ToDouble(lon.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
                }

                    ' Aggiungi TUTTI gli attributi disponibili con formattazione corretta
                    For Each col As DataColumn In dt.Columns
                        If col.ColumnName = "MEAN_VEL" Then
                            ' Assicurati che MEAN_VEL sia un numero correttamente formattato
                            Dim meanVal As Double
                            If Double.TryParse(row(col).ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, meanVal) Then
                                point.Attributes(col.ColumnName) = meanVal.ToString(CultureInfo.InvariantCulture)
                            Else
                                point.Attributes(col.ColumnName) = "0"
                            End If
                        Else
                            point.Attributes(col.ColumnName) = row(col).ToString()
                        End If
                    Next

                    ' Aggiungi manualmente anche gli attributi D2* per il grafico (se non presenti, verranno aggiunti vuoti)
                    For i As Integer = 1 To 20  ' Assumendo che ci siano D201 a D220
                        Dim colName As String = $"D2{i.ToString("00")}"
                        If Not dt.Columns.Contains(colName) Then
                            ' Non fare nulla se la colonna non esiste
                        ElseIf Not point.Attributes.ContainsKey(colName) Then
                            ' Aggiungi l'attributo solo se non è già stato aggiunto
                            Dim d2Value As String = row(colName).ToString()
                            Dim d2Double As Double
                            If Double.TryParse(d2Value, NumberStyles.Any, CultureInfo.InvariantCulture, d2Double) Then
                                point.Attributes(colName) = d2Double.ToString(CultureInfo.InvariantCulture)
                            Else
                                point.Attributes(colName) = "0"
                            End If
                        End If
                    Next

                    _csvData.Add(point)
                    validPoints += 1
                End If
            Next

            _dbLoaded = True
            Dim elapsedTime = DateTime.Now.Subtract(startTime).TotalSeconds

            ' MODIFICA: Imposta _isFirstLoad = false per evitare ricaricamenti automatici 
            ' che potrebbero cancellare i dati appena caricati
            _isFirstLoad = False

            Await WebView.ExecuteScriptAsync($"updateLoading('Visualizzazione di {validPoints} punti (elaborazione completata in {elapsedTime:F1} secondi)...')")
            Await WebView.ExecuteScriptAsync("isClusteringEnabled = true;")

            ' Visualizza i punti
            Await VisualizzaPuntiSuMappa()

            ' AGGIUNTA: Blocca temporaneamente le notifiche di cambio viewport per evitare ricaricamenti
            ' immediati che potrebbero cancellare i dati appena visualizzati
            Await WebView.ExecuteScriptAsync("
            console.log('>>> JS: Blocco ricaricamenti per 10 secondi dopo caricamento iniziale');
            const originalNotifyBoundsChanged = notifyBoundsChanged;
            
            notifyBoundsChanged = function() {
                console.log('>>> JS: Evento viewport ignorato durante il periodo di blocco');
                // Non fare nulla durante il periodo di blocco
            };
            
            // Riattiva la funzione originale dopo 10 secondi
            setTimeout(function() {
                console.log('>>> JS: Ripristino notifiche viewport dopo blocco iniziale');
                notifyBoundsChanged = originalNotifyBoundsChanged;
                window.tempDisableViewNotifications = false;
            }, 10000);
        ")

        Catch ex As Exception
            MessageBox.Show($"Errore durante il caricamento dal database: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
            hideLoadingAfterError = True
        Finally
            _loadingData = False
            If hideLoadingAfterError Then
                Task.Run(Async Function()
                             Await Task.Delay(100)
                             Await Dispatcher.InvokeAsync(Async Function()
                                                              Await WebView.ExecuteScriptAsync("hideLoading()")
                                                          End Function)
                         End Function)
            End If
        End Try
    End Sub

    ' === UTILITY: Calcolo dimensione griglia per clustering ===
    Private Function CalcolaGridSizeDaExtent(extent As Envelope) As Integer
        If extent Is Nothing Then Return 1000
        Dim larghezza = extent.Width
        If larghezza > 2000000 Then
            Return 1000
        ElseIf larghezza > 200000 Then
            Return 100
        ElseIf larghezza > 20000 Then
            Return 10
        Else
            Return 0
        End If
    End Function

    ' Aggiungiamo questa funzione per garantire la formattazione corretta ovunque
    Private Function ToJsDouble(value As Double) As String
        Return value.ToString("0.000000", CultureInfo.InvariantCulture)
    End Function

    ' === CARICAMENTO DATI DB CON FILTRO GEOGRAFICO ===

    Private Async Sub CaricaDatiDaDB(bounds As Newtonsoft.Json.Linq.JObject, zoom As Double)
        Debug.WriteLine($">>> INIZIO CaricaDatiDaDB - Zoom: {zoom}, _loadingData: {_loadingData}")

        If _loadingData Then
            Debug.WriteLine(">>> USCITA ANTICIPATA: _loadingData già true")
            Return
        End If

        _loadingData = True
        Dim hideLoadingAfterError As Boolean = False
        ' Salva i dati correnti per poterli ripristinare se necessario
        Dim originalCsvData As New List(Of CsvPoint)(_csvData)
        Dim hadDataBefore As Boolean = _csvData.Count > 0

        Try
            Await WebView.ExecuteScriptAsync("showLoading('Caricamento dati per l\\'area visualizzata...');")
            Await WebView.ExecuteScriptAsync("console.log('>>> JS: Inizio caricamento dati per area visualizzata');")

            ' Salva una copia globale permanente della funzione notifyBoundsChanged
            Await WebView.ExecuteScriptAsync("
            if (!window.savedNotifyBoundsChanged) {
                window.savedNotifyBoundsChanged = notifyBoundsChanged;
                console.log('>>> JS: Funzione notifyBoundsChanged salvata globalmente');
            }
        ")

            Dim dbHelper As New OracleConnectionHelper()
            Dim query As String
            Dim parameters As New Dictionary(Of String, Object)

            ' Estrai i confini dell'area visibile
            Dim north = Convert.ToDouble(bounds("north"))
            Dim east = Convert.ToDouble(bounds("east"))
            Dim south = Convert.ToDouble(bounds("south"))
            Dim west = Convert.ToDouble(bounds("west"))

            ' Calcola l'area visualizzata
            Dim width = Math.Abs(east - west)
            Dim height = Math.Abs(north - south)
            Dim area = width * height

            ' Log dettagliato dei parametri per debug
            Debug.WriteLine($">>> Area di ricerca: North={north}, East={east}, South={south}, West={west}")
            Debug.WriteLine($">>> Dimensioni area: Width={width}, Height={height}, Area={area}")

            ' Scegli la query in base al livello di zoom
            If zoom >= 15 Then
                ' QUERY DETTAGLIATA per zoom elevato - recupera singoli punti
                query = "SELECT LATITUDE, LONGITUDE, MEAN_VEL, 1 AS NUM_PUNTI " &
               "FROM GEO_DATI " &
               "WHERE LATITUDE BETWEEN :south AND :north " &
               "AND LONGITUDE BETWEEN :west AND :east " &
               "AND ROWNUM <= :maxRecords"

                parameters.Add(":south", south)
                parameters.Add(":north", north)
                parameters.Add(":west", west)
                parameters.Add(":east", east)
                parameters.Add(":maxRecords", _maxRecordLimit)

                Debug.WriteLine($"Eseguo query dettagliata (zoom {zoom}): {query}")
                Await WebView.ExecuteScriptAsync($"updateLoading('Caricamento punti individuali (zoom {zoom})...');")
            Else
                ' QUERY AGGREGATA per zoom basso - raggruppa i punti
                Dim gridSize = If(zoom < 8, 1000, If(zoom < 12, 100, 10))

                query = "SELECT " &
               "ROUND(LATITUDE * " & gridSize & ") / " & gridSize & " AS LATITUDE, " &
               "ROUND(LONGITUDE * " & gridSize & ") / " & gridSize & " AS LONGITUDE, " &
               "CAST(AVG(NVL(MEAN_VEL,0)) AS NUMBER(10,5)) AS MEAN_VEL, " &
               "COUNT(*) AS NUM_PUNTI " &
               "FROM GEO_DATI " &
               "WHERE LATITUDE BETWEEN :south AND :north " &
               "AND LONGITUDE BETWEEN :west AND :east " &
               "GROUP BY ROUND(LATITUDE * " & gridSize & ") / " & gridSize & ", " &
               "ROUND(LONGITUDE * " & gridSize & ") / " & gridSize

                parameters.Add(":south", south)
                parameters.Add(":north", north)
                parameters.Add(":west", west)
                parameters.Add(":east", east)

                Debug.WriteLine($"Eseguo query aggregata (zoom {zoom}, gridSize {gridSize}): {query}")
                Await WebView.ExecuteScriptAsync($"updateLoading('Caricamento dati aggregati (zoom {zoom})...');")
            End If

            ' Esegui la query
            Dim dt = Await Task.Run(Function() dbHelper.ExecuteQuery(query, parameters))

            If dt.Rows.Count = 0 Then
                Debug.WriteLine(">>> Query non ha restituito risultati")

                ' MODIFICA IMPORTANTE: invece di pulire e uscire, manteniamo i dati esistenti
                If hadDataBefore Then
                    Debug.WriteLine(">>> Mantengo i dati esistenti (" & originalCsvData.Count & " punti)")
                    Await WebView.ExecuteScriptAsync($"updateLoading('Nessun nuovo dato trovato nell\\'area visualizzata. Mantengo dati esistenti.');")
                    Await Task.Delay(1500)
                    _loadingData = False
                    Await WebView.ExecuteScriptAsync("hideLoading();")

                    ' AGGIUNGI: Forza nuovamente la visibilità dei marker esistenti
                    Await WebView.ExecuteScriptAsync("
                    if (renderMode === 'markers' && markers && markers.length > 0) {
                        console.log('>>> JS: Forza visibilità marker esistenti');
                        markers.forEach(m => { 
                            if (m) {
                                m.setVisible(true);
                                m.setMap(map);
                            }
                        });
                        
                        if (isClusteringEnabled) {
                            setTimeout(() => enableClustering(), 200);
                        }
                    }
                ")

                    ' Nessuna operazione aggiuntiva, manteniamo i dati esistenti e la visualizzazione corrente
                    Return
                Else
                    ' Solo se non avevamo dati prima, mostriamo il messaggio e usciamo
                    Await WebView.ExecuteScriptAsync($"updateLoading('Nessun dato trovato nell\\'area visualizzata');")
                    Await Task.Delay(1500)
                    _loadingData = False
                    Await WebView.ExecuteScriptAsync("hideLoading();")
                    Return
                End If
            End If

            Debug.WriteLine($">>> Query completata, elaborazione di {dt.Rows.Count} record")
            Await WebView.ExecuteScriptAsync($"updateLoading('Elaborazione {dt.Rows.Count} record...');")
            _csvData.Clear()

            ' Elabora i risultati della query
            Dim counter = 0
            Dim validPoints = 0
            Dim totalRows = dt.Rows.Count
            Dim maxBatchSize = 1000 ' Aggiorna l'UI ogni 1000 record elaborati

            For Each row As DataRow In dt.Rows
                counter += 1

                ' Aggiorna l'interfaccia periodicamente
                If counter Mod maxBatchSize = 0 Then
                    Await WebView.ExecuteScriptAsync($"updateLoading('Elaborazione record: {counter}/{totalRows} ({validPoints} punti validi)');")
                    Await Task.Delay(1) ' Breve pausa per aggiornare l'UI
                End If

                ' Elabora ogni record
                Dim lat As Double, lon As Double
                If Double.TryParse(row("LATITUDE").ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, lat) AndAlso
           Double.TryParse(row("LONGITUDE").ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, lon) Then

                    Dim point As New CsvPoint With {
                .Latitude = lat,
                .Longitude = lon
            }

                    ' Aggiungi tutti gli attributi disponibili
                    For Each col As DataColumn In dt.Columns
                        If col.ColumnName = "MEAN_VEL" Then
                            ' Assicurati che MEAN_VEL sia un numero correttamente formattato
                            Dim meanVal As Double
                            If Double.TryParse(row(col).ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, meanVal) Then
                                point.Attributes(col.ColumnName) = meanVal.ToString(CultureInfo.InvariantCulture)
                            Else
                                point.Attributes(col.ColumnName) = "0"
                            End If
                        Else
                            point.Attributes(col.ColumnName) = row(col).ToString()
                        End If
                    Next

                    _csvData.Add(point)
                    validPoints += 1
                End If
            Next

            _dbLoaded = True
            Debug.WriteLine($">>> Elaborazione record completata, punti validi: {validPoints}")

            ' Visualizza punti sulla mappa senza modificare il viewport
            If _csvData.Count > 0 Then
                Debug.WriteLine($">>> Prima di disabilitare notifiche, punti da visualizzare: {_csvData.Count}")

                ' NUOVA VERSIONE: usa la copia globale salvata in precedenza
                Dim disableNotifyScript As String = "
                console.log('>>> JS: Inizio disabilitazione notifiche viewport');
                try {
                    window.originalNotifyBoundsChangedFunc = window.savedNotifyBoundsChanged;
                    notifyBoundsChanged = function() { 
                        console.log('>>> JS: Notifica viewport DISABILITATA temporaneamente');
                    };
                    console.log('>>> JS: Notifiche disabilitate con successo');
                } catch(err) {
                    console.error('>>> JS: Errore durante disabilitazione notifiche:', err);
                }
            "

                Await WebView.ExecuteScriptAsync(disableNotifyScript)

                ' Attendiamo un attimo per essere sicuri che la disabilitazione sia completata
                Await Task.Delay(100)

                Debug.WriteLine(">>> Prima di VisualizzaPuntiSenzaAdjustViewport")
                Await WebView.ExecuteScriptAsync("isClusteringEnabled = true;")
                Await VisualizzaPuntiSenzaAdjustViewport()
                Debug.WriteLine(">>> Dopo VisualizzaPuntiSenzaAdjustViewport")

                ' Attendiamo un po' più a lungo prima di ripristinare le notifiche
                Await Task.Delay(1000)
                Debug.WriteLine(">>> Prima di ripristinare notifiche")

                ' NUOVA VERSIONE: usa la copia globale per ripristino più affidabile
                Dim restoreNotifyScript As String = "
                console.log('>>> JS: Ripristino notifiche viewport');
                try {
                    if (window.savedNotifyBoundsChanged) {
                        notifyBoundsChanged = window.savedNotifyBoundsChanged;
                        console.log('>>> JS: Funzione notifyBoundsChanged ripristinata correttamente');
                    } else if (window.originalNotifyBoundsChangedFunc) {
                        notifyBoundsChanged = window.originalNotifyBoundsChangedFunc;
                        console.log('>>> JS: Funzione notifyBoundsChanged ripristinata da backup locale');
                    } else {
                        console.warn('>>> JS: ATTENZIONE! Nessuna funzione di backup trovata!');
                        console.log('>>> JS: Ricreazione funzione notifyBoundsChanged');
                        
                        notifyBoundsChanged = function() {
                            if (!isMapReady) return;
                            
                            try {
                                if (map && map.getBounds()) {
                                    var currentBounds = map.getBounds();
                                    var zoom = map.getZoom();
                                    
                                    window.chrome.webview.postMessage({
                                        type: 'viewChanged',
                                        bounds: {
                                            north: currentBounds.getNorthEast().lat(),
                                            east: currentBounds.getNorthEast().lng(),
                                            south: currentBounds.getSouthWest().lat(),
                                            west: currentBounds.getSouthWest().lng()
                                        },
                                        zoom: zoom
                                    });
                                }
                            } catch(e) {
                                console.error('>>> JS: Errore nella funzione notifyBoundsChanged ricreata:', e);
                            }
                        };
                    }
                    
                    // AGGIUNTI: Forza visibilità marker e ripristina la mappa 
                    if (renderMode === 'markers' && markers && markers.length > 0) {
                        console.log('>>> JS: Forzatura visualizzazione ' + markers.length + ' marker dopo ripristino notifiche');
                        markers.forEach(function(m) {
                            if (m) {
                                m.setVisible(true);
                                m.setMap(map);
                            }
                        });
                        
                        if (isClusteringEnabled) {
                            console.log('>>> JS: Riapplicazione clustering');
                            setTimeout(function() { enableClustering(); }, 200);
                        }
                    }
                    
                    // Forza resize per aggiornare visualizzazione
                    setTimeout(function() {
                        google.maps.event.trigger(map, 'resize');
                        console.log('>>> JS: Forzato resize mappa');
                    }, 300);
                } catch(err) {
                    console.error('>>> JS: Errore grave nel ripristino delle notifiche:', err);
                }
            "

                Await WebView.ExecuteScriptAsync(restoreNotifyScript)

                Debug.WriteLine(">>> Dopo ripristino notifiche")
                Await WebView.ExecuteScriptAsync($"updateStats('Visualizzati {validPoints} punti" &
                                $"{If(zoom >= 15, " (dettagliati)", " (aggregati)")}" &
                                $" - Zoom: {zoom}');")
            End If

        Catch ex As Exception
            Debug.WriteLine($">>> ERRORE in CaricaDatiDaDB: {ex.Message}")
            Debug.WriteLine($"Stack trace: {ex.StackTrace}")
            MessageBox.Show($"Errore durante il caricamento dei dati: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
            hideLoadingAfterError = True

            ' MODIFICA: Se c'è un errore, ripristina i dati originali
            If hadDataBefore Then
                Debug.WriteLine(">>> Ripristino i dati originali dopo un errore")
                _csvData = originalCsvData
            End If
        Finally
            Debug.WriteLine(">>> FINE CaricaDatiDaDB, resetto _loadingData = false")
            _loadingData = False
            If hideLoadingAfterError Then
                HideLoadingIndicator()
            End If
        End Try
    End Sub


    ' Metodo separato per nascondere l'indicatore di caricamento
    Private Async Sub HideLoadingIndicator()
        Try
            Await WebView.ExecuteScriptAsync("hideLoading();")
        Catch ex As Exception
            Debug.WriteLine($"Errore nel nascondere l'indicatore di caricamento: {ex.Message}")
        End Try
    End Sub
    ' funzione per scegliere il modo migliore di visualizzare i punti
    'Private Async Sub VisualizzaPuntiOttimizzati()
    '    If _csvData.Count < 1000 Then
    '        ' Usa il metodo standard per piccoli set di dati
    '        Await VisualizzaPuntiSuMappa()
    '    Else
    '        ' Usa il rendering WebGL per grandi set di dati
    '        Await VisualizzaPuntiWebGL()
    '    End If
    'End Sub


    ' Nuovo metodo per visualizzare grandi set di dati
    ' Private Async Function VisualizzaPuntiWebGL() As Task
    '     Try
    '         ' Converte i dati in formato più compatto
    '         Dim puntiSemplificati = _csvData.Select(Function(p) New With {
    '         .lat = p.Latitude,
    '         .lng = p.Longitude,
    '         .val = If(p.Attributes.ContainsKey("MEAN_VEL"), p.Attributes("MEAN_VEL"), 0)
    '     }).ToList()
    '
    '         Dim jsonPoints = JsonConvert.SerializeObject(puntiSemplificati)
    '
    '         ' Usa un metodo di rendering più efficiente per grandi dataset
    '         Await WebView.ExecuteScriptAsync($"renderPointsWebGL({jsonPoints})")
    '     Catch ex As Exception
    '         MessageBox.Show($"Errore durante la visualizzazione dei punti: {ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
    '     End Try
    ' End Function

    ' === PULIZIA MAPPA E RESET ===
    Private Async Sub BtnPulisciMappa_Click(sender As Object, e As RoutedEventArgs)
        _csvData.Clear()
        _dbLoaded = False

        If _webViewInitialized Then
            ' Pulisci tutti i layer
            Await WebView.ExecuteScriptAsync("clearAllLayers();")
            ' Resetta clustering e stato frontend
            Await WebView.ExecuteScriptAsync("isClusteringEnabled = true;")
            ' Invia un array vuoto per forzare il reset di marker, cluster e colori
            Await WebView.ExecuteScriptAsync("addPoints([]);")
        End If

        MessageBox.Show("Mappa pulita. Tutti i dati e lo stato sono stati resettati.", "Info", MessageBoxButton.OK, MessageBoxImage.Information)
    End Sub

    ' === VISUALIZZAZIONE INFO PUNTO ===
    Private Sub ShowPointInfo(data As Newtonsoft.Json.Linq.JObject)
        Try
            Dim attributes = data("attributes").ToObject(Of Dictionary(Of String, Object))
            Dim info As New StringBuilder()

            info.AppendLine("---- Attributi record ----")

            For Each kvp In attributes
                If Not kvp.Key.StartsWith("D2", StringComparison.OrdinalIgnoreCase) Then
                    info.AppendLine($"{kvp.Key}: {kvp.Value}")
                End If
            Next

            ' Mostra popup con attributi normali
            MessageBox.Show(info.ToString(), "Dettaglio punto", MessageBoxButton.OK, MessageBoxImage.Information)
        Catch ex As Exception
            Debug.WriteLine($"Errore nella visualizzazione delle informazioni del punto: {ex.Message}")
        End Try
    End Sub

    ' === VISUALIZZAZIONE GRAFICO DS20 ===
    Private Sub ShowDS20Chart(attributes As Newtonsoft.Json.Linq.JObject)
        Try
            Dim ds20Values As New List(Of Double)
            Dim ds20Labels As New List(Of String)

            For Each prop In attributes.Properties()
                If prop.Name.StartsWith("D2", StringComparison.OrdinalIgnoreCase) Then
                    Dim val As Double
                    If Double.TryParse(prop.Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, val) Then
                        If Not Double.IsNaN(val) AndAlso Not Double.IsInfinity(val) Then
                            ds20Labels.Add(prop.Name)
                            ds20Values.Add(val)
                        End If
                    End If
                End If
            Next

            If ds20Values.Count > 0 AndAlso ds20Labels.Count = ds20Values.Count Then
                Dim chartWindow As New Ds20ChartWindow(ds20Labels, ds20Values)
                chartWindow.Owner = Me
                chartWindow.ShowDialog()
            Else
                MessageBox.Show("Nessun dato di serie temporale valido per questo punto.", "Informazione", MessageBoxButton.OK, MessageBoxImage.Information)
            End If
        Catch ex As Exception
            Debug.WriteLine($"Errore nella visualizzazione del grafico DS20: {ex.Message}")
            MessageBox.Show("Errore nella visualizzazione del grafico DS20: " & ex.Message, "Errore", MessageBoxButton.OK, MessageBoxImage.Error)
        End Try
    End Sub
    ' === UTILITY: Trova indice colonna per assonanza ===
    Private Function TrovaIndiceColonna(headers As String(), possibiliNomi As String()) As Integer
        For i = 0 To headers.Length - 1
            Dim nome = headers(i).Trim().ToLowerInvariant()
            For Each possibile In possibiliNomi
                If nome.Contains(possibile) Then Return i
            Next
        Next
        Return -1
    End Function

    ' === UTILITY: Helper per assegnazione inline in While ===
    Private Function InlineAssignHelper(Of T)(ByRef target As T, value As T) As T
        target = value
        Return value
    End Function
End Class