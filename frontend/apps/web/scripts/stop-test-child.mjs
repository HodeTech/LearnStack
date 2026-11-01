// Test fixtures own detached POSIX groups; the group can outlive its leader.
// Retire an ownership handle permanently after absence. Repeated cleanup must
// never signal a saved numeric PID after the owned group has stopped.
const stopping = new WeakMap();
const productionStopping = new WeakMap();
const runtimeOwners = new WeakMap();
const HEX = /^[0-9a-f]{32}$/;

/** Register a detached fixture owner at its actual spawn, before readiness. */
export function markRuntimeTestChild(child) {
  if (runtimeOwners.has(child) || !Number.isSafeInteger(child.pid) || child.pid < 1)
    throw new Error('Runtime fixture owner capture refused');
  let family;
  let lease;
  const producers = new Map();
  let closed = false;
  const close = new Promise((resolve) =>
    child.once('close', () => {
      closed = true;
      resolve();
    }),
  );
  const state = {
    get family() {
      return family;
    },
    get lease() {
      return lease;
    },
    get producers() {
      return producers;
    },
    get closed() {
      return closed;
    },
    close,
    retired: new Set(),
  };
  runtimeOwners.set(child, state);
  return Object.freeze({
    captureLease(record) {
      if (
        lease ||
        !record ||
        Object.keys(record).sort().join(',') !== 'format,kind,nonce,pid' ||
        record.format !== 1 ||
        record.pid !== child.pid ||
        !['production', 'development'].includes(record.kind) ||
        !HEX.test(record.nonce)
      )
        throw new Error('Runtime fixture lease capture refused');
      lease = Object.freeze({ ...record });
      return lease;
    },
    captureProducerFamily(handle) {
      if (
        !lease ||
        !handle ||
        Object.keys(handle).sort().join(',') !== 'group,leaseNonce,parentPid,pid' ||
        !Number.isSafeInteger(handle.pid) ||
        handle.pid < 1 ||
        handle.group !== handle.pid ||
        handle.parentPid !== child.pid ||
        handle.leaseNonce !== lease.nonce ||
        producers.has(handle.pid)
      )
        throw new Error('Runtime fixture producer family capture refused');
      const copy = Object.freeze({ ...handle });
      producers.set(copy.pid, copy);
      return copy;
    },
    captureFamily(handle) {
      if (
        !lease ||
        family ||
        !handle ||
        Object.keys(handle).sort().join(',') !== 'group,incarnation,leaseNonce,parentPid,pid' ||
        !Number.isSafeInteger(handle.pid) ||
        handle.pid < 1 ||
        handle.group !== handle.pid ||
        handle.parentPid !== child.pid ||
        handle.leaseNonce !== lease.nonce ||
        !HEX.test(handle.incarnation)
      )
        throw new Error('Runtime fixture family capture refused');
      family = Object.freeze({ ...handle });
      return family;
    },
    lease() {
      return lease ?? null;
    },
    producers() {
      return Object.freeze([...producers.values()]);
    },
    family() {
      return family ?? null;
    },
    retired() {
      return (
        closed &&
        Boolean(family) &&
        [child.pid, ...producers.keys(), family.group].every((group) => state.retired.has(group))
      );
    },
  });
}

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
    pending = Promise.resolve().then(() =>
      runtimeOwners.has(child)
        ? stopOwnedRuntimeChild(child, runtimeOwners.get(child))
        : stopOwnedChild(child),
    );
    stopping.set(child, pending);
  }
  return pending;
}

/** Exact production public-server driver: its clean zero exit follows its own final family join. */
export function stopProductionRuntimeOwner(child) {
  let pending = productionStopping.get(child);
  if (pending) return pending;
  pending = new Promise((resolve, reject) => {
    if (
      !Number.isSafeInteger(child.pid) ||
      child.pid < 1 ||
      child.exitCode !== null ||
      child.signalCode !== null ||
      child.killed
    ) {
      reject(new Error('Production runtime owner was already lost or stopping'));
      return;
    }
    const done = (code, signal) => {
      child.off('error', failed);
      if (code === 0 && signal === null) resolve(Object.freeze({ forced: false }));
      else reject(new Error('Production runtime owner retirement unconfirmed'));
    };
    const failed = () => {
      child.off('close', done);
      reject(new Error('Production runtime owner process failed'));
    };
    child.once('close', done);
    child.once('error', failed);
    try {
      if (!child.kill('SIGTERM')) failed();
    } catch {
      failed();
    }
  });
  productionStopping.set(child, pending);
  return pending;
}

async function stopOwnedRuntimeChild(child, state) {
  // A runtime's owner group and child group are separate actual families.
  // No saved runtime PGID receives a nonzero signal, including after leader loss.
  if (!state.closed && child.exitCode === null && child.signalCode === null) {
    try {
      child.kill('SIGTERM');
    } catch (error) {
      if (error.code !== 'ESRCH') throw error;
    }
  }
  await state.close;
  const groups = [
    child.pid,
    ...state.producers.keys(),
    ...(state.family ? [state.family.group] : []),
  ];
  const retired = state.retired;
  let delay = 10;
  while (retired.size !== groups.length) {
    for (const group of groups) {
      if (retired.has(group)) continue;
      try {
        process.kill(-group, 0);
      } catch (error) {
        if (error.code === 'ESRCH') retired.add(group);
        else if (error.code !== 'EPERM') throw error;
      }
    }
    if (retired.size === groups.length) break;
    await new Promise((resolve) => setTimeout(resolve, delay));
    delay = Math.min(delay * 2, 250);
  }
  if (!state.family) throw new Error('Runtime fixture family handle missing');
  return Object.freeze({
    forced: Boolean(child.signalCode && !['SIGTERM', 'SIGINT'].includes(child.signalCode)),
    ownerCode: child.exitCode,
    ownerSignal: child.signalCode,
  });
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
