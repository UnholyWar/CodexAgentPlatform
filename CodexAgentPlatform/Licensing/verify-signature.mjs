import { createPublicKey, verify } from 'node:crypto';
import { pathToFileURL } from 'node:url';

const productionKey = createPublicKey({
    key: Buffer.from('MCowBQYDK2VwAyEAPCK0A4inSsM292T6B2YFOa0HpYtA9+UDNs3XJ4F+ipU=', 'base64'),
    format: 'der',
    type: 'spki'
});

export function verifySignature(message, signature, key = productionKey) {
    return signature.length === 64 && verify(null, message, key, signature);
}

// Internal binary protocol: the 64-byte signature followed by the exact signed bytes.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    const chunks = [];
    let size = 0;
    for await (const chunk of process.stdin) {
        size += chunk.length;
        if (size > 131072) process.exit(1);
        chunks.push(chunk);
    }
    const input = Buffer.concat(chunks);
    process.exitCode = verifySignature(input.subarray(64), input.subarray(0, 64)) ? 0 : 1;
}
