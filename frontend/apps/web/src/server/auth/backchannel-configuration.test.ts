// @vitest-environment node
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { expect, it } from 'vitest';

import { backchannelConfiguration } from './backchannel-configuration.js';

it('requires explicit activation, private agreeing sources and a fixed HTTPS callback', () => {
  const root = mkdtempSync(join(tmpdir(), 'backchannel-config-'));
  const env = {
    LEARNSTACK_BFF_BACKCHANNEL_ENABLED: 'true',
    LEARNSTACK_BFF_BACKCHANNEL_URL: 'https://localhost:4443/api/auth/backchannel/keycloak',
    LEARNSTACK_BFF_BACKCHANNEL_BIND_ADDRESS: '127.0.0.1',
    LEARNSTACK_BFF_BACKCHANNEL_TLS_CERT: 'cert',
    LEARNSTACK_BFF_BACKCHANNEL_TLS_KEY: 'key',
    LEARNSTACK_BFF_BACKCHANNEL_CA: 'ca',
    LEARNSTACK_BFF_BACKCHANNEL_NATIVE_CA: 'native-ca',
  };
  try {
    expect(backchannelConfiguration(root, {}, false)).toEqual({ enabled: false });
    expect(() => backchannelConfiguration(root, env, false)).toThrow();
    expect(backchannelConfiguration(root, env, true)).toMatchObject({
      enabled: true,
      authority: 'localhost:4443',
      port: 4443,
    });
    expect(() =>
      backchannelConfiguration(
        root,
        { ...env, LEARNSTACK_BFF_BACKCHANNEL_URL: `${env.LEARNSTACK_BFF_BACKCHANNEL_URL}?bad=1` },
        true,
      ),
    ).toThrow();
    mkdirSync(join(root, 'frontend/apps/web'), { recursive: true });
    writeFileSync(join(root, '.env'), 'LEARNSTACK_BFF_BACKCHANNEL_ENABLED=false\n', {
      mode: 0o600,
    });
    expect(() => backchannelConfiguration(root, env, true)).toThrow();
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
