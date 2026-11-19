import { animateOrb } from "./voice-orb.js";

const calls = new WeakMap();
const preparedAudio = new WeakMap();

export function bindAudio(trigger, dialog) {
    // Mobile browsers can require resume() inside the actual tap, before Blazor's server round trip.
    trigger.addEventListener("click", () => {
        if (calls.has(dialog) || preparedAudio.has(dialog)) return;
        const AudioContext = window.AudioContext ?? window.webkitAudioContext;
        if (!AudioContext) return;
        const context = new AudioContext();
        preparedAudio.set(dialog, context);
        void context.resume().catch(() => {});
    });
}

export async function start(dialog, server, captureUrl) {
    const context = preparedAudio.get(dialog);
    preparedAudio.delete(dialog);
    stop(dialog);
    const call = { server, listeners: new AbortController(), playback: new AbortController(), queue: Promise.resolve(),
        audioContext: context, captureUrl,
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
        if (!context?.audioWorklet) {
            await fail("This browser cannot play voice calls. Open the chat in a browser with Web Audio support.");
            return;
        }
        const updateSound = () => { call.play.hidden = context.state === "running"; };
        call.play.addEventListener("click", () => { void context.resume().catch(updateSound); }, { signal });
        context.addEventListener("statechange", updateSound, { signal });
        updateSound();

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
    if (!call) return false;
    const { signal } = call.playback;
    call.queue = call.queue.then(async () => {
        if (signal.aborted) return true;
        call.playbackError.hidden = true;
        call.speakingAgent.textContent = `Preparing audio for ${name}…`;
        call.speakingAgent.hidden = false;
        try {
            const clip = await recordReply(call, id, signal);
            await playReply(dialog, call, clip, name, signal);
            return true;
        } catch (error) {
            showSpeaking(dialog, call);
            if (!signal.aborted) {
                call.playbackError.textContent = error.message || "Could not play the selected ChatGPT voice. See the written reply in chat.";
                call.playbackError.hidden = false;
            }
            return signal.aborted;
        }
    });
    return call.queue;
}

async function recordReply(call, id, signal) {
    const context = call.audioContext;
    await untilAborted(context.resume(), signal);
    call.captureModule ??= context.audioWorklet.addModule(call.captureUrl).catch(error => {
        call.captureModule = null;
        throw error;
    });
    await untilAborted(call.captureModule, signal);
    const peer = new RTCPeerConnection();
    const silentInput = context.createMediaStreamDestination();
    const silence = context.createConstantSource();
    silence.offset.value = 0;
    silence.connect(silentInput);
    silence.start();
    let output, remotePlayer, remote, capture;
    let playbackClosed = false;
    try {
        // Negotiate a full-duplex RTC session but send silence. The microphone stays on the transcription peer.
        for (const track of silentInput.stream.getAudioTracks()) peer.addTrack(track, silentInput.stream);
        const channel = peer.createDataChannel("oai-events");
        peer.addEventListener("track", event => {
            output = event.streams[0] ?? new MediaStream([event.track]);
        }, { signal });
        await peer.setLocalDescription(await peer.createOffer());
        await gatherIce(peer, signal);
        const answer = await untilAborted(call.server.invokeMethodAsync("ConnectVoicePlaybackAsync", id, peer.localDescription.sdp), signal);
        if (!answer.sdp) throw new Error(answer.error);
        await peer.setRemoteDescription({ type: "answer", sdp: answer.sdp });
        const deadline = performance.now() + 15000;
        while (!output || peer.connectionState !== "connected" || channel.readyState !== "open") {
            if (peer.connectionState === "failed" || performance.now() > deadline)
                throw new Error("The ChatGPT playback connection timed out.");
            await delay(80, signal);
        }

        // Keep the RTC decoder active. Only the checked sample buffer goes to the speaker.
        remotePlayer = new Audio();
        remotePlayer.muted = true;
        remotePlayer.playsInline = true;
        remotePlayer.srcObject = output;
        // Some browsers only resolve play() when the first speech packet arrives.
        void remotePlayer.play().catch(() => {});
        remote = context.createMediaStreamSource(output);
        capture = new AudioWorkletNode(context, "voice-capture", { channelCount: 1, channelCountMode: "explicit" });
        let frames = 0, first = -1, last = -1, lastSpeechAt = 0;
        const blocks = [];
        const flushed = new Promise(resolve => {
            capture.port.onmessage = ({ data }) => {
                if (!data) { resolve(); return; }
                for (let index = 0; index < data.length; index++) {
                    if (Math.abs(data[index]) <= 0.002) continue;
                    if (first < 0) first = frames + index;
                    last = frames + index;
                }
                if (last >= frames) lastSpeechAt = performance.now();
                frames += data.length;
                blocks.push(data);
            };
        });
        remote.connect(capture).connect(context.destination);
        const verified = await untilAborted(call.server.invokeMethodAsync("RenderVoicePlaybackAsync", id), signal);
        if (!verified) throw new Error("ChatGPT could not confirm the spoken reply. See the written reply in chat.");
        const audioDeadline = performance.now() + 12000;
        await delay(650, signal);
        while (performance.now() < audioDeadline && (!lastSpeechAt || performance.now() - lastSpeechAt < 550))
            await delay(100, signal);
        capture.port.postMessage("finish");
        const captured = await Promise.race([flushed.then(() => true), delay(3000, signal).then(() => false)]);
        if (!captured)
            throw new Error("The browser could not finish capturing this voice reply.");
        if (first < 0) throw new Error("ChatGPT returned no audible speech for this reply.");
        const canPlay = await untilAborted(call.server.invokeMethodAsync("CloseVoicePlaybackAsync", id), signal);
        playbackClosed = true;
        if (!canPlay) throw new Error("ChatGPT could not confirm the spoken reply. See the written reply in chat.");

        const padding = Math.round(context.sampleRate * 0.12);
        const start = Math.max(0, first - padding);
        const end = Math.min(frames, last + padding + 1);
        const clip = context.createBuffer(1, end - start, context.sampleRate);
        let offset = 0;
        for (const block of blocks) {
            const from = Math.max(0, start - offset), to = Math.min(block.length, end - offset);
            if (to > from) clip.copyToChannel(block.subarray(from, to), 0, offset + from - start);
            offset += block.length;
        }
        return clip;
    } finally {
        if (remotePlayer) {
            remotePlayer.pause();
            remotePlayer.srcObject = null;
        }
        remote?.disconnect();
        capture?.disconnect();
        capture?.port.close();
        silence.stop();
        silence.disconnect();
        silentInput.stream.getTracks().forEach(track => track.stop());
        peer.close();
        if (!playbackClosed) void call.server.invokeMethodAsync("CloseVoicePlaybackAsync", id).catch(() => {});
    }
}

async function playReply(dialog, call, clip, name, signal) {
    const context = call.audioContext;
    await untilAborted(context.resume(), signal);
    const source = context.createBufferSource();
    source.buffer = clip;
    source.connect(context.destination);
    try {
        const ended = new Promise(resolve => { source.onended = resolve; });
        source.start();
        showSpeaking(dialog, call, name);
        await untilAborted(ended, signal);
    } finally {
        source.onended = null;
        source.stop();
        source.disconnect();
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
    call.play.hidden = call.audioContext?.state === "running";
    call.playbackError.hidden = true;
    showSpeaking(dialog, call);
}

export function mute(dialog, muted) {
    calls.get(dialog)?.stream?.getAudioTracks().forEach(track => { track.enabled = !muted; });
}

export function stop(dialog) {
    const prepared = preparedAudio.get(dialog);
    if (prepared) {
        preparedAudio.delete(dialog);
        void prepared.close().catch(() => {});
    }
    const call = calls.get(dialog);
    if (!call) return;
    interrupt(dialog);
    calls.delete(dialog);
    call.listeners.abort();
    call.play.hidden = true;
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
