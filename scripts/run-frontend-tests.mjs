import { spawn } from "node:child_process";
import {
  mkdtemp,
  readFile,
  readdir,
  realpath,
  rm,
  stat,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const ignored = new Set([
  "node_modules",
  ".next",
  ".server",
  "dist",
  "coverage",
  ".git",
]);
const testFile = /\.(?:test|spec)\.(?:[cm]?[jt]s|[jt]sx)$/;

async function testsBelow(directory) {
  const found = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (ignored.has(entry.name)) continue;
    const path = join(directory, entry.name);
    if (entry.isSymbolicLink())
      throw new Error(`Symlinked test source is unsupported: ${path}`);
    if (entry.isDirectory()) found.push(...(await testsBelow(path)));
    else if (entry.isFile() && testFile.test(entry.name)) found.push(path);
  }
  return found.sort();
}

/** Discover from the workspace declaration, including packages pnpm would silently skip. */
export async function discoverTestPackages(workspaceRoot) {
  const root = await realpath(workspaceRoot);
  const declaration = await readFile(join(root, "pnpm-workspace.yaml"), "utf8");
  const lines = declaration
    .split(/\r?\n/)
    .map((line) => line.replace(/\s*#.*$/, "").trim())
    .filter(Boolean);
  if (lines.shift() !== "packages:" || lines.length === 0)
    throw new Error("Frontend workspace declaration is empty or unsupported");
  const groups = lines.map((line) => {
    const match = /^-\s+['"]?([a-z][a-z-]*)\/\*['"]?$/.exec(line);
    if (!match)
      throw new Error(
        "Update frontend test discovery for the workspace declaration",
      );
    return match[1];
  });
  if (new Set(groups).size !== groups.length)
    throw new Error("Frontend workspace declaration repeats a package group");
  const packages = [];
  let manifests = 0;
  for (const group of groups) {
    for (const entry of await readdir(join(root, group), {
      withFileTypes: true,
    })) {
      if (entry.isSymbolicLink())
        throw new Error(`Symlinked workspace package is unsupported: ${group}/${entry.name}`);
      if (!entry.isDirectory()) continue;
      const path = join(root, group, entry.name);
      const manifest = JSON.parse(
        await readFile(join(path, "package.json"), "utf8"),
      );
      manifests += 1;
      const files = await testsBelow(path);
      const script = manifest.scripts?.test;
      if (files.length && (typeof script !== "string" || !script.trim()))
        throw new Error(
          `Discovered tests without an execution script: ${group}/${entry.name}`,
        );
      if (
        script !== undefined &&
        (typeof script !== "string" || !script.trim())
      )
        throw new Error(`Invalid test script: ${group}/${entry.name}`);
      if (script)
        packages.push({ path, name: `${group}/${entry.name}`, files });
    }
  }
  if (!manifests || !packages.length)
    throw new Error("Frontend test discovery found no tested packages");
  return packages.sort((left, right) => left.name.localeCompare(right.name));
}

/** Read actual Vitest outcomes; exit code zero alone permits skipped and todo cases. */
export function validateTestReport(report) {
  if (!report || typeof report !== "object" || report.success !== true)
    throw new Error("Frontend test report did not succeed");
  const positive = [
    "numTotalTests",
    "numPassedTests",
    "numTotalTestSuites",
    "numPassedTestSuites",
  ];
  const zero = [
    "numFailedTests",
    "numPendingTests",
    "numTodoTests",
    "numFailedTestSuites",
    "numPendingTestSuites",
  ];
  if (
    positive.some(
      (key) => !Number.isSafeInteger(report[key]) || report[key] <= 0,
    ) ||
    zero.some((key) => report[key] !== 0) ||
    report.numTotalTests !== report.numPassedTests ||
    report.numTotalTestSuites !== report.numPassedTestSuites
  )
    throw new Error(
      "Frontend test report is empty, incomplete, failed, skipped or todo",
    );
  if (!Array.isArray(report.testResults) || !report.testResults.length)
    throw new Error("Frontend test report has no executed files");
  let executed = 0;
  for (const file of report.testResults) {
    if (
      file.status !== "passed" ||
      !Array.isArray(file.assertionResults) ||
      !file.assertionResults.length
    )
      throw new Error("Frontend test report has an empty or unsuccessful file");
    for (const assertion of file.assertionResults) {
      if (assertion.status !== "passed")
        throw new Error("Frontend test report contains an unexecuted case");
      executed += 1;
    }
  }
  if (executed !== report.numTotalTests)
    throw new Error(
      "Frontend test report counters disagree with executed cases",
    );
  return executed;
}

function executeTests(path, reportPath, output) {
  return new Promise((accept, reject) => {
    // The group belongs to this runner; timeout cleanup never targets a user's process.
    const child = spawn(
      "pnpm",
      [
        "--dir",
        path,
        "run",
        "test",
        "--reporter=default",
        "--reporter=json",
        `--outputFile=${reportPath}`,
      ],
      {
        detached: process.platform !== "win32",
        stdio: ["ignore", "pipe", "pipe"],
      },
    );
    child.stdout.on("data", output);
    child.stderr.on("data", output);
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      try {
        process.kill(
          process.platform === "win32" ? child.pid : -child.pid,
          "SIGKILL",
        );
      } catch (error) {
        if (error.code !== "ESRCH") reject(error);
      }
    }, 180_000);
    child.once("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.once("close", (code) => {
      clearTimeout(timer);
      if (timedOut)
        reject(new Error("Frontend test package exceeded its deadline"));
      else if (code !== 0)
        reject(new Error("Frontend test package exited unsuccessfully"));
      else accept();
    });
  });
}

export async function runTestWorkspaces(
  workspaceRoot,
  output = (chunk) => process.stdout.write(chunk),
) {
  const packages = await discoverTestPackages(resolve(workspaceRoot));
  const directory = await mkdtemp(
    join(tmpdir(), "learnstack-frontend-results-"),
  );
  let total = 0;
  try {
    for (const [index, pkg] of packages.entries()) {
      const reportPath = join(directory, `${index}.json`);
      await executeTests(pkg.path, reportPath, output);
      // Bound reads before parsing; missing/unreadable reports remain a hard failure.
      if ((await stat(reportPath)).size > 64 * 1024 * 1024)
        throw new Error("Frontend test report exceeds its read bound");
      const report = JSON.parse(await readFile(reportPath, "utf8"));
      total += validateTestReport(report);
      const executedFiles = new Set(
        report.testResults.map((file) => resolve(file.name)),
      );
      if (
        executedFiles.size !== pkg.files.length ||
        pkg.files.some((file) => !executedFiles.has(resolve(file)))
      )
        throw new Error(
          `Frontend report omits a discovered test file: ${pkg.name}`,
        );
    }
    return { packages: packages.length, tests: total };
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(resolve(process.argv[1])).href
) {
  try {
    if (process.argv.length !== 2)
      throw new Error("Frontend test runner takes no arguments");
    const summary = await runTestWorkspaces(
      join(dirname(fileURLToPath(import.meta.url)), "../frontend"),
    );
    process.stdout.write(
      `Frontend guard: ${summary.tests} passed in ${summary.packages} packages; zero skips/todos.\n`,
    );
  } catch (error) {
    // Failures identify the guard, without dumping child environment/configuration.
    process.stderr.write(
      `Frontend guard refused the run: ${error instanceof Error ? error.message : "invalid result"}\n`,
    );
    process.exitCode = 1;
  }
}
