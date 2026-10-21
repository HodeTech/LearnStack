// JUnit can call an empty describe() a passing testcase. Require Node's real
// same-invocation test counters as well; the workflow separately checks JUnit.
export default async function* requireTestCases(source) {
  let summaries = 0;
  for await (const event of source) {
    if (event.type !== 'test:summary' || event.data.file !== undefined) continue;
    summaries++;
    const { counts, success } = event.data;
    const fields = ['tests', 'passed', 'failed', 'cancelled', 'skipped', 'todo'];
    if (summaries !== 1 || success !== true
      || fields.some(field => !Number.isSafeInteger(counts?.[field]) || counts[field] < 0)
      || counts.tests === 0 || counts.passed !== counts.tests
      || ['failed', 'cancelled', 'skipped', 'todo'].some(field => counts[field] !== 0)) {
      throw new Error('Node controls require executed passing tests and zero cancellations/skips/todos.');
    }
    yield `${counts.tests} executed tests passed; zero cancellations/skips/todos.\n`;
  }
  if (summaries !== 1) throw new Error('Node controls require one complete test summary.');
}
