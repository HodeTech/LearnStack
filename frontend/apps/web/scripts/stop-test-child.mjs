// Test fixtures own detached POSIX groups; the group can outlive its leader.
// Retire an ownership handle permanently after absence. Repeated cleanup must
// never signal a saved numeric PID after the owned group has stopped.
const stopping = new WeakMap();
export function stopTestChild(child) {
  let pending = stopping.get(child);
  if (!pending) {
    pending = Promise.resolve().then(() => stopOwnedChild(child));
    stopping.set(child, pending);
  }
  return pending;
}

async function stopOwnedChild(child) {
  if (child.pid === undefined) return; // A failed spawn owns no process.
  const group = process.platform !== 'win32';
  let retired = false;
  const signal = (name) => {
    try {
      if (group) process.kill(-child.pid, name);
      else if (child.exitCode === null && child.signalCode === null) child.kill(name);
    } catch (error) {
      // Neither permission refusal nor a sent signal proves absence. Poll below;
      // persistent refusal remains present and fails the bounded deadline.
      if (error.code === 'ESRCH') retired = true;
      else if (error.code !== 'EPERM') throw error;
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
  if (!alive()) return;
  signal('SIGTERM');
  const started = Date.now();
  let forced = false;
  while (alive()) {
    const elapsed = Date.now() - started;
    if (elapsed >= 5000) throw new Error('Owned fixture process group did not stop');
    if (!forced && elapsed >= 2000) {
      signal('SIGKILL');
      forced = true;
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
}
