import { isMainThread, parentPort, workerData } from 'node:worker_threads';

const goodPid = (value) => Number.isSafeInteger(value) && value > 1;
const validData =
  workerData !== null && typeof workerData === 'object' && !Array.isArray(workerData);
const data = Object.freeze(validData ? workerData : {});
const { parentPid, groupPid } = data;
if (
  isMainThread ||
  process.platform === 'win32' ||
  !parentPort ||
  Object.keys(data).sort().join(',') !== 'groupPid,parentPid' ||
  !goodPid(parentPid) ||
  !goodPid(groupPid) ||
  groupPid !== process.pid
)
  throw new Error('Invalid BFF parent watcher');
// The helper starts this process as a detached group leader. This independent
// thread can notice parent loss while the leader's event loop is blocked. The
// interval is not a hard deadline; a dead leader, detached descendants and
// Windows remain outside this parent-loss control.
const check = () => {
  if (process.ppid !== parentPid) process.kill(-process.pid, 'SIGKILL');
};
check();
setInterval(check, 50);
parentPort.postMessage('ready');
