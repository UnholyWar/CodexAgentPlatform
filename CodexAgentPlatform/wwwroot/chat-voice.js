import { animateOrb } from "./voice-orb.js";
import { deviceVoices, voiceForReply } from "./device-voices.js";

const calls = new WeakMap();

export async function start(dialog, server) {
    stop(dialog);
    const call = { listeners: new AbortController(), replies: [], voices: deviceVoices(),
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
        call.play.addEventListener("click", () => playNext(dialog, call), { signal });
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

export async function speak(dialog, reply) {
    const call = calls.get(dialog);
    if (!call) return;
    const text = reply.text.replace(/```[^]*?```/g, " ").replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
        .replace(/^\s*#{1,6}\s+/gm, "").replace(/[*`]/g, "").trim();
    if (!text) return;
    const parts = text.slice(0, 6000).match(/.{1,220}(?:\s|$)|.{1,220}/gs);
    call.replies.push({ speaker: reply.speaker, text, parts, next: 0 });
    await call.voices;
    if (calls.get(dialog) !== call || !call.play.hidden) return;
    playNext(dialog, call);
}

function playNext(dialog, call) {
    const reply = call.replies[0];
    if (!reply || call.utterance) return;
    const engine = window.speechSynthesis;
    const { voice, language } = voiceForReply(reply.speaker, reply.text);
    call.play.hidden = true;
    call.playbackError.hidden = true;
    if (!voice || !engine || !window.SpeechSynthesisUtterance) {
        call.replies.shift();
        call.playbackError.textContent = `Install a device voice for ${language.toUpperCase()} to hear replies. The reply is available in chat.`;
        call.playbackError.hidden = false;
        if (call.replies.length) playNext(dialog, call);
        return;
    }
    const utterance = new SpeechSynthesisUtterance(reply.parts[reply.next]);
    utterance.voice = voice;
    utterance.lang = voice.lang;
    call.utterance = utterance;
    const current = () => calls.get(dialog) === call && call.utterance === utterance;
    const finish = () => {
        clearTimeout(call.speechTimer);
        call.utterance = null;
        showSpeaking(dialog, call);
    };
    const retry = () => {
        if (!current()) return;
        finish();
        engine.cancel();
        call.play.hidden = false;
    };
    utterance.onstart = () => {
        if (!current()) return;
        clearTimeout(call.speechTimer);
        showSpeaking(dialog, call, reply.speaker.name);
    };
    utterance.onend = () => {
        if (!current()) return;
        finish();
        if (++reply.next === reply.parts.length) call.replies.shift();
        playNext(dialog, call);
    };
    utterance.onerror = retry;
    call.speechTimer = setTimeout(retry, 2500);
    engine.speak(utterance);
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
    clearTimeout(call.speechTimer);
    const playing = call.utterance;
    call.utterance = null;
    call.replies.length = 0;
    call.play.hidden = true;
    call.playbackError.hidden = true;
    if (playing) window.speechSynthesis.cancel();
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
    dialog.removeAttribute("data-connected");
    delete dialog.dataset.speaking;
    dialog.style.removeProperty("--voice-speech-scale");
}

export function close(dialog) {
    stop(dialog);
    if (dialog.open) dialog.close();
}
