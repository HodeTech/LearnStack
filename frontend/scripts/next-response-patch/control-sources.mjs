// Test-only reconstruction. Never import restored stock code from a supported
// launcher. Each reconstructed byte sequence must match its original SHA-256.
import { readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { join, resolve } from 'node:path';
import { verifyFrameworkInstallation } from '../framework-installation.mjs';

const sha = (text) => createHash('sha256').update(text).digest('hex');

export async function loadQualifiedResponseSources({ frontendRoot }) {
  frontendRoot = resolve(frontendRoot);
  const appRoot = join(frontendRoot, 'apps/web');
  const { nextRoot } = await verifyFrameworkInstallation({ appRoot, frontendRoot });
  const census = JSON.parse(
    await readFile(join(frontendRoot, 'framework/next-response-cleanup/census.json'), 'utf8'),
  );
  const results = [];
  for (const restoration of census.javascriptRestoration) {
    const fact = census.files.find((f) => f.path === restoration.path && f.kind !== 'map');
    if (!fact) throw new Error('Missing response source identity');
    const patched = await readFile(join(nextRoot, fact.path), 'utf8');
    if (sha(patched) !== fact.sha256) throw new Error('Changed response source');
    let original = '',
      cursor = 0;
    for (const edit of restoration.edits) {
      if (
        !Number.isInteger(edit.start) ||
        !Number.isInteger(edit.end) ||
        edit.start < cursor ||
        edit.end < edit.start ||
        edit.end > patched.length ||
        typeof edit.text !== 'string'
      )
        throw new Error('Invalid original edit identity');
      original += patched.slice(cursor, edit.start) + edit.text;
      cursor = edit.end;
    }
    original += patched.slice(cursor);
    if (
      sha(original) !== fact.originalSha256 ||
      fact.originalSha256 !== restoration.originalSha256
    ) {
      throw new Error('Original response source reconstruction mismatch');
    }
    results.push(Object.freeze({ path: fact.path, kind: fact.kind, original, patched }));
  }
  if (results.length !== 30) throw new Error('Unexpected response source count');
  return Object.freeze(results);
}
