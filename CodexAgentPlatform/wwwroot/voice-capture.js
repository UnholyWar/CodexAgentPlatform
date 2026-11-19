// Capture the provider's mono audio without sending unverified speech to the speakers.
class VoiceCapture extends AudioWorkletProcessor {
    constructor() {
        super();
        this.block = new Float32Array(4096);
        this.used = 0;
        this.finished = false;
        this.port.onmessage = () => {
            this.finished = true;
            if (this.used) this.port.postMessage(this.block.slice(0, this.used));
            this.port.postMessage(null);
        };
    }

    process(inputs) {
        if (this.finished) return false;
        const samples = inputs[0]?.[0];
        if (!samples) return true;
        let offset = 0;
        while (offset < samples.length) {
            const count = Math.min(samples.length - offset, this.block.length - this.used);
            this.block.set(samples.subarray(offset, offset + count), this.used);
            this.used += count;
            offset += count;
            if (this.used === this.block.length) {
                this.port.postMessage(this.block, [this.block.buffer]);
                this.block = new Float32Array(4096);
                this.used = 0;
            }
        }
        return true;
    }
}

registerProcessor("voice-capture", VoiceCapture);
