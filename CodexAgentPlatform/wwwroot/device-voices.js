export async function deviceVoices() {
    const engine = window.speechSynthesis;
    if (!engine) return [];
    if (engine.getVoices().length === 0) {
        await new Promise(resolve => {
            const ready = () => {
                clearTimeout(timer);
                engine.removeEventListener("voiceschanged", ready);
                resolve();
            };
            const timer = setTimeout(ready, 700);
            engine.addEventListener("voiceschanged", ready, { once: true });
        });
    }
    return localVoices().map(voice => ({ id: voice.voiceURI, name: voice.name, language: voice.lang }));
}

function localVoices() {
    return (window.speechSynthesis?.getVoices() ?? []).filter(voice => voice.localService);
}

export function voiceForReply(speaker, text) {
    const language = /[çğıöşüİÇĞÖŞÜ]|\b(?:merhaba|evet|hayir|tamam|sen|ben|proje|bir|icin|nedir)\b/i.test(text)
        ? "tr" : /\b(?:the|is|are|what|your|you|can|hello|hi|this|it|could|would)\b/i.test(text)
            ? "en" : null;
    const voices = localVoices();
    const preferred = voices.find(voice => voice.voiceURI === speaker.voice);
    const locale = language ?? (preferred?.lang || navigator.language).split("-")[0].toLowerCase();
    const matching = voices.filter(voice => voice.lang.split("-")[0].toLowerCase() === locale);
    if (matching.includes(preferred)) return { voice: preferred, language: locale };
    if (matching.length === 0)
        return { voice: preferred ?? voices.find(candidate => candidate.default) ?? voices[0] ?? null, language: locale };
    const key = speaker.voice ?? speaker.id;
    const variation = Array.from(key).reduce((sum, letter) => (Math.imul(sum, 31) + letter.codePointAt(0)) >>> 0, 0);
    const voice = speaker.id === "normal-chat" ? matching.find(candidate => candidate.default) : null;
    return { voice: voice ?? matching[variation % matching.length], language: locale };
}
