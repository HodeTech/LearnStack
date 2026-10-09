// @vitest-environment node
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { LegacyESLint } from 'eslint/use-at-your-own-risk';
import { describe, expect, it } from 'vitest';

// Standards 16 and accepted P02d-6 G43 require the applicable recommended
// jsx-a11y rules to block the real web app's JSX. This literal census means a
// plugin upgrade or config edit cannot silently change what the guard covers.
const cases = [
  ['alt-text', '<img src="/photo.png" />', '<img src="/photo.png" alt="Mountains" />'],
  ['anchor-has-content', '<a href="/course" />', '<a href="/course">Course</a>'],
  ['anchor-is-valid', '<a href="#">Course</a>', '<a href="/course">Course</a>'],
  [
    'aria-activedescendant-has-tabindex',
    '<div role="listbox" aria-activedescendant="option" />',
    '<div role="listbox" aria-activedescendant="option" tabIndex={0} />',
  ],
  ['aria-props', '<div aria-typo="x" />', '<div aria-label="Item" />'],
  ['aria-proptypes', '<div aria-hidden="maybe" />', '<div aria-hidden="true" />'],
  ['aria-role', '<div role="unknown" />', '<div role="main" />'],
  [
    'aria-unsupported-elements',
    '<meta aria-label="Item" />',
    '<meta name="theme-color" content="#fff" />',
  ],
  ['autocomplete-valid', '<input autoComplete="fake" />', '<input autoComplete="email" />'],
  [
    'click-events-have-key-events',
    '<div onClick={() => {}}>Open</div>',
    '<button type="button" onClick={() => {}}>Open</button>',
  ],
  ['heading-has-content', '<h2 />', '<h2>Courses</h2>'],
  ['html-has-lang', '<html><body /></html>', '<html lang="en"><body /></html>'],
  ['iframe-has-title', '<iframe src="/media" />', '<iframe src="/media" title="Lesson video" />'],
  [
    'img-redundant-alt',
    '<img src="/photo.png" alt="photo of mountains" />',
    '<img src="/photo.png" alt="Mountains" />',
  ],
  [
    'interactive-supports-focus',
    '<div role="button" onClick={() => {}}>Open</div>',
    '<button type="button" onClick={() => {}}>Open</button>',
  ],
  [
    'label-has-associated-control',
    '<label>Name</label>',
    '<><label htmlFor="name">Name</label><input id="name" /></>',
  ],
  [
    'media-has-caption',
    '<video src="/clip.mp4" />',
    '<video src="/clip.mp4"><track kind="captions" src="/captions.vtt" /></video>',
  ],
  [
    'mouse-events-have-key-events',
    '<div onMouseOver={() => {}}>Details</div>',
    '<div onMouseOver={() => {}} onFocus={() => {}}>Details</div>',
  ],
  ['no-access-key', '<button accessKey="x">Open</button>', '<button type="button">Open</button>'],
  ['no-autofocus', '<input autoFocus />', '<input aria-label="Search" />'],
  ['no-distracting-elements', '<marquee>News</marquee>', '<p>News</p>'],
  [
    'no-interactive-element-to-noninteractive-role',
    '<button role="article">Open</button>',
    '<button type="button">Open</button>',
  ],
  [
    'no-noninteractive-element-interactions',
    '<article onClick={() => {}}>Open</article>',
    '<article>Open</article>',
  ],
  ['no-noninteractive-element-to-interactive-role', '<h2 role="button">Open</h2>', '<h2>Open</h2>'],
  ['no-noninteractive-tabindex', '<article tabIndex={0}>Open</article>', '<article>Open</article>'],
  [
    'no-redundant-roles',
    '<button role="button">Open</button>',
    '<button type="button">Open</button>',
  ],
  [
    'no-static-element-interactions',
    '<div onClick={() => {}}>Open</div>',
    '<button type="button" onClick={() => {}}>Open</button>',
  ],
  [
    'role-has-required-aria-props',
    '<div role="checkbox" tabIndex={0}>Accept</div>',
    '<div role="checkbox" aria-checked="false" tabIndex={0}>Accept</div>',
  ],
  [
    'role-supports-aria-props',
    '<button aria-checked="true">Open</button>',
    '<button type="button" aria-label="Open">Open</button>',
  ],
  ['scope', '<div scope="col" />', '<th scope="col">Course</th>'],
  ['tabindex-no-positive', '<div tabIndex={1} />', '<button type="button">Open</button>'],
] as const;

describe('actual web JSX accessibility lint config', () => {
  const app = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  const filePath = join(app, 'src', 'accessibility-probe.tsx');
  const eslint = new LegacyESLint({
    cwd: app,
    resolvePluginsRelativeTo: resolve(app, '../../packages/config/eslint'),
    // Planted source strings have no tsconfig file; these rules inspect syntax.
    overrideConfig: { parserOptions: { project: null } },
  });

  const lint = async (jsx: string) => {
    const results = await eslint.lintText(`export const Probe = () => (${jsx});\n`, { filePath });
    expect(results).toHaveLength(1); // An ignored probe checks nothing.
    const messages = results[0]!.messages;
    expect(messages.filter((message) => !message.ruleId || message.fatal)).toEqual([]);
    return messages.filter((message) => message.ruleId?.startsWith('jsx-a11y/'));
  };

  it('keeps the complete active recommended rule census blocking', async () => {
    const config = await eslint.calculateConfigForFile(filePath);
    const enabled = Object.entries(config.rules)
      .filter(
        ([name, setting]) =>
          name.startsWith('jsx-a11y/') &&
          (Array.isArray(setting) ? setting[0] : setting) !== 'off' &&
          (Array.isArray(setting) ? setting[0] : setting) !== 0,
      )
      .map(([name]) => name);
    const expected = cases.map(([name]) => `jsx-a11y/${name}`);

    expect(expected.length).toBeGreaterThan(0);
    expect(new Set(expected).size).toBe(expected.length);
    expect(enabled.sort()).toEqual([...expected].sort());
    for (const name of enabled) {
      const setting = config.rules[name];
      expect(Array.isArray(setting) ? setting[0] : setting, name).toBe('error');
    }
  });

  it.each(cases)('blocks jsx-a11y/%s and accepts its clean control', async (name, dirty, clean) => {
    const violation = await lint(dirty);
    expect(violation, name).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ ruleId: `jsx-a11y/${name}`, severity: 2 }),
      ]),
    );
    expect(await lint(clean), `${name} clean control`).toEqual([]);
  });
});
