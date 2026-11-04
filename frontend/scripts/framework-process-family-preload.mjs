// Private control for the existing mutable Node command; no additional process.
import { isMainThread } from 'node:worker_threads';

const nonce = process.env.LEARNSTACK_FRAMEWORK_FAMILY_NONCE;
const parent = process.env.LEARNSTACK_FRAMEWORK_FAMILY_PARENT;
if (isMainThread && (nonce !== undefined || parent !== undefined)) {
  if (
    !/^[0-9a-f]{32}$/.test(nonce ?? '') ||
    parent !== String(process.ppid) ||
    typeof process.send !== 'function' ||
    !process.connected
  )
    throw new Error('Framework private process control refused');
  // Ordinary descendants retain the process group, not this leader protocol.
  delete process.env.LEARNSTACK_FRAMEWORK_FAMILY_NONCE;
  delete process.env.LEARNSTACK_FRAMEWORK_FAMILY_PARENT;
  let requested = false;
  const stop = (signal) => {
    if (requested) return;
    requested = true;
    // This process is still a member of its actual current group at the kernel
    // operation. No saved numeric identity is used to signal another generation.
    process.kill(0, signal);
  };
  process.on('message', (message) => {
    if (
      message?.kind === 'learnstack-framework-family-stop' &&
      message.nonce === nonce &&
      ['SIGTERM', 'SIGINT'].includes(message.signal) &&
      Object.keys(message).length === 3
    )
      stop(message.signal);
  });
  process.once('disconnect', () => stop('SIGTERM'));
  process.send({
    kind: 'learnstack-framework-family-ready',
    nonce,
    pid: process.pid,
    parent: process.ppid,
  });
  // Listening for a private shutdown request must not keep successful commands
  // alive after their own work/handles finish.
  process.channel.unref();
}
