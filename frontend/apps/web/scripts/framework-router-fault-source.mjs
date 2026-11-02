// Closed copied-source transformations applied before genuine generation.
import assert from 'node:assert/strict';

import { copiedRouterFaultRouteSource } from './framework-router-fault-route.mjs';

function replace(source, before, after) {
  assert.equal(source.split(before).length, 2, 'Exact copied fault source anchor');
  return source.replace(before, after);
}
function observerSource(source) {
  let text = replace(
    source,
    "import zlib from 'node:zlib';",
    `import zlib from 'node:zlib';
import { faultCases } from './framework-router-fault-gate.mjs';
import { createRouterFaultObserver } from './framework-router-fault-runtime.mjs';
import { deriveFaultJoinPoints, deriveFaultBoundaryPoints } from './framework-router-fault-join-points.mjs';`,
  );
  text = replace(
    text,
    `const CASES = new Set([
  'healthy-gzip',
  'healthy-deflate',
  'healthy-identity',
  'fence-gzip',
  'fence-deflate',
]);`,
    'const CASES = new Set(faultCases);',
  );
  text = replace(
    text,
    '  const pipeFiles = pipeUnion.files;',
    `  const pipeFiles = pipeUnion.files;
  const faultJoins = deriveFaultJoinPoints(identity, pipeFiles, SPEC.parser);
  const faultBoundaries = deriveFaultBoundaryPoints(identity, SPEC);`,
  );
  text = replace(
    text,
    '    gzip: zlib.createGzip,',
    `    gzipDescriptor: Object.getOwnPropertyDescriptor(zlib, 'createGzip'),
    deflateDescriptor: Object.getOwnPropertyDescriptor(zlib, 'createDeflate'),
    gzipOnceDescriptor: Object.getOwnPropertyDescriptor(zlib.Gzip.prototype, 'once'),
    deflateOnceDescriptor: Object.getOwnPropertyDescriptor(zlib.Deflate.prototype, 'once'),
    gzip: zlib.createGzip,`,
  );
  text = replace(
    text,
    '  const pendingHandlers = new Set();',
    `  const pendingHandlers = new Set();
  let fault;
  const observeFault = (method,...args) => {
    try { return fault[method](...args); }
    catch { observationFailed=true; fault?.fail(); }
  };`,
  );
  text = replace(
    text,
    'if (!/^[0-9]{1,16}$/.test(scriptId) || ++attestedSequence > 3)',
    'if (!/^[0-9]{1,16}$/.test(scriptId) || ++attestedSequence > 8)',
  );
  text = replace(text, '++pipeSequence > 3', '++pipeSequence > 8');
  text = replace(text, '++filterSequence > 3', '++filterSequence > 8');
  text = replace(
    text,
    "pipePhase = ['healthy-identity','healthy-gzip','healthy-deflate'].includes(name) ? 'case-' + ['healthy-identity','healthy-gzip','healthy-deflate'].indexOf(name) : 'closing';",
    "pipePhase = 'case-' + faultCases.indexOf(name);",
  );
  text = replace(
    text,
    `  function settleIfDone() {
    if (
      !active ||
      !active.handlerSettled ||
      !active.responseClosed ||
      active.pipeIndex === undefined ||
      (active.allocated ? !active.encoderClosed : !active.noCompression)
    )
      return;
    delete active.response[MARK];
    active = undefined;
  }`,
    `  function settleIfDone() {
    if (!active || !active.handlerSettled || !active.responseClosed ||
        active.pipeIndex === undefined || !fault.ready(active.response)) return;
    delete active.response[MARK];
    active = undefined;
  }`,
  );
  text = replace(
    text,
    "  inspector.on('Debugger.paused', ({ params }) => {",
    `  const callFault = (frame,method,args) => {
    let synchronous=false, accepted=false;
    inspector.post('Debugger.evaluateOnCallFrame', {
      callFrameId:frame.callFrameId,
      expression:'globalThis[Symbol.for("learnstack.routerFaultObserver")].'+method+'('+args+'); true',
      returnByValue:true,throwOnSideEffect:false,
    },(error,result)=>{synchronous=true;accepted=!error&&!result?.exceptionDetails&&result?.result?.value===true;});
    if(!synchronous||!accepted) throw new Error('Copied fault frame refused');
  };
  const verifyFaultFrame = (point,frame) => {
    if(point.fileIndex!==undefined) verifyPipeFrame(point,frame);
    else if(frame?.location?.scriptId!==scripts.get(point.key)||!inRange(frame.location,point.expected))
      throw new Error('Copied owner frame refused');
  };
  inspector.on('Debugger.paused', ({ params }) => {`,
  );
  text = replace(
    text,
    `      const expression = point.kind === 'pipe'
        ? point.responseName + '.' + MARK + ' === 1'
        : point.kind === 'router' ? 'res.' + MARK + ' === 1' : 'response.' + MARK + ' === 1';`,
    `      if(point.kind!=='pipe' && point.kind!=='router' && point.kind!=='allocation' && point.kind!=='close' && point.kind!=='none') verifyFaultFrame(point,frame);
      const expression = point.responseName
        ? point.responseName + '.' + MARK + ' === 1'
        : point.kind === 'router' ? 'res.' + MARK + ' === 1' : 'response.' + MARK + ' === 1';`,
  );
  text = replace(
    text,
    `      if (point.kind === 'pipe') {
        if (!active.router || active.pipeIndex !== undefined || !pipeVerified.has(point.fileIndex)) {`,
    `      if (point.kind === 'router-register') {
        callFault(frame,'registrationPath','req,res,compress');
      } else if (point.kind === 'owner-install') {
        callFault(frame,'ownerInstall','response,owner,onResponseTerminal');
      } else if (point.kind === 'owner-cleanup') {
        callFault(frame,'cleanup','response,onResponseTerminal');
      } else if (point.kind === 'owner-transition') {
        callFault(frame,'transition','response,error,abortResponse');
      } else if (point.kind === 'owner-drain') {
        callFault(frame,'drain','response,listener');
      } else if (point.kind === 'pipe-join' || point.kind === 'router-join') {
        const phase=point.kind==='pipe-join'?'pipe':'router';
        const witness={phase,index:point.index??-1,scriptId:frame.location.scriptId,
          sha256:point.fileIndex===undefined?SPEC.router.sha256:pipeFiles[point.fileIndex].sha256,
          line:frame.location.lineNumber,column:frame.location.columnNumber};
        callFault(frame,'join',point.responseName+','+point.ownerName+','+point.endWorkName+','+JSON.stringify(witness));
      } else if (point.kind === 'pipe-result' || point.kind === 'router-result') {
        callFault(frame,'result',point.responseName+','+JSON.stringify(point.kind==='pipe-result'?'pipe':'router')+','+point.names.join(','));
      } else if (point.kind === 'pipe') {
        if (!active.router || active.pipeIndex !== undefined || !pipeVerified.has(point.fileIndex)) {`,
  );
  text = replace(
    text,
    '        pipeEvent(point.index);',
    `        pipeEvent(point.index);
        callFault(frame,'pipe',point.responseName+','+point.endWorkName);`,
  );
  text = replace(
    text,
    "        event('allocation', type === 'Gzip' ? 'gzip' : 'deflate');",
    `        event('allocation', type === 'Gzip' ? 'gzip' : 'deflate');
        callFault(frame,'allocation','response,stream');`,
  );
  text = replace(
    text,
    "        event('encoder-close', ended ? 'ended' : 'unended');",
    `        event('encoder-close', ended ? 'ended' : 'unended');
        callFault(frame,'encoderClose','response,stream');`,
  );
  text = replace(
    text,
    `    } catch {
      observationFailed = true;
    } finally {
      try {
        inspector.post('Debugger.resume'`,
    `    } catch {
      observationFailed = true;
      fault?.fail();
    } finally {
      try {
        inspector.post('Debugger.resume'`,
  );
  text = replace(
    text,
    '    inspector.connect();\n    inspectorConnected = true;',
    `    fault = createRouterFaultObserver(framework,()=>{observationFailed=true;});
    inspector.connect();
    inspectorConnected = true;`,
  );
  text = replace(
    text,
    "points.set(result.breakpointId, {kind:'pipe',index:subject.index,fileIndex:subject.fileIndex,entryIndex:subject.entryIndex,responseName:subject.responseName,expected:subject.point});",
    "points.set(result.breakpointId, {kind:'pipe',index:subject.index,fileIndex:subject.fileIndex,entryIndex:subject.entryIndex,responseName:subject.responseName,endWorkName:faultJoins.find(item=>item.fileIndex===subject.fileIndex&&item.entryIndex===subject.entryIndex).endWorkName,expected:subject.point});",
  );
  text = replace(
    text,
    '    breakpointsReady = true;',
    `    const installFaultPoint = async (url,point) => {
      const result=await post('Debugger.setBreakpointByUrl',{url,lineNumber:point.expected.line,columnNumber:point.expected.column});
      if(!result?.breakpointId||points.has(result.breakpointId)) refuse();
      points.set(result.breakpointId,point);
      for(const location of result.locations??[]) {
        if(!inRange(location,point.expected)||resolved.has(result.breakpointId)) refuse();
        resolved.add(result.breakpointId);
      }
    };
    for(const join of faultJoins) {
      const site=pipeSubjects.find(item=>item.fileIndex===join.fileIndex&&item.entryIndex===join.entryIndex);
      if(!site)refuse();
      await installFaultPoint(pipeFiles[join.fileIndex].url,{...join,index:site.index,kind:'pipe-join',expected:join.joinPoint});
      await installFaultPoint(pipeFiles[join.fileIndex].url,{...join,index:site.index,kind:'pipe-result',names:join.result.names,expected:join.result.point});
    }
    for(const [kind,name] of [['owner-install','ownerInstall'],['owner-cleanup','cleanup'],['owner-transition','transition'],['owner-drain','drain']])
      await installFaultPoint(actual.compression.url,{kind,key:'compression',expected:faultBoundaries[name]});
    await installFaultPoint(actual.router.url,{kind:'router-register',key:'router',responseName:'res',expected:faultBoundaries.routerRegistration});
    await installFaultPoint(actual.router.url,{kind:'router-join',key:'router',responseName:'response',ownerName:'owner',endWorkName:'explicitEndWork',expected:faultBoundaries.routerJoin});
    await installFaultPoint(actual.router.url,{kind:'router-result',key:'router',responseName:'response',names:faultBoundaries.routerResult.names,expected:faultBoundaries.routerResult.point});
    breakpointsReady = true;`,
  );
  text = replace(
    text,
    "        [...points].filter(([,point]) => point.kind !== 'pipe')",
    "        [...points].filter(([,point]) => !['pipe','pipe-join','pipe-result'].includes(point.kind))",
  );
  text = replace(
    text,
    "            response.once('finish', () => event('response-finish'));",
    `            fault.arm(name,response);
            response.once('finish', () => {
              observeFault('finish',response);
              event('response-finish');
            });`,
  );
  text = replace(
    text,
    "                event('response-close');",
    `                observeFault('responseClose',response);
                event('response-close');`,
  );
  text = replace(
    text,
    "if (selected && ['healthy-gzip','healthy-deflate'].includes(active.name))",
    "if (selected && active.name.endsWith('-gzip'))",
  );
  text = replace(
    text,
    "            active.handlerSettled = true;\n            event('handler-failed');",
    "            active.handlerSettled = true;\n            observeFault('handler',response,true);\n            event('handler-failed');",
  );
  text = replace(
    text,
    "                  event('handler-ok');",
    "                  observeFault('handler',response,false);\n                  event('handler-ok');",
  );
  text = replace(
    text,
    "                  event('handler-failed');",
    "                  observeFault('handler',response,true);\n                  event('handler-failed');",
  );
  text = replace(
    text,
    '        if (pipeSequence !== 3 || pipeScripts.size === 0)',
    '        if (pipeSequence !== 8 || pipeScripts.size === 0)',
  );
  text = replace(
    text,
    '  function settleIfDone() {',
    `  function terminalQualification() {
    const sameDescriptor = (left,right) => left === undefined || right === undefined
      ? left === right
      : ['value','get','set','writable','enumerable','configurable'].every(key=>left[key]===right[key]);
    if (observationFailed || fault.failed() || !confirmed || !breakpointsReady || !inspectorConnected ||
        pipeTrace?.summary().failed || scripts.size !== 2 || pipeSequence !== 8 || pipeScripts.size === 0 ||
        !active || active.name !== 'constructor-failure-gzip' || active.handlerSettled || pendingHandlers.size !== 1 ||
        zlib.createGzip !== native.gzip || zlib.createDeflate !== native.deflate ||
        zlib.Gzip.prototype.once !== native.gzipOnce || zlib.Deflate.prototype.once !== native.deflateOnce ||
        !sameDescriptor(Object.getOwnPropertyDescriptor(zlib,'createGzip'),native.gzipDescriptor) ||
        !sameDescriptor(Object.getOwnPropertyDescriptor(zlib,'createDeflate'),native.deflateDescriptor) ||
        !sameDescriptor(Object.getOwnPropertyDescriptor(zlib.Gzip.prototype,'once'),native.gzipOnceDescriptor) ||
        !sameDescriptor(Object.getOwnPropertyDescriptor(zlib.Deflate.prototype,'once'),native.deflateOnceDescriptor)) refuse();
    const loadedSource = scriptId => {
      let synchronous=false, accepted=false, bytes;
      inspector.post('Debugger.getScriptSource',{scriptId},(error,result)=>{
        synchronous=true;
        if (!error && typeof result?.scriptSource==='string') {
          bytes=Buffer.from(result.scriptSource); accepted=true;
        }
      });
      if (!synchronous || !accepted) refuse();
      return bytes;
    };
    for (const [key,subject] of Object.entries(actual)) {
      if (sha(loadedSource(scripts.get(key))) !== sha(subject.bytes)) refuse();
    }
    for (const [index,scriptId] of pipeScripts) {
      if (sha(loadedSource(scriptId)) !== pipeFiles[index].sha256) refuse();
    }
    // A synchronous inspector callback can set either sticky flag during the reads.
    if (observationFailed || fault.failed() || pipeTrace?.summary().failed) refuse();
    return true;
  }
  function settleIfDone() {`,
  );
  text = replace(
    text,
    '    async close() {',
    `    stop:()=>fault.stop(),
    watchdog:()=>{
      try { fault.watchdog(terminalQualification()); }
      catch { observationFailed=true; fault.fail(); refuse(); }
    },
    async close() {`,
  );
  text = replace(
    text,
    '        inspector.disconnect();\n        if (attestedDescriptor',
    '        inspector.disconnect();\n        fault.close();\n        if (attestedDescriptor',
  );
  return text;
}

export function instrumentCopiedRouterFaults({ runtime, observer, ledger, requestRuntime, owner }) {
  const copiedOwner = replace(
    "import { writeFileSync as writeRouterFaultResult } from 'node:fs';\n" + owner,
    '  const graceful =',
    `  writeRouterFaultResult(new URL('../../../../.router-runtime-result.json', import.meta.url),
    JSON.stringify({version:1,ownerPid:process.pid,leaseNonce:lease.record.nonce,receipt}) + '\\n',
    {flag:'wx',mode:0o600});
  const graceful =`,
  );
  let copiedRuntime = replace(
    runtime,
    '    proofShutdown.watchdog = true;',
    '    transportProof?.watchdog();\n    proofShutdown.watchdog = true;',
  );
  copiedRuntime = replace(
    copiedRuntime,
    '  const requestNativeStops = () => {',
    '  const requestNativeStops = () => {\n    transportProof?.stop();',
  );
  return Object.freeze({
    runtime: copiedRuntime,
    owner: copiedOwner,
    observer: observerSource(observer),
    route: copiedRouterFaultRouteSource,
    ledger: replace(
      ledger,
      "const PHASES = new Set(['setup', 'prepared', 'case-0', 'case-1', 'case-2', 'closing']);",
      "const PHASES = new Set(['setup','prepared','case-0','case-1','case-2','case-3','case-4','case-5','case-6','case-7','closing']);",
    ),
    requestRuntime: replace(
      requestRuntime,
      '        const actual = Promise.resolve(promise);',
      `        const capture = (globalThis as unknown as Record<symbol,
          { capture(value: Promise<unknown>, response: unknown): void }>)[Symbol.for('learnstack.routerFaultAggregateCapture')];
        capture?.capture(promise,response);
        const actual = Promise.resolve(promise);`,
    ),
  });
}
