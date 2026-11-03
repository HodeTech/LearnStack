import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import {
  existsSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';

import { instrumentCopiedBffRuntime, prepareBffProof } from './bff-proof-host.mjs';
import { instrumentCopiedAdmissionRuntime } from './framework-proof-admission-source.mjs';
import { instrumentCopiedIngressRuntime } from './framework-proof-ingress-source.mjs';
import { prepareFrameworkProof } from './framework-proof-support.mjs';

const sourceApp = realpathSync(new URL('../', import.meta.url));
function matrixOptions(marker) {
  return {
    ownershipCase: 'matrix',
    uiMode: false,
    isolatedProtocol: true,
    fakeIssuer: 'https://identity.example.test/realms/learnstack',
    fakeLogout: 'https://identity.example.test/logout',
    sites: [{ host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' }],
    marker,
    clockBase: 1800000000000,
  };
}
function credentials(source, clientSecret) {
  const declarations = source.match(/^ {4}const credentials = (.+);$/gm);
  assert.equal(declarations?.length, 1);
  const expression = declarations[0].slice('    const credentials = '.length, -1);
  return vm.runInNewContext(`(${expression})()`, { bff: { clientSecret }, now: 0 });
}
function temp() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-proof-source-')));
  return { root, remove: () => rmSync(root, { recursive: true, force: true }) };
}

test('matrix source stays identical while credentials retain distinct private fixture bindings', () => {
  const source = readFileSync(join(sourceApp, 'scripts/public-server-runtime.mjs'), 'utf8');
  const markers = ['first-private-marker', 'second-private-marker'];
  const copies = markers.map((marker) => instrumentCopiedBffRuntime(source, matrixOptions(marker)));
  assert.equal(copies[0], copies[1]);
  for (const marker of markers) {
    assert.equal(copies[0].includes(marker), false);
    const actual = credentials(copies[0], marker);
    for (const field of ['accessToken', 'refreshToken', 'idToken'])
      assert.equal(actual[field], marker);
  }
  assert.notEqual(credentials(copies[0], markers[1]).accessToken, markers[0]);
});

test('matrix credential selection refuses other profiles and preserves ordinary literal credentials', () => {
  const source = readFileSync(join(sourceApp, 'scripts/public-server-runtime.mjs'), 'utf8');
  for (const override of [
    { isolatedProtocol: false },
    { uiMode: true },
    { external: {} },
    { mutation: 'shutdown' },
  ])
    assert.throws(
      () => instrumentCopiedBffRuntime(source, { ...matrixOptions('marker'), ...override }),
      /Matrix credentials require the isolated ownership fixture/,
    );
  const ordinary = ['first-marker', 'second-marker'].map((marker) =>
    instrumentCopiedBffRuntime(source, { ...matrixOptions(marker), ownershipCase: undefined }),
  );
  assert.notEqual(ordinary[0], ordinary[1]);
  assert.equal(credentials(ordinary[0], 'unrelated-secret').accessToken, 'first-marker');
});

test('forged matrix templates and ordinary template requests refuse before creating resources', async () => {
  const f = temp();
  try {
    const root = join(f.root, 'refused');
    const owner = {
      assertActive() {
        assert.fail('Unadmitted template reached the owner');
      },
    };
    const options = {
      ...matrixOptions('marker'),
      root,
      owner,
      frameworkTemplate: Object.freeze({ app: sourceApp }),
    };
    await assert.rejects(prepareBffProof(options), /Matrix template must share its owner/);
    await assert.rejects(
      prepareBffProof({ ...options, ownershipCase: undefined }),
      /Only isolated matrix fixtures copy templates/,
    );
    assert.equal(existsSync(root), false);
  } finally {
    f.remove();
  }
});

for (const profile of ['none', 'ingress', 'admission', 'bff'])
  test(`copied ${profile} profile is literal and syntactically sealed before generation`, () => {
    const f = temp();
    try {
      const app = join(f.root, 'frontend/apps/web');
      prepareFrameworkProof({ sourceApp, app, profile });
      const owner = join(app, 'scripts/public-server.mjs');
      const child = join(app, 'scripts/public-runtime-child.mjs');
      const ownerSource = readFileSync(owner, 'utf8');
      assert.ok(ownerSource.includes(`createCopiedParentProof('${profile}')`));
      assert.ok(ownerSource.includes('await copiedProof?.close();'));
      assert.ok(
        ownerSource.indexOf('await copiedProof?.close();') <
          ownerSource.indexOf('lease?.release();'),
        'Copied proof IPC closes after the runtime family joins and before lease release',
      );
      assert.ok(readFileSync(child, 'utf8').includes(`createCopiedChildProof('${profile}')`));
      for (const path of [owner, child]) execFileSync(process.execPath, ['--check', path]);
    } finally {
      f.remove();
    }
  });

test('BFF copied runtime emits only its selected closed proof hooks', () => {
  const f = temp();
  try {
    const source = readFileSync(join(sourceApp, 'scripts/public-server-runtime.mjs'), 'utf8');
    const transformed = instrumentCopiedBffRuntime(source, {
      mutation: undefined,
      external: undefined,
      uiMode: true,
      isolatedProtocol: true,
      disabledOwnPreference: false,
      fakeIssuer: 'https://identity.example.test/realms/learnstack',
      fakeLogout: 'https://identity.example.test/realms/learnstack/protocol/openid-connect/logout',
      sites: [{ host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' }],
      marker: 'fixture-marker',
      clockBase: 1800000000000,
    });
    assert.equal(transformed.includes("process.on('message'"), false);
    assert.ok(transformed.includes('control.proof.onCommand(async value'));
    assert.ok(transformed.includes("control.proof.emit({ kind: 'proof-listener'"));
    assert.ok(transformed.includes("observeShutdownJoin('requests'"));
    assert.ok(transformed.includes('releaseProofRender();'));
    const path = join(f.root, 'public-server-runtime.mjs');
    writeFileSync(path, transformed);
    execFileSync(process.execPath, ['--check', path]);
  } finally {
    f.remove();
  }
});

test('BFF copied mutants retain their distinct failure and diagnostic pins', () => {
  const source = readFileSync(join(sourceApp, 'scripts/public-server-runtime.mjs'), 'utf8');
  const options = {
    external: undefined,
    uiMode: true,
    isolatedProtocol: false,
    disabledOwnPreference: false,
    fakeIssuer: 'https://identity.example.test/realms/learnstack',
    fakeLogout: 'https://identity.example.test/logout',
    sites: [{ host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' }],
    marker: 'fixture-marker',
    clockBase: 1800000000000,
  };
  assert.ok(
    instrumentCopiedBffRuntime(source, { ...options, mutation: 'shutdown' }).includes(
      'let failed = true;',
    ),
  );
  assert.equal(
    instrumentCopiedBffRuntime(source, { ...options, mutation: 'diagnostics' }).includes(
      "process.env.NEXT_TRACE_SPAN_THRESHOLD_MS = '9007199254740991';",
    ),
    false,
  );
});

test('external BFF copy contains no private proof command registration', () => {
  const source = readFileSync(join(sourceApp, 'scripts/public-server-runtime.mjs'), 'utf8');
  const transformed = instrumentCopiedBffRuntime(source, {
    mutation: undefined,
    external: { issuer: 'https://identity.example.test' },
    uiMode: false,
    isolatedProtocol: false,
    disabledOwnPreference: false,
    fakeIssuer: 'https://identity.example.test',
    fakeLogout: 'https://identity.example.test/logout',
    sites: [],
    marker: 'fixture-marker',
    clockBase: 1800000000000,
  });
  assert.equal(transformed.includes('control.proof.onCommand'), false);
  assert.equal(transformed.includes('proofShutdown'), false);
});

test('admission copied runtime preserves real settled barrier and one shutdown observation', () => {
  const f = temp();
  try {
    const source = readFileSync(join(sourceApp, 'scripts/public-server-runtime.mjs'), 'utf8');
    const transformed = instrumentCopiedAdmissionRuntime(source);
    assert.equal(transformed.includes("process.on('message'"), false);
    assert.ok(transformed.includes("if (message.op === 'settled') await completions.settled()"));
    assert.ok(
      transformed.includes("control.proof.emit({ kind: 'shutdown', counts: admission.counts() })"),
    );
    const path = join(f.root, 'public-server-runtime.mjs');
    writeFileSync(path, transformed);
    execFileSync(process.execPath, ['--check', path]);
  } finally {
    f.remove();
  }
});

test('ingress copied runtime preserves dual output fences and actual bind observation', () => {
  const f = temp();
  try {
    const source = readFileSync(join(sourceApp, 'scripts/public-server-runtime.mjs'), 'utf8');
    const transformed = instrumentCopiedIngressRuntime(source);
    assert.equal(transformed.includes("process.on('message'"), false);
    assert.ok(transformed.includes('Promise.all([process.stdout, process.stderr]'));
    assert.ok(transformed.includes("control.proof.emit({ surface: 'native'"));
    assert.ok(transformed.includes("control.proof.emit({ surface: 'sink'"));
    assert.ok(
      transformed.includes('boundNextVersionFetch(globalThis.fetch, (observation, sequence)'),
    );
    assert.ok(transformed.includes("kind: 'version-fetch', pid: process.pid, sequence"));
    const path = join(f.root, 'public-server-runtime.mjs');
    writeFileSync(path, transformed);
    execFileSync(process.execPath, ['--check', path]);
  } finally {
    f.remove();
  }
});
