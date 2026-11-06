// The camera scanner on the Repairs page. Built into one file, with its decoder beside it, so it needs no internet:
//   cd tools/barcode-scanner && npm ci && npm run build
// The results go to src/PcServ.Web/wwwroot/lib/barcode/ and are committed.
import { BarcodeDetector, prepareZXingModule } from "barcode-detector/ponyfill";

// Without this the library downloads its decoder from a public site, which an internal server shouldn't depend on.
prepareZXingModule({
    overrides: {
        locateFile: (file, prefix) => (file.endsWith(".wasm") ? new URL("./zxing_reader.wasm", import.meta.url).href : prefix + file),
    },
});

// One-dimensional formats only, as used on asset tags. Leaving out the rest is faster, and avoids false reads.
const detector = new BarcodeDetector({ formats: ["code_39", "code_128", "itf", "code_93", "codabar"] });

const SCAN_GAP_MS = 150;
const SAME_CODE_WAIT_MS = 4000;

function beep() {
    try {
        const audio = new AudioContext();
        const tone = audio.createOscillator();
        tone.frequency.value = 880;
        tone.connect(audio.destination);
        tone.start();
        tone.stop(audio.currentTime + 0.12);
        tone.onended = () => audio.close();
    } catch {
        // Sound is a nicety, and the screen shows the result as well.
    }
    navigator.vibrate?.(80);
}

export async function start(video, photoInput, dotNet) {
    let stopped = false;
    let stream = null;
    let lastFrame = new Set();
    const handledAt = new Map();

    // Passes codes on until one is accepted. Returns whether one was.
    async function offer(codes) {
        for (const code of codes) {
            handledAt.set(code, Date.now());
            if (await dotNet.invokeMethodAsync("Scanned", code)) {
                beep();
                return true;
            }
        }
        return false;
    }

    // A code has to appear in two frames running, because one blurred frame can misread.
    async function loop() {
        while (!stopped) {
            if (video.readyState >= 2) {
                let found = [];
                try {
                    found = (await detector.detect(video)).map((barcode) => barcode.rawValue).filter(Boolean);
                } catch {
                    // Frames can fail to read while the camera is starting. The next one will do.
                }
                const steady = found.filter((code) => lastFrame.has(code) && Date.now() - (handledAt.get(code) ?? 0) > SAME_CODE_WAIT_MS);
                lastFrame = new Set(found);
                if (steady.length > 0 && !stopped) await offer(steady);
            }
            await new Promise((resolve) => setTimeout(resolve, SCAN_GAP_MS));
        }
    }

    // A photo goes through the phone's own camera app, so it works even where the browser won't allow live video.
    photoInput.addEventListener("change", async () => {
        const file = photoInput.files?.[0];
        if (!file) return;
        await dotNet.invokeMethodAsync("PhotoReading");
        let found = [];
        try {
            const image = await createImageBitmap(file);
            found = (await detector.detect(image)).map((barcode) => barcode.rawValue).filter(Boolean);
            // Thin bars in a distant photo can read once enlarged, so that gets a second try.
            if (found.length === 0) {
                const larger = document.createElement("canvas");
                larger.width = image.width * 2;
                larger.height = image.height * 2;
                larger.getContext("2d").drawImage(image, 0, 0, larger.width, larger.height);
                found = (await detector.detect(larger)).map((barcode) => barcode.rawValue).filter(Boolean);
            }
            image.close();
        } catch {
            // Treated the same as finding nothing.
        }
        photoInput.value = "";
        if (found.length === 0) await dotNet.invokeMethodAsync("PhotoUnreadable");
        else await offer(found);
    });

    let cameraProblem = null;
    if (!navigator.mediaDevices?.getUserMedia) {
        cameraProblem = "insecure";
    } else {
        try {
            stream = await navigator.mediaDevices.getUserMedia({
                audio: false,
                video: { facingMode: { ideal: "environment" }, width: { ideal: 1920 }, height: { ideal: 1080 } },
            });
            video.srcObject = stream;
            await video.play();
            loop();
        } catch (error) {
            cameraProblem = error?.name === "NotAllowedError" ? "denied" : error?.name === "NotFoundError" ? "missing" : "failed";
        }
    }

    const track = stream?.getVideoTracks()[0];
    let torchOn = false;
    return {
        problem: () => cameraProblem,
        hasLight: () => Boolean(track?.getCapabilities?.().torch),
        toggleLight: async () => {
            torchOn = !torchOn;
            await track.applyConstraints({ advanced: [{ torch: torchOn }] });
            return torchOn;
        },
        takePhoto: () => photoInput.click(),
        stop() {
            stopped = true;
            stream?.getTracks().forEach((t) => t.stop());
            video.srcObject = null;
        },
    };
}
