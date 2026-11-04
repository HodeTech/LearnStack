// Synthetic protocol controls only: actual copied producers and actual host parsers.
// No Next subject is imported/executed, and no synthetic family/inventory grants runtime proof.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  closeSync,
  constants,
  fstatSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  openSync,
  readFileSync,
  realpathSync,
  rmSync,
  writeFileSync,
  writeSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createContext, Script } from 'node:vm';

import { verifyFrameworkInstallation } from './framework-installation.mjs';
import { instrumentCopiedBffRuntime } from '../apps/web/scripts/bff-proof-host.mjs';
import {
  deriveTransportSpec,
  instrumentCopiedTransport,
} from '../apps/web/scripts/framework-transport-proof-source.mjs';
import { instrumentCopiedRouterMatrix } from '../apps/web/scripts/framework-router-matrix-source.mjs';
import { instrumentCopiedRouterFaults } from '../apps/web/scripts/framework-router-fault-source.mjs';
import { faultCases } from '../apps/web/scripts/framework-router-fault-gate.mjs';
import { validateRouterFaultRows } from '../apps/web/scripts/framework-router-fault-host.mjs';
import { createFaultPipeObservation } from '../apps/web/scripts/framework-router-fault-pipe-host.mjs';
import {
  describeRouterParser,
  loadRouterProofParser,
  routerPipeInventory,
} from '../apps/web/scripts/framework-router-generated-pipes.mjs';

const frontend = fileURLToPath(new URL('../', import.meta.url));
const app = join(frontend, 'apps/web');
const identity = verifyFrameworkInstallation({ appRoot: app });
const parser = describeRouterParser(identity);
const ts = loadRouterProofParser(identity, parser);
const sha = (value) => createHash('sha256').update(value).digest('hex');
const bff = instrumentCopiedBffRuntime(
  readFileSync(join(app, 'scripts/public-server-runtime.mjs'), 'utf8'),
  {
    mutation: undefined,
    external: false,
    uiMode: true,
    isolatedProtocol: true,
    disabledOwnPreference: false,
    fakeIssuer: 'https://identity.example.test/realms/learnstack',
    fakeLogout: 'https://identity.example.test/realms/learnstack/protocol/openid-connect/logout',
    sites: [{ host: 'institution.example.test:3000', displayName: 'Institution', locale: 'en' }],
    marker: 'a'.repeat(43),
    clockBase: 1800000000000,
  },
);
const transport = instrumentCopiedTransport({ source: bff, identity });
const matrix = instrumentCopiedRouterMatrix({ ...transport, identity });
const copied = instrumentCopiedRouterFaults({
  ...matrix,
  owner: readFileSync(join(app, 'scripts/public-server.mjs'), 'utf8'),
  ledger: readFileSync(join(app, 'scripts/framework-router-pipe-ledger.mjs'), 'utf8'),
  requestRuntime: readFileSync(join(app, 'src/server/auth/request-runtime.ts'), 'utf8'),
});
const parse = (name, source) => {
  const file = ts.createSourceFile(name, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.JS);
  assert.equal(file.parseDiagnostics.length, 0);
  return file;
};
const observerFile = parse('copied-observer.mjs', copied.observer);
const ledgerFile = parse('copied-ledger.mjs', copied.ledger);
function one(file, predicate) {
  const found = [];
  const visit = (node) => {
    if (predicate(node)) found.push(node);
    ts.forEachChild(node, visit);
  };
  visit(file);
  assert.equal(found.length, 1, 'Exact copied control source');
  return found[0];
}
const initializer = (file, name) =>
  one(
    file,
    (node) => ts.isVariableDeclaration(node) && node.name.getText(file) === name,
  ).initializer.getText(file);
const attestSource = initializer(observerFile, 'attestPipeFrame');
const pipeSource = initializer(observerFile, 'pipeEvent');
assert.ok(
  copied.observer.includes(
    "trace('entry', point.fileIndex, point.entryIndex, 'router-seen');\n" +
      '        attestPipeFrame(point, frame);\n' +
      '        active.pipeIndex = point.index;\n' +
      '        pipeEvent(point.index);\n' +
      "        callFault(frame,'pipe',point.responseName+','+point.endWorkName);",
  ),
  'Actual producer order: trace, attestation, pipe commit, coordinator milestone',
);
const ledgerFactory = one(
  ledgerFile,
  (node) => ts.isFunctionDeclaration(node) && node.name?.text === 'createGeneratedPipeLedger',
).getText(ledgerFile);
assert.ok(ledgerFactory.startsWith('export function '));
const createLedger = new Script(`(() => {
  const VALUES = ${initializer(ledgerFile, 'VALUES')};
  const PHASES = ${initializer(ledgerFile, 'PHASES')};
  const refuse = ${initializer(ledgerFile, 'refuse')};
  ${ledgerFactory.slice('export '.length)}
  return createGeneratedPipeLedger;
})()`).runInNewContext({
  constants,
  closeSync,
  fstatSync,
  lstatSync,
  openSync,
  realpathSync,
  writeSync,
  join,
  resolve,
  Buffer,
  process,
});
const freeze = (value) => {
  if (value && typeof value === 'object') {
    Object.values(value).forEach(freeze);
    Object.freeze(value);
  }
  return value;
};
const compressionSha256 = deriveTransportSpec(identity).compression.sha256;

function fixture() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'learnstack-router-prefix-control-')));
  const ownedApp = join(root, 'frontend/apps/web');
  mkdirSync(ownedApp, { recursive: true });
  let ledger, pipeDescriptor, attestedDescriptor;
  const close = () => {
    ledger?.close();
    if (pipeDescriptor !== undefined) closeSync(pipeDescriptor);
    if (attestedDescriptor !== undefined) closeSync(attestedDescriptor);
    rmSync(root, { recursive: true, force: true });
  };
  try {
    // Installed entry points use qualified bytes. The extra zero-entry file and manifest
    // are explicitly synthetic parser inputs, not an independently admitted build.
    const files = matrix.pipes.map((file, index) => ({
      index,
      kind: 'installed',
      path: file.path,
      url: pathToFileURL(join(identity.nextRoot, file.path)).href,
      bytes: file.bytes,
      sha256: file.sha256,
      map: null,
      entries: [{ responseName: 'response', point: file.point }],
    }));
    const empty = Buffer.from('export const syntheticNoPipe = true;\n');
    files.push({
      index: files.length,
      kind: 'generated',
      path: 'next/server/prefix-control-empty.js',
      url: pathToFileURL(join(ownedApp, '.next/server/prefix-control-empty.js')).href,
      bytes: empty.length,
      sha256: sha(empty),
      map: null,
      entries: [],
    });
    const sites = matrix.pipes.map((file, index) => ({
      index,
      fileIndex: index,
      entryIndex: 0,
      kind: 'installed',
      path: file.path,
      url: files[index].url,
      bytes: file.bytes,
      sha256: file.sha256,
      manifestSha256: null,
      responseName: 'response',
      point: file.point,
    }));
    const union = freeze({
      format: 1,
      manifestSha256: sha('synthetic prefix-control manifest'),
      censusSha256: identity.censusSha256,
      parser,
      referenceSha256: sha('synthetic prefix-control reference'),
      files,
      sites,
    });
    const ownedIdentity = Object.freeze({ ...identity, appRoot: ownedApp });
    const framework = Object.freeze({ identity: ownedIdentity, mode: 'production' });
    const lease = Object.freeze({ pid: process.ppid, nonce: 'synthetic-prefix-control' });
    const family = Object.freeze({
      pid: process.pid,
      group: process.pid,
      parentPid: lease.pid,
      leaseNonce: lease.nonce,
    });
    let terminal = false;
    const broker = {
      assertHealthy() {},
      capture: { family: () => family, lease: () => lease, retired: () => terminal },
    };
    const path = (name) => join(root, name);
    writeFileSync(
      path('.router-pipe-inventory.json'),
      JSON.stringify({ version: 1, pid: process.pid, inventory: routerPipeInventory(union) }) +
        '\n',
      { mode: 0o600 },
    );
    writeFileSync(path('.router-filter.events'), '', { mode: 0o600 });
    pipeDescriptor = openSync(path('.router-pipe.events'), 'wx', 0o600);
    attestedDescriptor = openSync(path('.router-pipe-attested.events'), 'wx', 0o600);
    ledger = createLedger(
      framework,
      files.map((file) => file.entries.length),
    );
    const point = { ...sites[0], expected: sites[0].point };
    const frame = {
      location: { scriptId: '314', lineNumber: point.point.line, columnNumber: point.point.column },
    };
    const context = createContext({
      Buffer,
      writeSync,
      process: { pid: process.pid },
      active: { name: faultCases[0] },
      pipeDescriptor,
      attestedDescriptor,
      pipeSequence: 0,
      attestedSequence: 0,
      pipeSubjects: sites,
      pipeFiles: files,
      observationFailed: false,
    });
    const attest = new Script('(' + attestSource + ')').runInContext(context);
    const pipe = new Script('(' + pipeSource + ')').runInContext(context);
    assert.equal(ledger.record('script', point.fileIndex, -1, 'setup', 'ready'), true);
    assert.equal(
      ledger.record('resolved', point.fileIndex, point.entryIndex, 'setup', 'match'),
      true,
    );
    const observation = createFaultPipeObservation({
      app: ownedApp,
      broker,
      union,
      compressionSha256,
    });
    return {
      close,
      union,
      point,
      observation,
      entry(index, commit = true) {
        context.active = { name: faultCases[index] };
        const phase = 'case-' + index;
        assert.equal(ledger.record('hit', point.fileIndex, point.entryIndex, phase, 'known'), true);
        if (index === 0)
          assert.equal(ledger.record('source', point.fileIndex, -1, phase, 'match'), true);
        assert.equal(ledger.record('mark', point.fileIndex, point.entryIndex, phase, 'true'), true);
        assert.equal(
          ledger.record('entry', point.fileIndex, point.entryIndex, phase, 'router-seen'),
          true,
        );
        attest(point, frame);
        if (commit) pipe(point.index);
        assert.equal(context.observationFailed, false);
        assert.equal(ledger.summary().failed, false);
      },
      commit() {
        pipe(point.index);
        assert.equal(context.observationFailed, false);
      },
      inspectTerminal() {
        terminal = true;
        return observation.inspect(true);
      },
      corrupt(name, change) {
        const target = path(name);
        const rows = readFileSync(target, 'utf8').trimEnd().split('\n').map(JSON.parse);
        change(rows);
        writeFileSync(target, rows.map((row) => JSON.stringify(row) + '\n').join(''));
      },
    };
  } catch (error) {
    close();
    throw error;
  }
}
const withFixture = (work) => {
  const value = fixture();
  try {
    work(value);
  } finally {
    value.close();
  }
};
const refused = (work) => assert.throws(work, { message: 'Copied router pipe evidence refused' });

test('actual producer interleave accepts an ahead attestation without case credit', () => {
  withFixture((value) => {
    assert.equal(value.observation.inspect().pipes.length, 0);
    value.entry(0);
    assert.equal(value.observation.inspect().attestations.length, 1);
    value.entry(1, false);
    const pending = value.observation.inspect();
    assert.equal(pending.pipes.length, 1);
    assert.equal(pending.attestations.length, 1);
    refused(() => value.observation.assertCase(faultCases[1]));
    value.commit();
    assert.equal(value.observation.assertCase(faultCases[1]).attestations.length, 2);
  });
});

test('uncommitted attestation cannot satisfy case or retirement milestones', () => {
  withFixture((value) => {
    value.entry(0, false);
    const pending = value.observation.inspect();
    assert.equal(pending.pipes.length, 0);
    assert.equal(pending.attestations.length, 0);
    refused(() => value.observation.assertCase(faultCases[0]));
    const rows = ['arm', 'route', 'pull', 'retired'].map((k, index) => ({
      v: 1,
      p: process.pid,
      n: index + 1,
      c: faultCases[0],
      k,
      x: null,
    }));
    assert.throws(
      () => validateRouterFaultRows(rows, { pid: process.pid, union: value.union }),
      /Required fault milestone pipe/,
    );
  });
});

test('complete eight-case producer prefix passes terminal parser checks', () => {
  withFixture((value) => {
    for (let index = 0; index < 8; index++) value.entry(index);
    const result = value.inspectTerminal();
    assert.equal(result.pipes.length, 8);
    assert.equal(result.attestations.length, 8);
  });
});

test('eighth ahead attestation stays pending and terminal inspection refuses it', () => {
  withFixture((value) => {
    for (let index = 0; index < 7; index++) value.entry(index);
    value.entry(7, false);
    assert.equal(value.observation.inspect().attestations.length, 7);
    refused(() => value.observation.assertCase(faultCases[7]));
    refused(() => value.inspectTerminal());
  });
});

test('two ahead attestations refuse before terminal inspection', () => {
  withFixture((value) => {
    value.entry(0);
    value.entry(1, false);
    value.entry(2, false);
    refused(() => value.observation.inspect());
  });
});

for (const terminal of [false, true]) {
  test(
    'missing committed attestation refuses ' +
      (terminal ? 'terminal' : 'nonterminal') +
      ' inspection',
    () => {
      withFixture((value) => {
        for (let index = 0; index < (terminal ? 8 : 2); index++) value.entry(index);
        value.corrupt('.router-pipe-attested.events', (rows) => rows.pop());
        refused(() => (terminal ? value.inspectTerminal() : value.observation.inspect()));
      });
    },
  );
}

const changes = [
  [
    'missing trace entry',
    (value) => value.corrupt('.router-pipe-trace.events', (rows) => rows.pop()),
  ],
  [
    'source hash',
    (value) =>
      value.corrupt('.router-pipe-attested.events', (rows) => {
        rows.at(-1).sha256 = '0'.repeat(64);
      }),
  ],
  [
    'excluded endpoint',
    (value) =>
      value.corrupt('.router-pipe-attested.events', (rows) => {
        rows.at(-1).line = value.point.point.endLine;
        rows.at(-1).column = value.point.point.endColumn;
      }),
  ],
  [
    'case identity',
    (value) =>
      value.corrupt('.router-pipe-attested.events', (rows) => {
        rows.at(-1).case = faultCases[2];
      }),
  ],
  [
    'script identity',
    (value) =>
      value.corrupt('.router-pipe-attested.events', (rows) => {
        rows.at(-1).scriptId = '315';
      }),
  ],
  [
    'site without matching entry',
    (value) =>
      value.corrupt('.router-pipe-attested.events', (rows) => {
        const site = value.union.sites[1];
        Object.assign(rows.at(-1), {
          index: site.index,
          sha256: site.sha256,
          scriptId: '315',
          line: site.point.line,
          column: site.point.column,
        });
      }),
  ],
];
for (const [name, change] of changes) {
  test('ahead attestation with ' + name + ' refuses', () => {
    withFixture((value) => {
      value.entry(0);
      value.entry(1, false);
      change(value);
      refused(() => value.observation.inspect());
    });
  });
}
