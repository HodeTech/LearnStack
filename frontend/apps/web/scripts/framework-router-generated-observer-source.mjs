// Fixed transforms of the already reviewed three-case copied observer.
import assert from 'node:assert/strict';

import { describeRouterParser } from './framework-router-generated-pipes.mjs';

function replace(source, before, after) {
  assert.equal(source.split(before).length, 2, 'Exact generated pipe observer anchor');
  return source.replace(before, after);
}

export function instrumentGeneratedRouterObserver(source, identity) {
  const match = /^const SPEC = (\{[^\n]*\});$/m.exec(source);
  assert.ok(match);
  const spec = JSON.parse(match[1]);
  let text = replace(
    source,
    match[0],
    'const SPEC = ' + JSON.stringify({ ...spec, parser: describeRouterParser(identity) }) + ';',
  );
  text = replace(
    text,
    "import { createHash } from 'node:crypto';",
    "import { createHash } from 'node:crypto';\n" +
      "import { deriveRouterPipeUnion, routerPipeInventory } from './framework-router-generated-pipes.mjs';\n" +
      "import { createGeneratedPipeLedger } from './framework-router-pipe-ledger.mjs';",
  );
  const start = text.indexOf('  const pipeSubjects = SPEC.pipes.map(');
  const end = text.indexOf('  const pipeLedgerPath =', start);
  assert.ok(start > 0 && end > start);
  text =
    text.slice(0, start) +
    `  const pipeUnion = deriveRouterPipeUnion(framework, SPEC.pipes, SPEC.parser);
  const pipeSubjects = pipeUnion.sites;
  const pipeFiles = pipeUnion.files;
  const inventory = routerPipeInventory(pipeUnion);
` +
    text.slice(end);
  text = replace(
    text,
    '  const pipeScripts = new Map();',
    `  const pipeScripts = new Map();
  const pipeVerified = new Set();
  let attestedDescriptor;
  let attestedSequence = 0;
  let pipeTrace;
  let pipePhase = 'setup';
  const trace = (kind, subject, point, value) => {
    if (!pipeTrace?.record(kind, subject, point, pipePhase, value)) observationFailed = true;
  };
  const pipeFault = (reason) => { pipeTrace?.fault(reason, pipePhase); observationFailed = true; };
  const verifyPipeFrame = (point, frame) => {
    if (frame?.location?.scriptId !== pipeScripts.get(point.fileIndex) || !inRange(frame.location, point.expected)) {
      pipeFault('range-mismatch'); throw new Error('Pipe frame mismatch');
    }
    if (!pipeVerified.has(point.fileIndex)) {
      let synchronous = false, matched = false;
      inspector.post('Debugger.getScriptSource', {scriptId: frame.location.scriptId}, (error, result) => {
        synchronous = true;
        matched = !error && typeof result?.scriptSource === 'string' &&
          sha(Buffer.from(result.scriptSource)) === pipeFiles[point.fileIndex].sha256;
      });
      trace('source', point.fileIndex, -1, matched && synchronous ? 'match' : 'mismatch');
      if (!matched || !synchronous) { pipeFault('source-mismatch'); throw new Error('Pipe source mismatch'); }
      pipeVerified.add(point.fileIndex);
    }
  };
  const attestPipeFrame = (point, frame) => {
    const scriptId = frame.location.scriptId;
    if (!/^[0-9]{1,16}$/.test(scriptId) || ++attestedSequence > 3) throw new Error('Pipe attestation refused');
    const line = JSON.stringify({version:1,pid:process.pid,sequence:attestedSequence,case:active.name,
      index:point.index,scriptId,sha256:pipeFiles[point.fileIndex].sha256,
      line:frame.location.lineNumber,column:frame.location.columnNumber}) + '\\n';
    if (Buffer.byteLength(line) > 384 || writeSync(attestedDescriptor,line) !== Buffer.byteLength(line))
      throw new Error('Pipe attestation refused');
  };`,
  );
  text = replace(text, 'index >= 27 ||', 'index >= pipeSubjects.length ||');
  text = replace(
    text,
    `    for (const subject of pipeSubjects)
      if (params.url === subject.url) {
        if (!breakpointsReady || pipeScripts.has(subject.index)) observationFailed = true;
        else pipeScripts.set(subject.index, params.scriptId);
      }`,
    `    for (const subject of pipeFiles)
      if (params.url === subject.url) {
        const duplicate = pipeScripts.has(subject.index);
        trace('script', subject.index, -1, duplicate ? 'duplicate' : breakpointsReady ? 'ready' : 'early');
        if (!breakpointsReady || duplicate) pipeFault(duplicate ? 'duplicate-script' : 'early-script');
        else pipeScripts.set(subject.index, params.scriptId);
      }`,
  );
  text = replace(
    text,
    '    const point = points.get(params.breakpointId);',
    `    const point = points.get(params.breakpointId);
    if (point?.kind === 'pipe') {
      const duplicate = resolved.has(params.breakpointId), matches = inRange(params.location, point.expected);
      trace('resolved', point.fileIndex, point.entryIndex, duplicate ? 'duplicate' : matches ? 'match' : 'range');
      if (duplicate || !matches) pipeFault(duplicate ? 'duplicate-resolution' : 'range-mismatch');
    }`,
  );
  text = replace(
    text,
    "      const expression = point.kind === 'router' ? `res.${MARK} === 1` : `response.${MARK} === 1`;",
    `      if (point.kind === 'pipe') {
        trace('hit', point.fileIndex, point.entryIndex, 'known');
        verifyPipeFrame(point, frame);
      }
      const expression = point.kind === 'pipe'
        ? point.responseName + '.' + MARK + ' === 1'
        : point.kind === 'router' ? 'res.' + MARK + ' === 1' : 'response.' + MARK + ' === 1';`,
  );
  text = replace(
    text,
    '      if (!frame || testExpression(frame, expression) !== true) return;',
    `      let mark;
      try { mark = frame && testExpression(frame, expression); }
      catch (error) {
        if (point.kind === 'pipe') { trace('mark', point.fileIndex, point.entryIndex, 'unavailable'); pipeFault('mark-unavailable'); }
        throw error;
      }
      if (point.kind === 'pipe') {
        trace('mark', point.fileIndex, point.entryIndex, mark === true ? 'true' : mark === false ? 'false' : 'unavailable');
        if (typeof mark !== 'boolean') pipeFault('mark-unavailable');
      }
      if (mark !== true) return;`,
  );
  text = replace(
    text,
    `        if (!active.router || active.pipeIndex !== undefined ||
            !pipeScripts.has(point.index)) throw new Error('Pipe attribution mismatch');
        active.pipeIndex = point.index;`,
    `        if (!active.router || active.pipeIndex !== undefined || !pipeVerified.has(point.fileIndex)) {
          pipeFault(!active.router ? 'pipe-before-router' : 'duplicate-pipe');
          throw new Error('Pipe attribution mismatch');
        }
        trace('entry', point.fileIndex, point.entryIndex, 'router-seen');
        attestPipeFrame(point, frame);
        active.pipeIndex = point.index;`,
  );
  text = replace(
    text,
    "points.set(result.breakpointId, {kind:'pipe',index:subject.index,expected:subject.point});",
    "points.set(result.breakpointId, {kind:'pipe',index:subject.index,fileIndex:subject.fileIndex,entryIndex:subject.entryIndex,responseName:subject.responseName,expected:subject.point});",
  );
  text = replace(
    text,
    '    inspector.connect();\n    inspectorConnected = true;',
    `    pipeTrace = createGeneratedPipeLedger(framework, pipeFiles.map(file => file.entries.length));
    const inventoryBytes = JSON.stringify({version:1,pid:process.pid,inventory}) + '\\n';
    if (Buffer.byteLength(inventoryBytes) > 262144) refuse();
    const inventoryDescriptor = openSync(join(root, '.router-pipe-inventory.json'),
      constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
    try { if (writeSync(inventoryDescriptor, inventoryBytes) !== Buffer.byteLength(inventoryBytes)) refuse(); }
    finally { closeSync(inventoryDescriptor); }
    attestedDescriptor = openSync(join(root, '.router-pipe-attested.events'),
      constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW, 0o600);
    inspector.connect();
    inspectorConnected = true;`,
  );
  text = replace(
    text,
    '      confirmed = true;',
    "      confirmed = true;\n      pipePhase = 'prepared';",
  );
  text = replace(
    text,
    '          if (name) {',
    "          if (name) {\n            pipePhase = ['healthy-identity','healthy-gzip','healthy-deflate'].includes(name) ? 'case-' + ['healthy-identity','healthy-gzip','healthy-deflate'].indexOf(name) : 'closing';",
  );
  text = replace(
    text,
    '      closed = true;',
    "      closed = true;\n      pipePhase = 'closing';",
  );
  text = replace(
    text,
    '        if (\n          active ||',
    "        if (active) pipeTrace?.fault('close-active', pipePhase);\n        if (pipeTrace?.summary().failed) observationFailed = true;\n        if (\n          active ||",
  );
  text = replace(
    text,
    `        if (pipeSequence !== 3 || pipeScripts.size === 0) refuse();
        for (const [index, scriptId] of pipeScripts) {
          const source = await post('Debugger.getScriptSource', {scriptId});
          if (typeof source.scriptSource !== 'string' ||
              sha(Buffer.from(source.scriptSource)) !== sha(pipeSubjects[index].bytes)) refuse();
        }`,
    `        if (pipeSequence !== 3 || pipeScripts.size === 0) { pipeTrace?.fault('incomplete-cases', pipePhase); refuse(); }
        for (const [index, scriptId] of pipeScripts) {
          const source = await post('Debugger.getScriptSource', {scriptId});
          if (typeof source.scriptSource !== 'string' || sha(Buffer.from(source.scriptSource)) !== pipeFiles[index].sha256) {
            pipeTrace?.fault('source-mismatch', pipePhase); refuse();
          }
        }`,
  );
  text = replace(
    text,
    '    if (filterDescriptor !== undefined) closeSync(filterDescriptor);\n    if (pipeDescriptor',
    '    if (attestedDescriptor !== undefined) closeSync(attestedDescriptor);\n    pipeTrace?.close();\n    if (filterDescriptor !== undefined) closeSync(filterDescriptor);\n    if (pipeDescriptor',
  );
  text = replace(
    text,
    '        if (filterDescriptor !== undefined) closeSync(filterDescriptor);\n        if (pipeDescriptor',
    '        if (attestedDescriptor !== undefined) closeSync(attestedDescriptor);\n        pipeTrace?.close();\n        if (filterDescriptor !== undefined) closeSync(filterDescriptor);\n        if (pipeDescriptor',
  );
  return text;
}
