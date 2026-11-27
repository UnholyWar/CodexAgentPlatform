#!/usr/bin/env python3
"""Install only the current published Platform image, on request from the panel."""
import json
import os
from pathlib import Path
import re
import socketserver
import subprocess
import threading
import urllib.request

SOCKET = Path('/var/lib/codex-platform-updater/update.sock')
DEPLOY = Path(__file__).with_name('enable.sh')
REPOSITORY = 'ghcr.io/unholywar/codexagentplatform'
SOURCE = 'https://github.com/UnholyWar/CodexAgentPlatform'
CATALOG = 'https://lisansapi.onurozgultekin.com/v1/releases/latest'
TAG = re.compile(r'v(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})\.(0|[1-9][0-9]{0,9})')
state = {'state': 'idle'}
lock = threading.Lock()


def version(tag):
    match = TAG.fullmatch(tag)
    if not match:
        raise ValueError('Invalid release version')
    return tuple(map(int, match.groups()))


def docker(*args, timeout=15, capture=True):
    return subprocess.run(['docker', *args], timeout=timeout, check=True,
                          text=True, stdout=subprocess.PIPE if capture else None).stdout


def read_release():
    with urllib.request.urlopen(CATALOG, timeout=8) as response:
        if response.url != CATALOG or response.status != 200:
            raise ValueError('Unexpected catalog response')
        data = response.read(4097)
    if len(data) > 4096:
        raise ValueError('Oversized catalog response')
    release = json.loads(data)
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', release['digest']):
        raise ValueError('Invalid release digest')
    if not re.fullmatch(r'[a-f0-9]{40}', release['commit']):
        raise ValueError('Invalid release commit')
    return release


def install(tag):
    global state
    outcome = 'failed'
    try:
        release = read_release()
        current = docker('exec', 'codex-agent-platform', 'cat', '/app/platform-version').strip()
        if release['version'] != tag or version(tag) <= version(current):
            raise ValueError('The selected release is stale or not newer')
        image = REPOSITORY + '@' + release['digest']
        docker('pull', image, timeout=360, capture=False)
        metadata = json.loads(docker('image', 'inspect', '--format', '{{json .}}', image))
        labels = metadata['Config']['Labels']
        if (image not in metadata['RepoDigests'] or labels.get('org.opencontainers.image.source') != SOURCE
                or labels.get('org.opencontainers.image.version') != tag
                or labels.get('org.opencontainers.image.revision') != release['commit']):
            raise ValueError('Image metadata does not match the catalog')
        subprocess.run(['bash', str(DEPLOY), '--image', metadata['Id'], '--enable-vault'],
                       check=True)
        outcome = 'installed'
    except Exception as error:
        print(f'Update {tag} failed: {type(error).__name__}: {error}', flush=True)
    finally:
        with lock:
            state = {'state': outcome, 'version': tag}


class Request(socketserver.StreamRequestHandler):
    def handle(self):
        global state
        self.connection.settimeout(2)
        raw = self.rfile.readline(49)
        command = raw.decode('ascii', errors='replace').removesuffix('\n')
        with lock:
            if not raw.endswith(b'\n'):
                answer = {'state': 'invalid'}
            elif command == 'status':
                answer = state
            elif not TAG.fullmatch(command):
                answer = {'state': 'invalid'}
            elif state['state'] == 'running':
                answer = state
            else:
                state = {'state': 'running', 'version': command}
                threading.Thread(target=install, args=(command,), daemon=True).start()
                answer = state
        self.wfile.write(json.dumps(answer).encode('ascii') + b'\n')


def main():
    SOCKET.unlink(missing_ok=True)
    with socketserver.UnixStreamServer(str(SOCKET), Request) as server:
        os.chown(SOCKET, 0, 1654)
        SOCKET.chmod(0o660)
        server.serve_forever()


if __name__ == '__main__':
    main()
