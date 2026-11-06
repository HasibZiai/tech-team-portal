// The site map viewer: a thin wrapper around Leaflet. All data and permissions are handled in C#;
// this file only draws what the server sends. Positions run 0 to 1 from the map's top-left corner.
/* global L */

// Label size as a share of the map's width, roughly matching a 9pt label on a tabloid plan.
const LABEL_SHARE = 0.0075;
const MIN_LABEL_PX = 11;
const MIN_NAME_PX = 11;

const SYMBOLS = {
    "ap-indoor": { shape: "star", color: "#4F94EF" },
    "ap-outdoor": { shape: "triangle", color: "#1FB524" },
    "ap-outdoor-577": { shape: "triangle", color: "#B8861E" },
    "mdf": { shape: "square", color: "#F09600", letter: "M" },
    "idf": { shape: "square", color: "#F5D800", letter: "I" },
    "photo": { shape: "camera", color: "#12315C" },
    "order": { shape: "box", color: "#0B7A75" },
};

const STAR = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" stroke="#1B2735" stroke-width="1" d="M12 2l3 6.6 7.2.8-5.3 4.8 1.5 7.1L12 17.7 5.6 21.3l1.5-7.1L1.8 9.4 9 8.6z"/></svg>';

// Custom legend shapes. Keep in step with MapShapes in SiteMaps.cs. The white edge keeps dark colors clear of walls.
const CUSTOM_SHAPES = {
    "circle": "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20z",
    "square": "M3 3h18v18H3z",
    "triangle": "M12 3l10 18H2z",
    "diamond": "M12 2l10 10-10 10L2 12z",
    "star": "M12 2l3 6.6 7.2.8-5.3 4.8 1.5 7.1L12 17.7 5.6 21.3l1.5-7.1L1.8 9.4 9 8.6z",
};

// Colors go into inline styles, so only plain hex values are allowed through.
function safeColor(color) {
    return /^#[0-9a-fA-F]{3,8}$/.test(color ?? "") ? color : "#1F6FC5";
}

// Symbols differ by shape as well as color, so they can be told apart without seeing color.
const CAMERA = '<svg class="map-pin-icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="11.5" fill="#fff" stroke="#12315C"/><path fill="currentColor" d="M9.2 6.5 8 8H5.5A1.5 1.5 0 0 0 4 9.5v7A1.5 1.5 0 0 0 5.5 18h13a1.5 1.5 0 0 0 1.5-1.5v-7A1.5 1.5 0 0 0 18.5 8H16l-1.2-1.5zM12 15.8a3 3 0 1 1 0-6 3 3 0 0 1 0 6z"/></svg>';
const BOX = '<svg class="map-pin-icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="11.5" fill="#fff" stroke="#0B7A75"/><path fill="currentColor" d="M12 4 5 7.5v9l7 3.5 7-3.5v-9zm0 2 4.2 2.1L12 10.2 7.8 8.1zM7 9.7l4 2v5.6l-4-2zm10 0v5.6l-4 2v-5.6z"/></svg>';
const TRIANGLE = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" stroke="#1B2735" stroke-width="1" d="M12 3l10 18H2z"/></svg>';

function escapeHtml(text) {
    const element = document.createElement("span");
    element.textContent = text ?? "";
    return element.innerHTML;
}

/** Returns false if the browser wouldn't allow it, which it refuses on pages without HTTPS. */
export async function copyText(text) {
    try {
        await navigator.clipboard.writeText(text);
        return true;
    } catch {
        return false;
    }
}

export function localDate() {
    return new Date().toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
}

export async function downloadFile(fileName, content) {
    const blob = new Blob([await content.arrayBuffer()], { type: "application/pdf" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}

export function create(element, imageUrl, width, height, layers, altText, dotNet) {
    const map = L.map(element, {
        crs: L.CRS.Simple,
        minZoom: -6,
        // One map unit is one image pixel, so zoom 0 is full resolution. Beyond that it just gets blurry.
        maxZoom: 0,
        zoomSnap: 0.25,
        zoomDelta: 0.5,
        wheelPxPerZoomLevel: 120,
        attributionControl: false,
    });

    const bounds = [[0, 0], [height, width]];
    L.imageOverlay(imageUrl, bounds, { className: "map-base-image", alt: altText }).addTo(map);
    map.fitBounds(bounds);

    const toLatLng = (x, y) => [height * (1 - y), width * x];
    const fromLatLng = (latLng) => ({ x: latLng.lng / width, y: 1 - latLng.lat / height });
    const groups = new Map();
    const markers = new Map();
    let moveMode = false;
    let selectedId = null;
    let placing = false;

    const redraw = (entry) => entry.item.setIcon(markerIcon(entry.feature, entry.layer, entry.feature.id === selectedId));

    function addToGroup(feature, layer, group) {
        const item = createFeature(feature, layer, toLatLng);
        group.addLayer(item);
        if (feature.type === "Label") return;
        markers.set(feature.id, { item, group, feature: { ...feature }, layer });
        item.on("click", () => dotNet?.invokeMethodAsync("MarkerClicked", feature.id));
        item.on("dragend", () => {
            const point = fromLatLng(item.getLatLng());
            dotNet?.invokeMethodAsync("MarkerMoved", feature.id, point.x, point.y);
        });
    }

    for (const layer of layers) {
        const group = L.layerGroup();
        for (const feature of layer.features) addToGroup(feature, layer, group);
        groups.set(layer.id, group);
        if (layer.visibleByDefault) group.addTo(map);
    }

    function applyScale() {
        const pixelsPerUnit = Math.pow(2, map.getZoom());
        const labelPx = width * LABEL_SHARE * pixelsPerUnit;
        element.style.setProperty("--map-label-size", `${Math.max(labelPx, MIN_LABEL_PX)}px`);
        element.style.setProperty("--map-symbol-size", `${Math.max(labelPx * 2, 12)}px`);
        // Item names would just overlap when zoomed far out, so they appear as you zoom in.
        element.classList.toggle("map-hide-names", labelPx < MIN_NAME_PX);
    }

    // While moving items, a click on the map places the selected item. It doesn't require dragging.
    map.on("click", (e) => {
        const point = fromLatLng(e.latlng);
        if (point.x < 0 || point.x > 1 || point.y < 0 || point.y > 1) return;
        if (placing) dotNet?.invokeMethodAsync("PlaceNewPin", point.x, point.y);
        else if (moveMode && selectedId !== null) dotNet?.invokeMethodAsync("PlaceSelected", point.x, point.y);
    });

    map.on("zoom zoomend", applyScale);
    applyScale();

    const resizeObserver = new ResizeObserver(() => map.invalidateSize());
    resizeObserver.observe(element);

    return {
        setLayerVisible(layerId, visible) {
            const group = groups.get(layerId);
            if (!group) return;
            if (visible) group.addTo(map);
            else group.remove();
        },
        resetView() {
            map.fitBounds(bounds);
        },
        // The part of the map showing, so a link opens the same area on a screen of any size.
        viewArea() {
            const view = map.getBounds();
            const topLeft = fromLatLng(view.getNorthWest());
            const bottomRight = fromLatLng(view.getSouthEast());
            return [topLeft.x, topLeft.y, bottomRight.x, bottomRight.y];
        },
        showArea(left, top, right, bottom) {
            map.fitBounds([toLatLng(left, bottom), toLatLng(right, top)], { animate: false });
        },
        addLayer(layerId) {
            if (!groups.has(layerId)) groups.set(layerId, L.layerGroup().addTo(map));
        },
        removeLayer(layerId) {
            const group = groups.get(layerId);
            if (!group) return;
            group.remove();
            groups.delete(layerId);
            for (const [id, entry] of markers) {
                if (entry.layer.id === layerId) markers.delete(id);
            }
        },
        // Redraws a layer's items after its legend has changed.
        updateLayerLegend(layerId, legend) {
            for (const entry of markers.values()) {
                if (entry.layer.id !== layerId) continue;
                entry.layer = { ...entry.layer, legend };
                redraw(entry);
            }
        },
        setPlacing(enabled) {
            placing = enabled;
            element.classList.toggle("map-placing", enabled);
        },
        viewCenter() {
            const point = fromLatLng(map.getCenter());
            return [Math.min(1, Math.max(0, point.x)), Math.min(1, Math.max(0, point.y))];
        },
        addFeature(feature, layer) {
            let group = groups.get(layer.id);
            if (!group) {
                group = L.layerGroup().addTo(map);
                groups.set(layer.id, group);
            }
            addToGroup(feature, layer, group);
        },
        setFeatureDetails(featureId, photoCount, hasDescription) {
            const entry = markers.get(featureId);
            if (!entry) return;
            entry.feature.photoCount = photoCount;
            entry.feature.description = hasDescription ? "1" : null;
            redraw(entry);
        },
        setMoveMode(enabled) {
            moveMode = enabled;
            element.classList.toggle("map-moving", enabled);
            for (const { item } of markers.values()) {
                // Markers on a hidden layer pick this up when their layer is switched back on.
                item.options.draggable = enabled;
                if (item.dragging) {
                    if (enabled) item.dragging.enable();
                    else item.dragging.disable();
                }
            }
        },
        selectFeature(featureId) {
            const previous = markers.get(selectedId);
            selectedId = featureId;
            if (previous) redraw(previous);
            const entry = markers.get(featureId);
            if (entry) redraw(entry);
        },
        moveFeature(featureId, x, y) {
            const entry = markers.get(featureId);
            if (!entry) return;
            entry.feature.x = x;
            entry.feature.y = y;
            entry.item.setLatLng(toLatLng(x, y));
        },
        focusFeature(featureId) {
            const entry = markers.get(featureId);
            if (!entry) return;
            if (!map.hasLayer(entry.group)) entry.group.addTo(map);
            map.setView(entry.item.getLatLng(), Math.max(map.getZoom(), -1));
        },
        updateFeature(featureId, label, needsReview) {
            const entry = markers.get(featureId);
            if (!entry) return;
            entry.feature.label = label;
            entry.feature.needsReview = needsReview;
            // Leaflet reads the title only when it builds a new icon: the option covers a hidden layer, the attribute a showing one.
            entry.item.options.title = label ?? "";
            redraw(entry);
            entry.item.getElement()?.setAttribute("title", label ?? "");
        },
        removeFeature(featureId) {
            const entry = markers.get(featureId);
            if (!entry) return;
            entry.group.removeLayer(entry.item);
            markers.delete(featureId);
        },
        destroy() {
            resizeObserver.disconnect();
            map.remove();
        },
    };
}

function createFeature(feature, layer, toLatLng) {
    const position = toLatLng(feature.x, feature.y);
    if (feature.type === "Label") {
        return L.marker(position, {
            interactive: false,
            keyboard: false,
            // Above symbols, because both tend to sit in the middle of a room and the text matters more.
            zIndexOffset: 10000,
            icon: L.divIcon({
                className: "map-label",
                html: `<span style="color:${safeColor(layer.color)}">${escapeHtml(feature.label)}</span>`,
                iconSize: [0, 0],
            }),
        });
    }

    return L.marker(position, { title: feature.label ?? "", icon: markerIcon(feature, layer) });
}

function markerIcon(feature, layer, selected = false) {
    const symbol = SYMBOLS[feature.symbol] ?? { shape: "square", color: safeColor(layer.color) };
    const entry = feature.legendEntryId ? layer.legend?.find(e => e.id === feature.legendEntryId) : null;
    const glyph = entry
        ? `<span class="map-symbol"><svg class="map-custom-icon" viewBox="0 0 24 24" aria-hidden="true"><path fill="${safeColor(entry.color)}" stroke="#fff" stroke-width="1.5" d="${CUSTOM_SHAPES[entry.shape] ?? CUSTOM_SHAPES.circle}"/></svg></span>`
        : symbol.shape === "square"
        ? `<span class="map-symbol map-symbol-square" style="background:${symbol.color}">${symbol.letter ?? ""}</span>`
        : `<span class="map-symbol" style="color:${symbol.color}">${symbol.shape === "camera" ? CAMERA : symbol.shape === "box" ? BOX : symbol.shape === "triangle" ? TRIANGLE : STAR}</span>`;

    // Other items with pictures or notes get a small mark, so people know there's something to open.
    const badge = feature.symbol !== "photo" && (feature.photoCount > 0 || feature.description)
        ? '<span class="map-item-badge" title="Has photos or notes">+</span>' : "";

    // Items to check get a ring and a "?" so they stand out without relying on color.
    return L.divIcon({
        className: `map-marker${feature.needsReview ? " map-marker-review" : ""}${selected ? " map-marker-selected" : ""}`,
        html: `${glyph}${badge}<span class="map-marker-name">${feature.needsReview ? "? " : ""}${escapeHtml(feature.label ?? "(no name)")}</span>`,
        iconSize: [0, 0],
    });
}
