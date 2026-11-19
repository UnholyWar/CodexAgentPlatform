import { animateOrb } from "./voice-orb.js";

const calls = new WeakMap();

export async function start(dialog, server) {
    stop(dialog);
    const call = { server, listeners: new AbortController(), playback: new AbortController(), queue: Promise.resolve(),
        player: dialog.querySelector(".voice-recording"),
        play: dialog.querySelector(".voice-play"), playbackError: dialog.querySelector(".voice-playback-error"),
        speakingAgent: dialog.querySelector(".voice-speaking-agent") };
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
        channel.addEventListener("close", () => { if (current()) void fail("The voice session closed."); }, { signal });
        call.play.addEventListener("click", () => {
            if (call.audioContext && call.audioContext.state !== "running") void call.audioContext.resume().catch(() => {});
            if (call.player.hasAttribute("src")) void call.player.play().catch(() => {});
        }, { signal });
        for (const track of stream.getAudioTracks()) {
            peer.addTrack(track, stream);
            track.addEventListener("ended", () => void fail("The microphone disconnected."), { signal });
        }
        // The peer supplies microphone transcription. Its independent audio output is never attached to a player.
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

export function speak(dialog, id, name) {
    const call = calls.get(dialog);
    if (!call) return;
    const { signal } = call.playback;
    call.queue = call.queue.then(async () => {
        if (signal.aborted) return;
        call.playbackError.hidden = true;
        try {
            const clip = await recordReply(call, id, signal);
            await playReply(dialog, call, clip, name, signal);
        } catch (error) {
            if (!signal.aborted) {
                call.playbackError.textContent = error.message || "Could not play the selected ChatGPT voice. See the written reply in chat.";
                call.playbackError.hidden = false;
            }
        }
    });
    return call.queue;
}

async function recordReply(call, id, signal) {
    const AudioContext = window.AudioContext ?? window.webkitAudioContext;
    if (!AudioContext || !window.MediaRecorder) throw new Error("This browser cannot play ChatGPT voice replies. See the written reply in chat.");
    call.audioContext ??= new AudioContext();
    if (call.audioContext.state !== "running") {
        call.play.hidden = false;
        await untilAborted(call.audioContext.resume(), signal);
        call.play.hidden = true;
    }
    signal.throwIfAborted();
    const peer = new RTCPeerConnection();
    const source = call.audioContext.createConstantSource();
    const input = call.audioContext.createMediaStreamDestination();
    let output, recorder, remote, analyser, recorded, silent, meter, remotePlayer;
    let sourceStarted = false;
    let playbackClosed = false;
    let lastSpeechAt = 0;
    try {
        // The renderer receives only silence here. The microphone stays on the transcription peer.
        source.offset.value = 0;
        source.connect(input);
        source.start();
        sourceStarted = true;
        for (const track of input.stream.getAudioTracks()) peer.addTrack(track, input.stream);
        const channel = peer.createDataChannel("oai-events");
        peer.addEventListener("track", event => {
            output = event.streams[0] ?? new MediaStream([event.track]);
        }, { signal });

        await peer.setLocalDescription(await peer.createOffer());
        await gatherIce(peer, signal);
        const answer = await untilAborted(call.server.invokeMethodAsync("ConnectVoicePlaybackAsync", id, peer.localDescription.sdp), signal);
        if (!answer.sdp) throw new Error(answer.error);
        await peer.setRemoteDescription({ type: "answer", sdp: answer.sdp });
        const deadline = performance.now() + 12000;
        while (!output || peer.connectionState !== "connected" || channel.readyState !== "open") {
            if (peer.connectionState === "failed" || performance.now() > deadline)
                throw new Error("The ChatGPT playback connection timed out.");
            await delay(80, signal);
        }

        // Keep the browser's remote track decoding while the recording is assembled off the speaker.
        remotePlayer = new Audio();
        remotePlayer.muted = true;
        remotePlayer.playsInline = true;
        remotePlayer.srcObject = output;
        void remotePlayer.play().catch(() => {});
        remote = call.audioContext.createMediaStreamSource(output);
        analyser = call.audioContext.createAnalyser();
        recorded = call.audioContext.createMediaStreamDestination();
        silent = call.audioContext.createGain();
        silent.gain.value = 0;
        remote.connect(analyser);
        analyser.connect(recorded);
        analyser.connect(silent).connect(call.audioContext.destination);
        const samples = new Uint8Array(analyser.fftSize);
        meter = setInterval(() => {
            analyser.getByteTimeDomainData(samples);
            if (samples.some(sample => Math.abs(sample - 128) > 1)) lastSpeechAt = performance.now();
        }, 40);
        recorder = new MediaRecorder(recorded.stream);
        const chunks = [];
        const recording = new Promise(resolve => {
            recorder.addEventListener("dataavailable", event => chunks.push(event.data));
            recorder.addEventListener("stop", () => resolve(new Blob(chunks, { type: recorder.mimeType })), { once: true });
        });
        recorder.start();
        const verified = await untilAborted(call.server.invokeMethodAsync("RenderVoicePlaybackAsync", id), signal);
        if (!verified) throw new Error("Could not verify this ChatGPT voice reply. See the written reply in chat.");
        // Allow the last audio packets to arrive after the provider's transcript.
        await delay(550, signal);
        const audioDeadline = performance.now() + 3500;
        while (performance.now() < audioDeadline && (!lastSpeechAt || performance.now() - lastSpeechAt < 400))
            await delay(100, signal);
        if (recorder.state !== "inactive") recorder.stop();
        const clip = await untilAborted(recording, signal);
        if (!lastSpeechAt || clip.size <= 0) throw new Error("ChatGPT returned no audio for this reply.");
        const canPlay = await untilAborted(call.server.invokeMethodAsync("CloseVoicePlaybackAsync", id), signal);
        playbackClosed = true;
        if (!canPlay) throw new Error("Could not verify this ChatGPT voice reply. See the written reply in chat.");
        return clip;
    } finally {
        if (recorder && recorder.state !== "inactive") recorder.stop();
        clearInterval(meter);
        if (remotePlayer) {
            remotePlayer.pause();
            remotePlayer.srcObject = null;
        }
        remote?.disconnect();
        analyser?.disconnect();
        silent?.disconnect();
        recorded?.stream.getTracks().forEach(track => track.stop());
        if (sourceStarted) source.stop();
        source.disconnect();
        input.stream.getTracks().forEach(track => track.stop());
        peer.close();
        if (!playbackClosed) void call.server.invokeMethodAsync("CloseVoicePlaybackAsync", id).catch(() => {});
    }
}

async function playReply(dialog, call, clip, name, signal) {
    signal.throwIfAborted();
    const { player, play } = call;
    const url = URL.createObjectURL(clip);
    const events = new AbortController();
    try {
        const finished = new Promise(resolve => {
            player.addEventListener("playing", () => {
                play.hidden = true;
                showSpeaking(dialog, call, name);
            }, { signal: events.signal });
            player.addEventListener("ended", () => resolve(true), { signal: events.signal });
            player.addEventListener("error", () => resolve(false), { signal: events.signal });
        });
        player.src = url;
        void player.play().catch(() => { if (!signal.aborted) play.hidden = false; });
        if (!await untilAborted(finished, signal)) throw new Error("Could not play this ChatGPT recording.");
    } finally {
        events.abort();
        player.pause();
        player.removeAttribute("src");
        player.load();
        URL.revokeObjectURL(url);
        play.hidden = true;
        showSpeaking(dialog, call);
    }
}

function untilAborted(promise, signal) {
    return new Promise((resolve, reject) => {
        if (signal.aborted) {
            void promise.catch(() => {});
            reject(signal.reason);
            return;
        }
        const abort = () => reject(signal.reason);
        signal.addEventListener("abort", abort, { once: true });
        promise.then(resolve, reject).finally(() => signal.removeEventListener("abort", abort));
    });
}

function delay(ms, signal) {
    return untilAborted(new Promise(resolve => setTimeout(resolve, ms)), signal);
}

function showSpeaking(dialog, call, name = null) {
    const speaking = name !== null;
    dialog.dataset.speaking = String(speaking);
    dialog.style.setProperty("--voice-speech-scale", speaking ? "1.13" : "1");
    if (call.speakingAgent) {
        call.speakingAgent.textContent = name ? `${name} is speaking` : "";
        call.speakingAgent.hidden = !speaking;
    }
}

export function interrupt(dialog) {
    const call = calls.get(dialog);
    if (!call) return;
    call.playback.abort();
    call.playback = new AbortController();
    call.play.hidden = true;
    call.playbackError.hidden = true;
    showSpeaking(dialog, call);
}

export function mute(dialog, muted) {
    calls.get(dialog)?.stream?.getAudioTracks().forEach(track => { track.enabled = !muted; });
}

export function stop(dialog) {
    const call = calls.get(dialog);
    if (!call) return;
    interrupt(dialog);
    calls.delete(dialog);
    call.listeners.abort();
    call.observer?.disconnect();
    clearTimeout(call.connectTimer);
    clearTimeout(call.disconnectTimer);
    call.stream?.getTracks().forEach(track => track.stop());
    call.peer?.close();
    if (call.audioContext) void call.audioContext.close().catch(() => {});
    dialog.removeAttribute("data-connected");
    delete dialog.dataset.speaking;
    dialog.style.removeProperty("--voice-speech-scale");
}

export function close(dialog) {
    stop(dialog);
    if (dialog.open) dialog.close();
}
