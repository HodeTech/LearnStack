#!/usr/bin/env python3
"""Authenticate the approved Go module before installing the CI secret scanner."""
import json
import os
import subprocess
import sys

MODULE = 'github.com/HodeTech/leakwatch'
VERSION = 'v1.8.0'
SUM = 'h1:/01muS5E8bluZ15eiQ5kYFVO6dLY3TN5UGL2R3+gskE='
GO_MOD_SUM = 'h1:avaj7X6b3nFede0VCf7vh9ghJkKAbiWGid/fe9NWTNw='


def main():
    # Go already authenticates public modules with its checksum database. Keep
    # that protection explicit for the complete dependency graph, and bind the
    # root module/version to the independently reviewed module and go.mod sums.
    environment = {**os.environ, 'GOSUMDB': 'sum.golang.org',
                   'GOPROXY': 'https://proxy.golang.org', 'GOPRIVATE': '',
                   'GONOSUMDB': '', 'GONOPROXY': '', 'GOWORK': 'off', 'GOENV': 'off'}
    target = MODULE + '@' + VERSION
    result = subprocess.run(['go', 'mod', 'download', '-json', target],
                            check=True, capture_output=True, text=True, env=environment)
    module = json.loads(result.stdout)
    expected = {'Path': MODULE, 'Version': VERSION, 'Sum': SUM, 'GoModSum': GO_MOD_SUM}
    if not isinstance(module, dict) or any(module.get(key) != value for key, value in expected.items()):
        raise ValueError('Pinned Leakwatch module checksum mismatch')
    subprocess.run(['go', 'install', target], check=True, env=environment)


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print('Leakwatch installation refused: ' + str(error), file=sys.stderr)
        sys.exit(1)
