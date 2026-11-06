// Two maps side by side for picking matching points. Points go back to C# as 0-to-1 map coordinates.
/* global L */

function createPane(element, imageUrl, width, height, altText, onPick) {
    const map = L.map(element, {
        crs: L.CRS.Simple,
        minZoom: -6,
        maxZoom: 0,
        zoomSnap: 0.25,
        zoomDelta: 0.5,
        wheelPxPerZoomLevel: 120,
        attributionControl: false,
    });
    const bounds = [[0, 0], [height, width]];
    L.imageOverlay(imageUrl, bounds, { alt: altText }).addTo(map);
    map.fitBounds(bounds);

    const markers = [];
    map.on("click", (e) => {
        const x = e.latlng.lng / width;
        const y = 1 - e.latlng.lat / height;
        if (x < 0 || x > 1 || y < 0 || y > 1) return;
        onPick(x, y, e.latlng);
    });

    const observer = new ResizeObserver(() => map.invalidateSize());
    observer.observe(element);

    return {
        addMarker(latlng, number) {
            markers.push(L.marker(latlng, {
                interactive: false,
                icon: L.divIcon({ className: "align-point", html: `<span>${number}</span>`, iconSize: [0, 0] }),
            }).addTo(map));
        },
        clear() {
            markers.splice(0).forEach((m) => m.remove());
        },
        destroy() {
            observer.disconnect();
            map.remove();
        },
    };
}

export function create(sourceElement, targetElement, source, target, dotNet) {
    let waitingForTarget = null;
    let count = 0;

    const sourcePane = createPane(sourceElement, source.url, source.width, source.height, source.alt, (x, y, latlng) => {
        if (waitingForTarget) return; // Finish the current pair first.
        waitingForTarget = { x, y };
        sourcePane.addMarker(latlng, count + 1);
        dotNet.invokeMethodAsync("SourcePicked");
    });

    const targetPane = createPane(targetElement, target.url, target.width, target.height, target.alt, (x, y, latlng) => {
        if (!waitingForTarget) return; // A point on the first map comes first.
        count++;
        targetPane.addMarker(latlng, count);
        dotNet.invokeMethodAsync("PairPicked", waitingForTarget.x, waitingForTarget.y, x, y);
        waitingForTarget = null;
    });

    return {
        clear() {
            waitingForTarget = null;
            count = 0;
            sourcePane.clear();
            targetPane.clear();
        },
        destroy() {
            sourcePane.destroy();
            targetPane.destroy();
        },
    };
}
