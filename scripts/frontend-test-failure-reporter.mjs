import { createHash } from "node:crypto";
import { writeSync } from "node:fs";

const MAX_RECORDS = 128;
const MAX_ERRORS = 64;
const knownCases = new Map([
  [
    "closes actual matcher, Next routing hooks, framework entry points and Server Actions",
    "public-http-surface",
  ],
  [
    "confines browser fetch to the exact same-origin no-store BFF extension action",
    "public-browser-fetch",
  ],
  ...["path", "credentials", "redirect", "cache", "headers"].map((field) => [
    `confines browser fetch to the BFF extension action when ${field} changes`,
    "public-browser-fetch",
  ]),
  [
    "rejects an extra browser fetch outside the BFF extension action",
    "public-browser-fetch",
  ],
  [
    "rejects the BFF extension fetch copied into another production module",
    "public-browser-fetch",
  ],
]);

function timeoutMessage(kind, timeout) {
  const setting = kind === "Test" ? "testTimeout" : "hookTimeout";
  return `${kind} timed out in ${timeout}ms.\nIf this is a long-running ${kind.toLowerCase()}, pass a timeout value as the last argument or configure it globally with "${setting}".`;
}

function category(error) {
  const message = typeof error?.message === "string" ? error.message : "";
  if (message.length <= 256) {
    const match = /^(Test|Hook) timed out in ([1-9][0-9]{0,8})ms\./.exec(
      message,
    );
    if (match && message === timeoutMessage(match[1], match[2]))
      return match[1] === "Test" ? "testTimeout" : "hookTimeout";
  }
  return error?.name === "AssertionError" ? "assertion" : "other";
}

function moduleName(testCase) {
  const name = testCase.module?.relativeModuleId;
  return typeof name === "string" &&
    name.length <= 256 &&
    /^(?:[A-Za-z0-9_-]+\/)*[A-Za-z0-9_-]+\.(?:test|spec)\.[cm]?[jt]sx?$/.test(
      name,
    )
    ? name
    : "unavailable";
}

function emit(record) {
  // For failures already delivered by Vitest: a bounded synchronous write
  // completes before a later owned-group kill. Undelivered worker events remain
  // best-effort diagnostics, never authoritative completion evidence.
  // Messages, stacks, expected/actual values and dynamic test names stay private.
  writeSync(2, `\nFrontend diagnostic: ${JSON.stringify(record)}\n`);
}

export default class FrontendTestFailureReporter {
  records = 0;

  onTestCaseResult(testCase) {
    const result = testCase.result();
    if (result.state !== "failed") return;
    if (this.records >= MAX_RECORDS) {
      if (this.records === MAX_RECORDS)
        emit({ schema: 1, kind: "frontend-test-failures-truncated" });
      this.records = MAX_RECORDS + 1;
      return;
    }
    this.records += 1;
    const errors = Array.isArray(result.errors) ? result.errors : [];
    const categories = {
      assertion: 0,
      testTimeout: 0,
      hookTimeout: 0,
      other: 0,
    };
    for (const error of errors.slice(0, MAX_ERRORS))
      categories[category(error)] += 1;
    const duration = testCase.diagnostic()?.duration;
    const line = testCase.location?.line;
    const id = typeof testCase.id === "string" ? testCase.id : "unavailable";
    emit({
      schema: 1,
      kind: "frontend-test-failure",
      file: moduleName(testCase),
      line: Number.isSafeInteger(line) && line > 0 ? line : null,
      case: knownCases.get(testCase.name) ?? "other",
      caseId: createHash("sha256")
        .update(id.slice(0, 4096))
        .digest("hex")
        .slice(0, 16),
      durationMs:
        Number.isFinite(duration) && duration >= 0 && duration <= 1_000_000_000
          ? Math.round(duration)
          : null,
      categories,
      errorsTruncated: errors.length > MAX_ERRORS,
    });
  }
}
