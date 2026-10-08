#!/usr/bin/env python3
"""Compare verified git snapshots; bootstrap only from a served, operation-free base."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
SNAPSHOT = 'backend/openapi/v1.json'


def git(*arguments):
    return subprocess.run(['git', *arguments], cwd=ROOT, check=True, capture_output=True)


def commit(reference):
    result = git('rev-parse', '--verify', reference + '^{commit}').stdout.decode().strip()
    if not re.fullmatch('[0-9a-f]{40}', result):
        raise ValueError('Base and head must resolve to verified commits')
    return result


def snapshot(sha):
    present = git('ls-tree', '--name-only', sha, '--', SNAPSHOT).stdout.decode().strip()
    if not present:
        return None
    if present != SNAPSHOT:
        raise ValueError('Unexpected snapshot tree entry')
    return git('show', sha + ':' + SNAPSHOT).stdout


def operation_count(document):
    return sum(method in {'get', 'head', 'post', 'put', 'patch', 'delete', 'options', 'trace'}
               for path, item in document['paths'].items() if path.startswith('/api/v1/') for method in item)


def bootstrap(sha, output):
    """Build and serve the actual base Program, with no test controllers or database access."""
    with tempfile.TemporaryDirectory(prefix='learnstack-openapi-base-') as temporary:
        checkout = Path(temporary)
        archive = checkout / 'source.tar'
        git('archive', '--format=tar', '--output=' + str(archive), sha)
        with tarfile.open(archive) as bundle:
            for member in bundle.getmembers():
                target = checkout / member.name
                if not target.resolve().is_relative_to(checkout.resolve()) or not (member.isdir() or member.isfile()):
                    raise ValueError('Unsafe base archive entry')
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    source = bundle.extractfile(member)
                    if source is None:
                        raise ValueError('Cannot extract base source')
                    target.write_bytes(source.read())
        api = checkout / 'backend/src/LearnStack.Api'
        with (output / 'bootstrap-build.log').open('w') as log:
            subprocess.run(['dotnet', 'build', 'LearnStack.Api.csproj', '-c', 'Release'], cwd=api,
                           env={**os.environ, 'CI': 'true'}, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=300)
        with socket.socket() as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        url = f'http://127.0.0.1:{port}'
        env = {**os.environ, 'ASPNETCORE_ENVIRONMENT': 'Development', 'DOTNET_ENVIRONMENT': 'Development',
               'ASPNETCORE_URLS': url, 'Deployment__Mode': 'Development'}
        with (output / 'bootstrap-server.log').open('w') as log:
            process = subprocess.Popen(['dotnet', 'bin/Release/net10.0/LearnStack.Api.dll'], cwd=api, env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                deadline = time.monotonic() + 45
                while True:
                    if process.poll() is not None:
                        raise RuntimeError('Base production composition exited before serving OpenAPI')
                    try:
                        with urllib.request.urlopen(url + '/openapi/v1.json', timeout=2) as response:
                            served = response.read()
                        break
                    except (urllib.error.URLError, TimeoutError):
                        if time.monotonic() >= deadline:
                            raise RuntimeError('Base production OpenAPI did not become available')
                        time.sleep(0.2)
                document = json.loads(served)
                if operation_count(document) != 0:
                    raise ValueError('Missing base snapshot with existing v1 operations is not bootstrap')
                (output / 'bootstrap-served.json').write_bytes(served)
                return served
            finally:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base', required=True)
    parser.add_argument('--head', required=True)
    parser.add_argument('--tool', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / 'requested-refs.json').write_text(json.dumps({'base': args.base, 'head': args.head}, indent=2) + '\n')
    base_sha, head_sha = commit(args.base), commit(args.head)
    (args.output / 'commits.json').write_text(json.dumps({'base': base_sha, 'head': head_sha}, indent=2) + '\n')
    base, head = snapshot(base_sha), snapshot(head_sha)
    if head is None:
        raise ValueError('Head snapshot is missing or deleted')
    if operation_count(json.loads(head)) == 0:
        raise ValueError('Head snapshot has no v1 operations')
    first_baseline = base is None
    if first_baseline:
        base = bootstrap(base_sha, args.output)
    (args.output / 'base.json').write_bytes(base)
    (args.output / 'head.json').write_bytes(head)
    (args.output / 'commits.json').write_text(json.dumps({'base': base_sha, 'head': head_sha, 'firstBaseline': first_baseline,
        'baseSpecSha256': hashlib.sha256(base).hexdigest(), 'headSpecSha256': hashlib.sha256(head).hexdigest()}, indent=2) + '\n')
    return subprocess.run([sys.executable, str(ROOT / 'scripts/openapi-policy.py'), '--tool', str(args.tool.resolve()),
        '--base', str((args.output / 'base.json').resolve()), '--head', str((args.output / 'head.json').resolve()),
        '--output', str((args.output / 'comparison').resolve())], check=False).returncode


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, RuntimeError, subprocess.SubprocessError) as error:
        print('OpenAPI CI failed: ' + str(error), file=sys.stderr)
        sys.exit(2)
