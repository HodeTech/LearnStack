// PRIVATE candidate. Caller must independently authenticate the fixed sidecar inode
// and bind pid to the actual admitted child before applying this finite validator.
import assert from 'node:assert/strict';

const events = new Set([
  'armed',
  'reserve-b',
  'reserve-a',
  'track-b',
  'track-a',
  'bounded-b-tracked',
  'bounded-b-fulfilled',
  'bounded-b-rejected',
  'track-b-fulfilled',
  'track-b-rejected',
  'track-a-fulfilled',
  'track-a-rejected',
  'handler-b-fulfilled',
  'handler-b-rejected',
  'handler-a-fulfilled',
  'handler-a-rejected',
  'frame-b-cleaned',
  'frame-a-cleaned',
  'lease-b-cleaned',
  'lease-a-cleaned',
  'api-entered',
  'api-tracked',
  'api-fulfilled',
  'api-rejected',
  'framework-registered',
  'framework-settled',
  'render-entered',
  'render-settled',
  'snapshot-tracked',
  'snapshot-settled',
  'response-closed',
  'encoder-closed',
  'native-stop-started',
  'native-stop-fulfilled',
  'native-stop-rejected',
  'publish-b',
  'release-render',
  'release-framework',
  'release-snapshot',
  'release-api',
  'snapshot',
]);
const stateKeys = [
  'aHandler',
  'bHandler',
  'aFrame',
  'bFrame',
  'aLease',
  'bLease',
  'bPublished',
  'api',
  'framework',
  'render',
  'snapshot',
  'stop',
  'responseClosed',
  'encoderClosed',
  'reserved',
  'releaseIndex',
  'diagnosticFault',
  'stageAcknowledged',
  'bTracked',
  'bBounded',
].sort();
const exactKeys = (value, keys) =>
  value &&
  typeof value === 'object' &&
  !Array.isArray(value) &&
  JSON.stringify(Object.keys(value).sort()) === JSON.stringify(keys);
function validState(state) {
  if (!exactKeys(state, stateKeys)) return false;
  if (
    !['unknown', 'fulfilled', 'rejected'].includes(state.aHandler) ||
    !['unknown', 'fulfilled', 'rejected'].includes(state.bHandler)
  )
    return false;
  for (const name of [
    'aFrame',
    'bFrame',
    'aLease',
    'bLease',
    'bPublished',
    'responseClosed',
    'encoderClosed',
    'diagnosticFault',
    'stageAcknowledged',
    'bBounded',
  ])
    if (typeof state[name] !== 'boolean') return false;
  for (const name of ['api', 'framework', 'render', 'snapshot'])
    if (!['unstarted', 'pending', 'fulfilled', 'rejected'].includes(state[name])) return false;
  if (!['unstarted', 'pending', 'fulfilled', 'rejected'].includes(state.stop)) return false;
  return (
    Number.isInteger(state.reserved) &&
    state.reserved >= 0 &&
    state.reserved <= 2 &&
    Number.isInteger(state.releaseIndex) &&
    state.releaseIndex >= 0 &&
    state.releaseIndex <= 4 &&
    Number.isInteger(state.bTracked) &&
    state.bTracked >= 0 &&
    state.bTracked <= 128
  );
}
export function validateCopiedOverlapRows(text, childPid) {
  assert.ok(typeof text === 'string' && Buffer.byteLength(text) <= 65536 && text.endsWith('\n'));
  assert.ok(Number.isSafeInteger(childPid) && childPid > 0);
  const lines = text.slice(0, -1).split('\n');
  assert.ok(lines.length > 0 && lines.length <= 128);
  const rows = lines.map((line, index) => {
    assert.ok(Buffer.byteLength(line) <= 511);
    const row = JSON.parse(line);
    assert.ok(exactKeys(row, ['event', 'pid', 'sequence', 'state', 'version']));
    assert.equal(row.version, 1);
    assert.equal(row.pid, childPid);
    assert.equal(row.sequence, index + 1);
    assert.ok(events.has(row.event));
    assert.ok(row.event === 'snapshot' ? validState(row.state) : row.state === null);
    return row;
  });
  return Object.freeze(rows.map((row) => Object.freeze(row)));
}
/** Call only on a complete, independently authenticated snapshot after native stop. */
export function assertCompletedOverlap(rows) {
  const one = (event) => {
    const found = rows.filter((row) => row.event === event);
    assert.equal(found.length, 1, event);
    return found[0].sequence;
  };
  const either = (a, b) => {
    const found = rows.filter((row) => row.event === a || row.event === b);
    assert.equal(found.length, 1, a);
    return found[0].sequence;
  };
  const before = (a, b) => assert.ok(a < b, 'observed overlap event order');
  assert.equal(rows.filter((row) => row.event === 'publish-b').length, 0);
  assert.equal(rows.filter((row) => row.event === 'native-stop-rejected').length, 0);
  const b = one('reserve-b'),
    a = one('reserve-a');
  before(b, a);
  const api = one('api-entered'),
    apiTrack = one('api-tracked'),
    bounded = one('bounded-b-tracked');
  before(b, api);
  before(api, apiTrack);
  before(b, bounded);
  const encoder = one('encoder-closed'),
    response = one('response-closed'),
    stop = one('native-stop-started');
  before(a, encoder);
  before(a, response);
  before(encoder, stop);
  before(response, stop);
  before(bounded, stop);
  const ready = rows.filter((row) => row.event === 'snapshot' && row.state.stageAcknowledged);
  assert.deepEqual(
    ready.map((row) => row.state.releaseIndex),
    [0, 1, 2, 3],
  );
  for (const row of ready) assert.equal(row.state.diagnosticFault, false);
  const rr = one('release-render'),
    fr = one('release-framework'),
    sr = one('release-snapshot'),
    ar = one('release-api');
  before(stop, ready[0].sequence);
  before(ready[0].sequence, rr);
  before(rr, one('render-settled'));
  before(one('render-settled'), ready[1].sequence);
  before(ready[1].sequence, fr);
  before(fr, one('framework-settled'));
  before(one('framework-settled'), ready[2].sequence);
  before(ready[2].sequence, sr);
  before(sr, one('snapshot-settled'));
  before(one('snapshot-settled'), one('lease-a-cleaned'));
  before(one('lease-a-cleaned'), ready[3].sequence);
  before(ready[3].sequence, ar);
  before(ar, either('api-fulfilled', 'api-rejected'));
  before(ar, either('bounded-b-fulfilled', 'bounded-b-rejected'));
  const bLease = one('lease-b-cleaned');
  before(either('bounded-b-fulfilled', 'bounded-b-rejected'), bLease);
  const stopped = one('native-stop-fulfilled');
  before(bLease, stopped);
  before(one('lease-a-cleaned'), stopped);
  before(one('frame-a-cleaned'), one('lease-a-cleaned'));
  before(one('frame-b-cleaned'), bLease);
  either('handler-a-fulfilled', 'handler-a-rejected');
  either('handler-b-fulfilled', 'handler-b-rejected');
  return Object.freeze({ nativeStop: 'fulfilled', reservations: 'settled', bPublished: false });
}
