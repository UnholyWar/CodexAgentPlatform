import test from 'node:test';
import assert from 'node:assert/strict';
import { generateKeyPairSync, randomBytes, sign } from 'node:crypto';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { join } from 'node:path';
import { updateLicense } from '../CodexAgentPlatform/Licensing/license.mjs';

test('activation, persisted identity, renewal, offline use and revocation', async t => {
    const dir = await mkdtemp(join(process.cwd(), '.license-test-'));
    t.after(() => rm(dir, { recursive: true, force: true }));
    const pair = generateKeyPairSync('ed25519');
    const code = randomBytes(32).toString('base64url');
    const now = Math.floor(Date.now() / 1000);
    let exp = now + 600, mode = 'ok', expectedAction = 'activate', bound;
    const request = async (url, options) => {
        assert.equal(url, 'https://lisansapi.onurozgultekin.com/v1/' + expectedAction);
        assert.equal(options.method, 'POST');
        const body = JSON.parse(options.body);
        assert.equal(body.code, code);
        bound ??= body.installationId;
        assert.equal(body.installationId, bound);
        if (mode === 'offline') throw Error();
        if (mode === 'revoked') return new Response(null, { status: 403 });
        const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
        const payload = { iss: 'codex-agent-platform-license-server', aud: 'codex-agent-platform',
            sub: 'fixture', installationId: bound, iat: now - 1, exp };
        const signed = encode({ alg: 'EdDSA', typ: 'JWT' }) + '.' + encode(payload);
        const token = signed + '.' + sign(null, Buffer.from(signed), pair.privateKey).toString('base64url');
        return Response.json({ token, expiresAt: exp });
    };
    const refresh = input => updateLicense(dir, input, request, pair.publicKey);
    assert.equal(await refresh(undefined), 0);
    assert.equal(await refresh(code), exp);
    const file = join(dir, 'platform-license.json');
    assert.equal((await stat(file)).mode & 0o777, 0o600);
    expectedAction = 'renew';
    mode = 'offline';
    assert.equal(await refresh(undefined), exp);
    assert.equal(await updateLicense(dir, undefined, request), 0);
    mode = 'ok';
    exp += 60;
    assert.equal(await refresh(undefined), exp);
    mode = 'revoked';
    assert.equal(await refresh(undefined), 0);
    assert.deepEqual(JSON.parse(await readFile(file)), { installationId: bound });
    mode = 'offline';
    assert.equal(await refresh(undefined), 0);
});

test('a fresh verifier verifies a previous activation before offline use', async t => {
    const dir = await mkdtemp(join(process.cwd(), '.license-test-'));
    t.after(() => rm(dir, { recursive: true, force: true }));
    const pair = generateKeyPairSync('ed25519');
    const code = randomBytes(32).toString('base64url');
    const exp = Math.floor(Date.now() / 1000) + 300;
    await updateLicense(dir, code, async (_, options) => {
        const encode = x => Buffer.from(JSON.stringify(x)).toString('base64url');
        const header = encode({ alg: 'EdDSA', typ: 'JWT' });
        const payload = encode({ iss: 'codex-agent-platform-license-server', aud: 'codex-agent-platform',
            sub: 'fixture', installationId: JSON.parse(options.body).installationId, iat: exp - 300, exp });
        const signed = header + '.' + payload;
        return Response.json({ token: signed + '.' + sign(null, Buffer.from(signed), pair.privateKey).toString('base64url'), expiresAt: exp });
    }, pair.publicKey);
    const helper = new URL('../CodexAgentPlatform/Licensing/license.mjs', import.meta.url);
    const restart = async () => {
        helper.search = randomBytes(8).toString('hex');
        const fresh = await import(helper.href);
        return fresh.updateLicense(dir, undefined, () => { throw Error('offline'); }, pair.publicKey);
    };
    assert.equal(await restart(), exp);
    const { writeFile } = await import('node:fs/promises');
    const file = join(dir, 'platform-license.json');
    const saved = JSON.parse(await readFile(file));
    saved.installationId = '00000000-0000-4000-8000-000000000001';
    await writeFile(file, JSON.stringify(saved));
    assert.equal(await restart(), 0);
});
