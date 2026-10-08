// Test fixtures own detached POSIX groups; the group can outlive its leader.
export async function stopTestChild(child) {
  if (child.pid === undefined) return; // A failed spawn owns no process.
  const group = process.platform !== 'win32';
  const signal = (name) => {
    try {
      if (group) process.kill(-child.pid, name);
      else if (child.exitCode === null && child.signalCode === null) child.kill(name);
    } catch (error) {
      // Neither permission refusal nor a sent signal proves absence. Poll below;
      // persistent refusal remains present and fails the bounded deadline.
      if (!['ESRCH', 'EPERM'].includes(error.code)) throw error;
    }
  };
  const alive = () => {
    if (!group) return child.exitCode === null && child.signalCode === null;
    try {
      process.kill(-child.pid, 0);
      return true;
    } catch (error) {
      if (error.code === 'ESRCH') return false;
      // Darwin can report EPERM while an owned process is exiting. It still
      // counts as present: only ESRCH closes the proof, or the deadline fails.
      if (error.code === 'EPERM') return true;
      throw error;
    }
  };
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
