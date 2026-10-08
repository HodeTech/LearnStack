#!/usr/bin/env python3
"""Install only the approved Linux AMD64 oasdiff archive, verifying before extraction."""
import hashlib
import io
from pathlib import Path
import platform
import subprocess
import sys
import tarfile
import urllib.request

VERSION = '1.33.0'
SHA256 = '43a4e328e2d13ba1552d760aa68d2485c75c5621f309f6ff64ae895188345247'
URL = 'https://github.com/oasdiff/oasdiff/releases/download/v' + VERSION + '/oasdiff_' + VERSION + '_linux_amd64.tar.gz'


def main():
    if len(sys.argv) != 2 or sys.platform != 'linux' or platform.machine() not in ('x86_64', 'amd64'):
        raise ValueError('Usage on Linux AMD64: install-oasdiff.py DESTINATION')
    with urllib.request.urlopen(URL, timeout=60) as response:
        archive = response.read()
    if hashlib.sha256(archive).hexdigest() != SHA256:
        raise ValueError('Pinned oasdiff archive checksum mismatch')
    destination = Path(sys.argv[1])
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(fileobj=io.BytesIO(archive), mode='r:gz') as bundle:
        member = bundle.getmember('oasdiff')
        if not member.isfile():
            raise ValueError('Verified archive has no regular oasdiff executable')
        source = bundle.extractfile(member)
        if source is None:
            raise ValueError('Cannot read verified oasdiff executable')
        executable = destination / 'oasdiff'
        executable.write_bytes(source.read())
    executable.chmod(0o755)
    version = subprocess.run([str(executable.resolve()), '--version'], check=True, capture_output=True, text=True).stdout.strip()
    if version != 'oasdiff version ' + VERSION:
        raise ValueError('Pinned executable version mismatch')
    (destination / 'archive-sha256.txt').write_text(SHA256 + '\n')
    (destination / 'version.txt').write_text(version + '\n')
    print(version + '; verified archive ' + SHA256)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print('oasdiff installation refused: ' + str(error), file=sys.stderr)
        sys.exit(1)
