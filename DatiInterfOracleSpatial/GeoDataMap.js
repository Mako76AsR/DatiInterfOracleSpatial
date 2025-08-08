/**
 * GeoDataMap.js - Gestione mappa Google Maps con overlay WebGL (Deck.gl)
 * Implementa la visualizzazione di dati GIS su mappa con tecnologia WebGL.
 * Versione: 2.2 - Architettura stabile basata su listener API nativi
 */

// ===== VARIABILI GLOBALI =====
let map;                          // Istanza principale Google Maps
let dataBounds = null;            // Confini geografici dei dati visualizzati
let clickPreventionActive = false; // Flag per bloccare interazioni problematiche
let highlightOverlay = null;      // Overlay per evidenziare punti selezionati
let lastEventTimestamp = 0;       // Timestamp per controllo debounce eventi
const debugMessages = [];         // Buffer messaggi di debug
const EVENT_THROTTLE_MS = 150;    // Millisecondi per throttling eventi frequenti

// Cache per migliorare le performance
const colorCache = new Map();     // Cache dei colori calcolati

// ===== INIZIALIZZAZIONE E CONFIGURAZIONE =====

/**
 * Funzione principale di inizializzazione dell'applicazione
 * Eseguita automaticamente al caricamento della pagina
 */
function initializeApplication() {
    setupErrorHandlers();
    setupDOMEventListeners();
    loadGoogleMapsAPI();
    logToConsole('Inizializzazione applicazione completata');
}

/**
 * Configura gestori globali per intercettare e gestire errori
 */
function setupErrorHandlers() {
    // Intercetta errori globali non gestiti
    window.addEventListener('error', function (e) {
        // Gestione speciale per errori di assertion di deck.gl che possono causare crash
        if (e.message && e.message.includes('assertion failed')) {
            logToConsole("INTERCETTATO: Errore di assertion deck.gl: " + e.message);
            disableInteractions(); // Previene ulteriori interazioni problematiche
            e.preventDefault();
            e.stopPropagation();
            return false;
        }
    }, true);

    // Intercetta Promise non gestite
    window.addEventListener('unhandledrejection', function (e) {
        logToConsole("INTERCETTATO: Promise non gestita: " + e.reason);
        if (e.reason && e.reason.toString().includes('deck.gl')) {
            e.preventDefault();
            disableInteractions();
        }
    });
}

/**
 * Configura i listener per eventi DOM a livello globale
 */
function setupDOMEventListeners() {
    // Listener globale per bloccare interazioni quando necessario
    document.addEventListener('click', function (e) {
        if (clickPreventionActive) {
            e.stopPropagation();
            e.preventDefault();
            logToConsole("Click bloccato a livello DOM (prevenzione attiva)");
            return false;
        }
    }, true); // true = fase di cattura, prima di qualsiasi altro listener
}

/**
 * Carica l'API Google Maps in modo asincrono
 */
function loadGoogleMapsAPI() {
    logToConsole('Caricamento API Google Maps in corso...');
    var script = document.createElement('script');
    script.src = 'https://maps.googleapis.com/maps/api/js?key=AIzaSyAeleu3f7WRCB8m62b3R6MQH-RQmY_ZfNI&callback=initMap';
    script.async = true;
    script.defer = true;
    script.onerror = function () {
        window.chrome.webview.postMessage({
            action: 'error',
            message: 'Impossibile caricare Google Maps API'
        });
    };
    document.head.appendChild(script);
}

/**
 * Funzione callback chiamata da Google Maps API dopo il caricamento
 * Inizializza la mappa e configura i vari listener per eventi
 */
window.initMap = function () {
    logToConsole('initMap chiamato');
    try {
        // Creazione istanza mappa
        map = new google.maps.Map(document.getElementById('map'), {
            center: { lat: 41.9028, lng: 12.4964 }, // Roma come centro predefinito
            zoom: 6,
            mapTypeId: 'terrain'
        });

        logToConsole('Mappa creata: ' + (map ? 'OK' : 'NO'));

        // Configurazione dei listener per gli eventi della mappa
        setupMapEventListeners();

        // Configurazione gestore click personalizzato usando l'API di Google Maps
        setupCustomClickHandler();

        // Notifica che la mappa è pronta
        window.chrome.webview.postMessage({ action: 'map_ready' });
    } catch (error) {
        window.chrome.webview.postMessage({
            action: 'error',
            message: 'Errore in initMap: ' + error.message
        });
    }
};

/**
 * Configura i listener per gli eventi standard della mappa
 */
function setupMapEventListeners() {
    // Evento cambio zoom con throttling
    map.addListener('zoom_changed', throttle(function () {
        window.chrome.webview.postMessage({
            action: 'zoom_changed',
            zoom: map.getZoom(),
            bounds: map.getBounds() ? map.getBounds().toJSON() : null
        });
    }, EVENT_THROTTLE_MS));

    // Evento cambio bounds con throttling
    map.addListener('bounds_changed', throttle(function () {
        window.chrome.webview.postMessage({
            action: 'bounds_changed',
            zoom: map.getZoom(), // <-- AGGIUNTO!
            bounds: map.getBounds() ? map.getBounds().toJSON() : null
        });
    }, EVENT_THROTTLE_MS));

    // Evento mappa idle (quando si ferma)
    map.addListener('idle', function () {
        window.chrome.webview.postMessage({
            action: 'map_idle',
            zoom: map.getZoom(),
            bounds: map.getBounds() ? map.getBounds().toJSON() : null
        });
    });
}

/**
 * Configura un gestore di click personalizzato usando il listener nativo di Google Maps.
 * Questo è l'approccio più stabile per evitare errori di proiezione.
 */
function setupCustomClickHandler() {
    // Usa il listener ufficiale di Google Maps per ottenere le coordinate in modo affidabile
    map.addListener('click', function (event) {
        // event.latLng è un oggetto google.maps.LatLng, fornito direttamente dall'API
        const lat = event.latLng.lat();
        const lng = event.latLng.lng();
        logToConsole(`Click su mappa (API): ${lat}, ${lng}`);

        // Se sono stati definiti dei confini, controlla se il click è al loro interno
        if (dataBounds) {
            if (lat < dataBounds.south || lat > dataBounds.north ||
                lng < dataBounds.west || lng > dataBounds.east) {
                logToConsole(`Click fuori dai bounds dati. Evento ignorato.`);
                // Non fare nulla. L'eventuale errore di assertion di Deck.gl
                // verrà gestito da patchDeckGL().
                return;
            }
        }

        // Se il click è valido (o se non ci sono bounds), invia il messaggio
        window.chrome.webview.postMessage({
            action: 'map_click',
            latitude: lat,
            longitude: lng
        });
    });
}

/**
 * Patch deck.gl per prevenire che errori di assertion blocchino l'applicazione.
 * Questa funzione è utile per gestire i click fuori dai bounds dei dati.
 */
function patchDeckGL() {
    if (!window.deck) return;

    try {
        if (window.deck.assert) {
            const originalAssert = window.deck.assert;
            window.deck.assert = function (condition, message) {
                if (!condition) {
                    logToConsole("INTERCETTATO (innocuo): Errore assertion deck.gl evitato: " + message);
                    return; // Non lanciare l'errore
                }
                originalAssert.apply(this, arguments);
            };
        }
        logToConsole("deck.gl patchato con successo per maggiore stabilità.");
    } catch (e) {
        logToConsole("Errore nel patch di deck.gl: " + e.message);
    }
}


// ===== GESTIONE INTERAZIONI =====

/**
 * Disabilita temporaneamente tutte le interazioni per prevenire errori
 * durante eventi problematici (es. click rapidi).
 */
function disableInteractions() {
    clickPreventionActive = true;
    setTimeout(() => {
        clickPreventionActive = false;
    }, 200);
}

/**
 * Funzione throttle per limitare la frequenza di chiamata di funzioni
 */
function throttle(func, wait) {
    return function () {
        const now = Date.now();
        if (now - lastEventTimestamp >= wait) {
            lastEventTimestamp = now;
            func.apply(this, arguments);
        }
    };
}


// ===== CARICAMENTO LIBRERIE =====

let deckReadyPromise = null;
let deckLoadTimeout = null;

/**
 * Carica Deck.gl e GoogleMapsOverlay se non sono già disponibili
 * @returns {Promise} Promise che si risolve quando le librerie sono pronte
 */
function ensureDeckReady() {
    if (window.deck) return Promise.resolve();
    if (deckReadyPromise) return deckReadyPromise;

    if (deckLoadTimeout) clearTimeout(deckLoadTimeout);
    deckLoadTimeout = setTimeout(() => {
        if (!window.deck) {
            logToConsole("TIMEOUT nel caricamento di deck.gl, riprovo...");
            deckReadyPromise = null;
        }
    }, 10000);

    deckReadyPromise = new Promise((resolve, reject) => {
        const script1 = document.createElement('script');
        script1.src = 'https://unpkg.com/deck.gl@8.8.0/dist.min.js';
        script1.onload = () => {
            const script2 = document.createElement('script');
            script2.src = 'https://unpkg.com/@deck.gl/google-maps@8.8.0/dist.min.js';
            script2.onload = () => {
                logToConsole("Deck.gl e GoogleMapsOverlay caricati!");
                if (deckLoadTimeout) clearTimeout(deckLoadTimeout);
                patchDeckGL(); // Applica la patch subito dopo il caricamento
                resolve();
            };
            script2.onerror = (err) => {
                logToConsole("Errore caricamento @deck.gl/google-maps");
                if (deckLoadTimeout) clearTimeout(deckLoadTimeout);
                reject(err);
            };
            document.head.appendChild(script2);
        };
        script1.onerror = (err) => {
            logToConsole("Errore caricamento deck.gl");
            if (deckLoadTimeout) clearTimeout(deckLoadTimeout);
            reject(err);
        };
        document.head.appendChild(script1);
    });

    return deckReadyPromise;
}


// ===== OPERAZIONI SULLA MAPPA E OVERLAY =====

function fitBounds(bounds) {
    if (!map) return false;
    try {
        map.fitBounds(new google.maps.LatLngBounds(
            { lat: bounds.south, lng: bounds.west },
            { lat: bounds.north, lng: bounds.east }
        ));
        return true;
    } catch (error) {
        logToConsole("Errore in fitBounds: " + error.message);
        return false;
    }
}

function setDataBounds(bounds) {
    dataBounds = bounds;
    logToConsole("Bounds dati impostati: " + JSON.stringify(bounds));
}

function removeWebGLOverlay() {
    try {
        if (window.pointsOverlay) {
            window.pointsOverlay.setMap(null);
            window.pointsOverlay.finalize();
            window.pointsOverlay = null;
        }
       /* if (window.boundsOverlay) {
            window.boundsOverlay.setMap(null);
            window.boundsOverlay.finalize();
            window.boundsOverlay = null;
        } non voglio cancellare anche i bounds*/
        if (highlightOverlay) {
            highlightOverlay.setMap(null);
            highlightOverlay.finalize();
            highlightOverlay = null;
        }
        return true;
    } catch (e) {
        logToConsole("Errore in removeWebGLOverlay: " + e.message);
        return false;
    }
}

async function drawDeckBounds(boundsArray) {
    await ensureDeckReady();
    if (!window.deck) return;
    if (window.boundsOverlay) window.boundsOverlay.setMap(null);

    // Se l'input non è un array, lo converte in array
    if (!Array.isArray(boundsArray)) boundsArray = [boundsArray];

    try {
        window.boundsOverlay = new deck.GoogleMapsOverlay({
            layers: [
                new deck.PolygonLayer({
                    id: 'bounds-polygon-static',
                    data: boundsArray.map(bounds => ({
                        polygon: [
                            [bounds.west, bounds.north],
                            [bounds.east, bounds.north],
                            [bounds.east, bounds.south],
                            [bounds.west, bounds.south]
                        ]
                    })),
                    stroked: true,
                    filled: false,
                    pickable: false,
                    lineWidthMinPixels: 2,
                    getPolygon: d => d.polygon,
                    getFillColor: [255, 0, 0, 0],
                    getLineColor: [255, 0, 0, 200]
                })
            ]
        });
        window.boundsOverlay.setMap(map);
    } catch (e) {
        logToConsole("Errore nel creare boundsOverlay: " + e.message);
    }
}

async function createWebGLOverlay(puntiJSON, markerRadius, zoomLevel, bounds) {
    await ensureDeckReady();
    if (!window.deck) return false;
    if (window.pointsOverlay) window.pointsOverlay.setMap(null);

    try {
        let punti;
        try {
            if (puntiJSON.startsWith('"') && puntiJSON.endsWith('"')) {
                puntiJSON = puntiJSON.substring(1, puntiJSON.length - 1);
            }
            punti = JSON.parse(puntiJSON);
        } catch (e) {
            punti = eval('(' + puntiJSON + ')');
        }

        const timestamp = new Date().toISOString();
        window.pointsOverlay = new deck.GoogleMapsOverlay({
            layers: [
                new deck.ScatterplotLayer({
                    id: 'scatter-plot-' + timestamp,
                    data: punti,
                    pickable: true,
                    opacity: 0.8,
                    stroked: false,
                    filled: true,
                    radiusMinPixels: markerRadius,
                    radiusMaxPixels: markerRadius,
                    getPosition: d => [d[0], d[1]],
                    getFillColor: d => d[3] || [128, 128, 128, 200],
                    onClick: info => {
                        if (info && info.object) {
                            window.chrome.webview.postMessage({
                                action: 'marker_click',
                                latitude: info.object[1],
                                longitude: info.object[0]
                            });
                        }
                    }
                })
            ]
        });
        window.pointsOverlay.setMap(map);
        window.chrome.webview.postMessage({
            action: 'webgl_created',
            pointCount: punti.length
        });
        return true;
    } catch (e) {
        logToConsole("Errore critico in createWebGLOverlay: " + e.message);
        return false;
    }
}

function highlightPoint(latitude, longitude) {
    return ensureDeckReady().then(() => {
        if (highlightOverlay) highlightOverlay.setMap(null);
        const lat = parseFloat(latitude);
        const lng = parseFloat(longitude);
        if (isNaN(lat) || isNaN(lng)) return false;

        try {
            highlightOverlay = new deck.GoogleMapsOverlay({
                layers: [
                    new deck.ScatterplotLayer({
                        id: 'highlight-point',
                        data: [[lng, lat]],
                        pickable: false,
                        opacity: 1.0,
                        filled: true,
                        stroked: true,
                        radiusMinPixels: 12,
                        radiusMaxPixels: 12,
                        getPosition: d => d,
                        getFillColor: [255, 0, 0, 120],
                        getLineColor: [255, 255, 255, 255],
                        lineWidthMinPixels: 3
                    })
                ]
            });
            highlightOverlay.setMap(map);
            map.panTo({ lat: lat, lng: lng });
            return true;
        } catch (e) {
            logToConsole('Errore in highlightPoint: ' + e.message);
            return false;
        }
    });
}

function removeHighlightedPoint() {
    if (highlightOverlay) {
        highlightOverlay.setMap(null);
        highlightOverlay = null;
        return true;
    }
    return false;
}

function addGroundOverlay(imageUrl, bounds) {
    if (window.currentOverlay) window.currentOverlay.setMap(null);
    if (!map || !imageUrl || !bounds) return false;
    const overlay = new google.maps.GroundOverlay(imageUrl, {
        north: bounds.north, south: bounds.south, east: bounds.east, west: bounds.west, opacity: 0.8
    });
    overlay.setMap(map);
    window.currentOverlay = overlay;
    return true;
}

function removeGroundOverlays() {
    if (window.currentOverlay) {
        window.currentOverlay.setMap(null);
        window.currentOverlay = null;
        return true;
    }
    return false;
}



function showMapLoadingSpinner() {
    const el = document.getElementById('map-loading-spinner');
    if (el) {
        el.style.display = 'flex';
        logToConsole('Spinner VISIBILE');
    } else {
        logToConsole('Spinner NON TROVATO');
    }
}
function hideMapLoadingSpinner() {
    const el = document.getElementById('map-loading-spinner');
    if (el) el.style.display = 'none';
}
window.showMapLoadingSpinner = showMapLoadingSpinner;
window.hideMapLoadingSpinner = hideMapLoadingSpinner;

// ===== UTILITÀ =====
function logToConsole(message) {
    try {
        console.log(message);
        debugMessages.push(message);
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.postMessage({ action: 'debug', message: message });
        }
    } catch (e) {
        console.error("Errore nel logging: ", e);
    }
}

// ===== ESPORTAZIONE METODI PUBBLICI =====
window.fitBounds = fitBounds;
window.drawDeckBounds = drawDeckBounds;
window.createWebGLOverlay = createWebGLOverlay;
window.removeWebGLOverlay = removeWebGLOverlay;
window.addGroundOverlay = addGroundOverlay;
window.removeGroundOverlays = removeGroundOverlays;
window.highlightPoint = highlightPoint;
window.removeHighlightedPoint = removeHighlightedPoint;
window.setDataBounds = setDataBounds;

// ===== INIZIALIZZAZIONE =====
document.addEventListener('DOMContentLoaded', initializeApplication);