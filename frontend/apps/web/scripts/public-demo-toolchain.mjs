import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';

/** Match installed metadata to exact workspace pins before admitting audit tools. */
export function toolchainVersions(pins, installed) {
  for (const name of ['lighthouse', 'playwright']) {
    assert.match(pins[name], /^\d+\.\d+\.\d+$/, 'Exact workspace tool pin');
    assert.equal(installed[name], pins[name], 'Installed ' + name + ' matches workspace pin');
  }
  assert.equal(installed.core, installed.playwright, 'Playwright core matches its library');
  const chromium = installed.browsers.filter((browser) => browser.name === 'chromium');
  assert.equal(chromium.length, 1, 'One managed full Chromium declaration');
  assert.match(chromium[0].revision, /^\d+$/, 'Managed Chromium revision');
  assert.match(chromium[0].browserVersion, /^\d+\.\d+\.\d+\.\d+$/, 'Managed Chromium version');
  return Object.freeze({
    lighthouse: installed.lighthouse,
    playwright: installed.playwright,
    chromiumRevision: chromium[0].revision,
    browserVersion: chromium[0].browserVersion,
  });
}

export function readDemoToolchain() {
  const require = createRequire(import.meta.url);
  const playwrightManifest = require.resolve('playwright/package.json');
  const coreManifest = createRequire(playwrightManifest).resolve('playwright-core/package.json');
  const json = (path) => JSON.parse(readFileSync(path, 'utf8'));
  return toolchainVersions(
    json(new URL('../../../package.json', import.meta.url)).devDependencies,
    {
      lighthouse: require('lighthouse/package.json').version,
      playwright: json(playwrightManifest).version,
      core: json(coreManifest).version,
      browsers: json(join(dirname(coreManifest), 'browsers.json')).browsers,
    },
  );
}
