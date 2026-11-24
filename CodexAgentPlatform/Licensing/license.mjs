import { randomUUID } from 'node:crypto';
import { readFile, writeFile, rename, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { verifySignature } from './verify-signature.mjs';

const url = 'https://lisansapi.onurozgultekin.com/v1/';
const codePattern = /^[a-zA-Z0-9_-]{43}$/;
const seconds = () => Math.floor(Date.now() / 1000);

function expiration(token, id, key) {
    if (typeof token !== 'string' || token.length > 16384) return 0;
    const parts = token.split('.');
    if (parts.length !== 3 || parts.some(s => !/^[A-Za-z0-9_-]+$/.test(s))) return 0;
    const [h, p, s] = parts;
    if (!verifySignature(Buffer.from(h + '.' + p), Buffer.from(s, 'base64url'), key)) return 0;
    try {
        const header = JSON.parse(Buffer.from(h, 'base64url'));
        const claims = JSON.parse(Buffer.from(p, 'base64url'));
        if (header.alg === 'EdDSA' && header.typ === 'JWT' &&
            claims.iss === 'codex-agent-platform-license-server' && claims.aud === 'codex-agent-platform' &&
            typeof claims.sub === 'string' && claims.sub && claims.installationId === id &&
            Number.isSafeInteger(claims.iat) && Number.isSafeInteger(claims.exp) &&
            claims.iat <= seconds() && claims.exp > seconds() && claims.exp > claims.iat &&
            claims.exp - claims.iat <= 604800) return claims.exp;
    } catch { /* malformed signed payload */ }
    return 0;
}

async function save(file, state) {
    const temporary = file + '.' + randomUUID();
    try {
        await writeFile(temporary, JSON.stringify(state), { mode: 0o600, flag: 'wx', flush: true });
        await rename(temporary, file);
    } finally { await rm(temporary, { force: true }); }
}

export async function updateLicense(directory, supplied, request = fetch, key = undefined) {
    const file = join(directory, 'platform-license.json');
    let state;
    try { state = JSON.parse(await readFile(file, 'utf8')); }
    catch (e) {
        if (e.code !== 'ENOENT') throw e;
        if (!supplied) return 0;
        state = { installationId: randomUUID() };
        await save(file, state);
    }
    const id = state.installationId;
    if (typeof id !== 'string' || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(id)) return 0;
    const code = supplied ?? state.code;
    if (!codePattern.test(code ?? '')) return 0;
    const cached = expiration(state.token, id, key);
    let response;
    try {
        response = await request(url + (supplied ? 'activate' : 'renew'), {
            method: 'POST', redirect: 'error', signal: AbortSignal.timeout(5000),
            headers: { 'content-type': 'application/json' }, body: JSON.stringify({ code, installationId: id })
        });
    } catch { return supplied ? 0 : cached; }
    if (response.status === 403) {
        if (!supplied || state.code === supplied) await save(file, { installationId: id });
        return 0;
    }
    if (response.status === 429 || response.status >= 500) return supplied ? 0 : cached;
    if (response.status !== 200) return 0;
    let result;
    try { result = await response.json(); }
    catch (error) {
        if (!supplied && (error instanceof TypeError || ['TimeoutError', 'AbortError'].includes(error.name))) return cached;
        return 0;
    }
    const exp = expiration(result?.token, id, key);
    if (!exp || exp !== result.expiresAt) return 0;
    await save(file, { installationId: id, code, token: result.token });
    return exp;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
    try {
        let input = '';
        for await (const part of process.stdin) {
            input += part;
            if (input.length > 256) throw Error();
        }
        console.log(await updateLicense(process.argv[2], input || undefined));
    } catch { process.exitCode = 1; }
}
