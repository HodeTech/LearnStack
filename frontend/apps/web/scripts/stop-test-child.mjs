// Test fixtures own detached POSIX groups; the group can outlive its leader.
// Retire an ownership handle permanently after absence. Repeated cleanup must
// never signal a saved numeric PID after the owned group has stopped.
const stopping = new WeakMap();

// This is a negative cleanup gate, not proof that a separate compiler family
// retired. A forced/fatal outer exit must not authorize private-root deletion.
export function assertUnforcedTestChildStop(child, stopped) {
  if (
    !stopped ||
    stopped.forced !== false ||
    (child.signalCode && !['SIGTERM', 'SIGINT'].includes(child.signalCode))
  )
    throw new Error('Owned fixture process stop was forced or unconfirmed');
}

export function stopTestChild(child) {
  let pending = stopping.get(child);
  if (!pending) {
    pending = Promise.resolve().then(() => stopOwnedChild(child));
    stopping.set(child, pending);
  }
  return pending;
}

async function stopOwnedChild(child) {
  if (child.pid === undefined) return Object.freeze({ forced: false }); // Failed spawn owns no process.
  const group = process.platform !== 'win32';
  let retired = false;
  const leaderExited = () => child.exitCode !== null || child.signalCode !== null;
  const signal = (name) => {
    // An observed leader loss is not a generation-safe authority to signal its
    // saved group number. Probe/retain/drain only; no recovery is inferred here.
    if (leaderExited()) return false;
    try {
      if (group) process.kill(-child.pid, name);
      else if (child.exitCode === null && child.signalCode === null) child.kill(name);
      return true;
    } catch (error) {
      // Neither permission refusal nor a sent signal proves absence. Poll below;
      // persistent refusal remains present and fails the bounded deadline.
      if (error.code === 'ESRCH') {
        retired = true;
        return false;
      } else if (error.code !== 'EPERM') throw error;
      return true;
    }
  };
  const alive = () => {
    if (retired) return false;
    if (!group) return child.exitCode === null && child.signalCode === null;
    try {
      process.kill(-child.pid, 0);
      return true;
    } catch (error) {
      if (error.code === 'ESRCH') {
        retired = true;
        return false;
      }
      // Darwin can report EPERM while an owned process is exiting. It still
      // counts as present: only ESRCH closes the proof, or the deadline fails.
      if (error.code === 'EPERM') return true;
      throw error;
    }
  };
  if (!alive()) return Object.freeze({ forced: false });
  signal('SIGTERM');
  const started = Date.now();
  let forced = false;
  while (alive()) {
    const elapsed = Date.now() - started;
    if (elapsed >= 5000) throw new Error('Owned fixture process group did not stop');
    if (!forced && elapsed >= 2000) {
      forced = signal('SIGKILL');
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  return Object.freeze({ forced });
}
