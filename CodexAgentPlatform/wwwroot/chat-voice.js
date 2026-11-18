import { animateOrb } from "./voice-orb.js";

const calls = new WeakMap();

export async function start(dialog, speaker, server) {
    stop(dialog);
    const call = { speaker, listeners: new AbortController() };
    calls.set(dialog, call);
    const current = () => calls.get(dialog) === call;
    const { signal } = call.listeners;
    const fail = async message => {
        if (!current()) return;
        stop(dialog);
        await server.invokeMethodAsync("VoiceFailedAsync", message).catch(() => {});
    };

    dialog.showModal();
    window.addEventListener("pagehide", () => stop(dialog), { signal });
    dialog.addEventListener("close", () => stop(dialog), { signal });
    document.getElementById("components-reconnect-modal")?.addEventListener("components-reconnect-state-changed", event => {
        if (event.detail.state === "show") close(dialog);
    }, { signal });
    call.observer = new MutationObserver(() => {
        if (!dialog.isConnected) stop(dialog);
    });
    call.observer.observe(document.body, { childList: true, subtree: true });

    try {
        if (!navigator.mediaDevices?.getUserMedia || !window.RTCPeerConnection) {
            await fail("Voice calls require a browser with microphone access on HTTPS or localhost.");
            return;
        }

        const stream = await navigator.mediaDevices.getUserMedia({
            audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }
        });
        if (!current()) {
            stream.getTracks().forEach(track => track.stop());
            return;
        }
        call.stream = stream;
        const peer = new RTCPeerConnection();
        call.peer = peer;
        const channel = peer.createDataChannel("oai-events");
        const play = dialog.querySelector(".voice-play");
        channel.addEventListener("close", () => { if (current()) void fail("The voice session closed."); }, { signal });
        play.addEventListener("click", () => {
            speaker.play().then(() => { play.hidden = true; }).catch(() => {});
        }, { signal });
        for (const track of stream.getAudioTracks()) {
            peer.addTrack(track, stream);
            track.addEventListener("ended", () => void fail("The microphone disconnected."), { signal });
        }
        peer.addEventListener("track", event => {
            speaker.srcObject = event.streams[0] ?? new MediaStream([event.track]);
            speaker.play().catch(() => { if (current()) play.hidden = false; });
            monitorSpeaking(dialog, call, event.receiver);
        }, { signal });
        peer.addEventListener("connectionstatechange", () => {
            if (!current()) return;
            clearTimeout(call.disconnectTimer);
            const connected = peer.connectionState === "connected";
            dialog.toggleAttribute("data-connected", connected);
            if (connected) {
                clearTimeout(call.connectTimer);
                if (!call.ready) {
                    call.ready = true;
                    server.invokeMethodAsync("VoiceReadyAsync").catch(() => stop(dialog));
                    const canvas = dialog.querySelector(".voice-orb-canvas");
                    if (canvas) animateOrb(canvas, signal);
                }
            } else if (peer.connectionState === "failed" || peer.connectionState === "closed") {
                void fail("The voice connection was interrupted.");
            } else if (peer.connectionState === "disconnected") {
                call.disconnectTimer = setTimeout(() => void fail("The network disconnected the call."), 5000);
            }
        }, { signal });

        await peer.setLocalDescription(await peer.createOffer());
        await gatherIce(peer, signal);
        if (!current()) return;
        const answer = await server.invokeMethodAsync("ConnectVoiceAsync", peer.localDescription.sdp);
        if (!current()) return;
        if (!answer) {
            stop(dialog);
            return;
        }
        call.connectTimer = setTimeout(() => void fail("The browser could not establish the voice connection."), 15000);
        await peer.setRemoteDescription({ type: "answer", sdp: answer });
    } catch (error) {
        const message = error.name === "NotAllowedError" ? "Allow microphone access in your browser to start a voice call."
            : error.name === "NotFoundError" ? "No microphone was found." : "Could not connect to the voice call.";
        await fail(message);
    }
}

function gatherIce(peer, signal) {
    return new Promise(resolve => {
        if (peer.iceGatheringState === "complete" || signal.aborted) {
            resolve();
            return;
        }
        const finish = () => {
            clearTimeout(timer);
            peer.removeEventListener("icegatheringstatechange", change);
            signal.removeEventListener("abort", finish);
            resolve();
        };
        const change = () => { if (peer.iceGatheringState === "complete") finish(); };
        const timer = setTimeout(finish, 1200);
        signal.addEventListener("abort", finish, { once: true });
        peer.addEventListener("icegatheringstatechange", change);
    });
}

function monitorSpeaking(dialog, call, receiver) {
    let previousAudio, previousTime;
    let expansion = 0;
    async function sample() {
        if (calls.get(dialog) !== call || call.receiver !== receiver) return;
        let level = 0;
        try {
            const report = await receiver.getStats();
            const remote = Array.from(report.values()).find(row =>
                row.type === "inbound-rtp" && (row.kind ?? row.mediaType) === "audio");
            const time = performance.now();
            if (remote?.packetsReceived === undefined || remote.packetsReceived !== previousAudio?.packetsReceived)
                level = remote?.audioLevel ?? 0;
            if (previousAudio && remote) {
                const energy = (remote.totalAudioEnergy ?? 0) - (previousAudio.totalAudioEnergy ?? 0);
                const duration = (remote.totalSamplesDuration ?? 0) - (previousAudio.totalSamplesDuration ?? 0);
                const seconds = duration > 0 ? duration : (time - previousTime) / 1000;
                if (seconds > 0 && energy > 0) level = Math.max(level, Math.sqrt(energy / seconds));
            }
            previousAudio = remote;
            previousTime = time;
            for (const source of receiver.getSynchronizationSources?.() ?? []) {
                if (time - source.timestamp < 250) level = Math.max(level, source.audioLevel ?? 0);
            }
        } catch {
            // A stats report can be unavailable while the peer is closing.
        }
        if (calls.get(dialog) !== call || call.receiver !== receiver) return;

        const target = level > .008 ? .035 + .105 * Math.sqrt(Math.min(1, level / .19)) : 0;
        // Respond to speech promptly and ease back through brief pauses between words.
        expansion += (target - expansion) * (target > expansion ? .55 : .19);
        dialog.style.setProperty("--voice-speech-scale", (1 + expansion).toFixed(3));
        dialog.dataset.speaking = String(expansion > .025);
        call.levelTimer = setTimeout(sample, 100);
    }
    clearTimeout(call.levelTimer);
    call.receiver = receiver;
    void sample();
}

export function mute(dialog, muted) {
    calls.get(dialog)?.stream?.getAudioTracks().forEach(track => { track.enabled = !muted; });
}

export function stop(dialog) {
    const call = calls.get(dialog);
    if (!call) return;
    calls.delete(dialog);
    call.listeners.abort();
    call.observer?.disconnect();
    clearTimeout(call.connectTimer);
    clearTimeout(call.disconnectTimer);
    clearTimeout(call.levelTimer);
    call.stream?.getTracks().forEach(track => track.stop());
    call.peer?.close();
    call.speaker.srcObject = null;
    dialog.querySelector(".voice-play").hidden = true;
    dialog.removeAttribute("data-connected");
    delete dialog.dataset.speaking;
    dialog.style.removeProperty("--voice-speech-scale");
}

export function close(dialog) {
    stop(dialog);
    if (dialog.open) dialog.close();
}
