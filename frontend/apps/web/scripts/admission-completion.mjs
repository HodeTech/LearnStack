// Test-only native-event barrier; observing client EOF is not server completion.
export function createNativeCompletionObserver() {
  const pending = new Set();
  const tracked = new WeakSet();
  return Object.freeze({
    track(response) {
      if (tracked.has(response)) throw new Error('Native response already observed');
      tracked.add(response);
      let resolve;
      const completion = new Promise((done) => {
        resolve = done;
      });
      pending.add(completion);
      let completed = false;
      const complete = () => {
        if (completed) return;
        completed = true;
        response.off('finish', complete);
        response.off('close', complete);
        pending.delete(completion);
        resolve();
      };
      response.once('finish', complete);
      response.once('close', complete);
    },
    settled: () => Promise.all([...pending]).then(() => undefined),
  });
}
