import { readdirSync, readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import type { transform as nextTransform } from 'next/dist/build/swc';
import type { getLoaderSWCOptions as nextLoaderOptions } from 'next/dist/build/swc/options';
import type { WEBPACK_LAYERS as nextLayers } from 'next/dist/lib/constants';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';

import {
  ADAPTER,
  buildSourceGraph,
  cacheFindings,
  clientBoundaryFindings,
  hasDirective,
  hasServerOnlyMarker,
  I18N_REQUEST,
  INGRESS,
  MIDDLEWARE,
  navigationFindings,
  privateServerFiles,
  publicLayoutPolicyFindings,
  PUBLIC_LAYOUT,
  rawAuthorityFindings,
  reachable,
  REQUEST_MEMO,
  resolutionCensusFindings,
  SDK_SERVER,
  transportFindings,
  unsupportedSourceFindings,
} from './public-boundary-analysis';
import type {
  Finding,
  ResolutionConfig,
  SourceCensus,
  WorkspaceEntry,
} from './public-boundary-analysis';

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const require = createRequire(import.meta.url);
const { transform } = require('next/dist/build/swc') as { transform: typeof nextTransform };
const { getLoaderSWCOptions } = require('next/dist/build/swc/options') as {
  getLoaderSWCOptions: typeof nextLoaderOptions;
};
const { WEBPACK_LAYERS } = require('next/dist/lib/constants') as {
  WEBPACK_LAYERS: typeof nextLayers;
};
const extensionFindings: Finding[] = [];
function productionSources(): SourceCensus {
  const sources: Record<string, string> = {};
  const visit = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      if (entry.isDirectory()) {
        // Runtime/test fixtures never enter the production graph.
        if (!['test', '__tests__', '__fixtures__', 'fixtures'].includes(entry.name)) visit(path);
      } else if (
        /\.(?:tsx?|json)$/.test(entry.name) &&
        !/\.(?:test|spec|d)\.(?:tsx?|json)$/.test(entry.name)
      ) {
        sources[relative(frontend, path).split('\\').join('/')] = readFileSync(path, 'utf8');
      } else
        extensionFindings.push(
          ...unsupportedSourceFindings([relative(frontend, path).split('\\').join('/')]),
        );
    }
  };
  visit(join(frontend, 'apps/web/src'));
  for (const entry of readdirSync(join(frontend, 'packages'), { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === 'config') continue;
    visit(join(frontend, 'packages', entry.name, 'src'));
  }
  return sources;
}
const sources = productionSources();
const production = buildSourceGraph(sources);
const publicRoots = [...production.files.keys()].filter((name) => name.includes('/app/(public)/'));
// Native ingress currently admits these exact scaffold paths alongside product
// routes. They share the root layout and must remain in the rendering census.
const scaffoldRoots = [...production.files.keys()].filter((name) =>
  /\/app\/\((?:studio|portal)\)\//.test(name),
);
const ROOT_LAYOUT = 'apps/web/src/app/layout.tsx';
// next-intl loads this configuration through its plugin, outside static imports.
const renderRoots = [...publicRoots, ...scaffoldRoots, ROOT_LAYOUT, MIDDLEWARE, I18N_REQUEST];
const catalogues = ['en', 'tr'].map((locale) => `apps/web/src/i18n/messages/${locale}/public.json`);
const publicGraph = reachable(production, renderRoots);
const clean = (findings: readonly Finding[], fix: string) =>
  expect(findings, fix + '\n' + JSON.stringify(findings, null, 2)).toEqual([]);
const graphWith = (file: string, source: string) =>
  buildSourceGraph({ ...sources, [file]: source });
const probe = 'apps/web/src/app/(public)/probe.ts';

function resolutionCensus() {
  const configs: Record<string, ResolutionConfig> = {};
  const packages: Record<string, WorkspaceEntry> = {};
  const directories = [
    'apps/web',
    ...readdirSync(join(frontend, 'packages'), { withFileTypes: true })
      .filter((entry) => entry.isDirectory() && entry.name !== 'config')
      .map((entry) => 'packages/' + entry.name),
  ];
  for (const directory of directories) {
    for (const name of readdirSync(join(frontend, directory)).filter((name) =>
      /^tsconfig(?:\.[^.]+)?\.json$/.test(name),
    )) {
      const file = directory + '/' + name;
      const config = ts.getParsedCommandLineOfConfigFile(
        join(frontend, file),
        {},
        {
          ...ts.sys,
          onUnRecoverableConfigFileDiagnostic: (diagnostic) => {
            throw new Error(ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n'));
          },
        },
      );
      expect(config, file).toBeDefined();
      expect(config!.errors, file).toEqual([]);
      const options = config!.options as ts.CompilerOptions & { pathsBasePath?: string };
      configs[file] = {
        paths: options.paths,
        baseUrl:
          options.baseUrl === undefined
            ? undefined
            : relative(frontend, options.baseUrl).split('\\').join('/'),
        pathsBasePath:
          options.pathsBasePath === undefined
            ? undefined
            : relative(frontend, options.pathsBasePath).split('\\').join('/'),
      };
    }
    if (directory.startsWith('packages/'))
      packages[directory] = JSON.parse(
        readFileSync(join(frontend, directory, 'package.json'), 'utf8'),
      ) as WorkspaceEntry;
  }
  return { configs, packages };
}

/** ADR-0053 § Architecture Tests; Standards 07 § SDK / Public Site Renderer. */
describe('ADR-0053 production public boundaries', () => {
  it('the production census pins the actual public/private/SDK module graph', () => {
    expect([...production.files.keys()]).toEqual(
      expect.arrayContaining([
        ADAPTER,
        INGRESS,
        I18N_REQUEST,
        MIDDLEWARE,
        PUBLIC_LAYOUT,
        ROOT_LAYOUT,
        SDK_SERVER,
        'packages/sdk/src/index.ts',
        'packages/sdk/src/response-policy.ts',
        'packages/ui/src/index.ts',
        'apps/web/src/server/public-entry.ts',
        'apps/web/src/server/public-request.ts',
        ...catalogues,
      ]),
    );
    expect(publicRoots).toContain(PUBLIC_LAYOUT);
    expect(renderRoots).toContain(I18N_REQUEST);
    expect(scaffoldRoots).toEqual(
      expect.arrayContaining([
        'apps/web/src/app/(studio)/layout.tsx',
        'apps/web/src/app/(studio)/studio/page.tsx',
        'apps/web/src/app/(portal)/layout.tsx',
        'apps/web/src/app/(portal)/portal/page.tsx',
      ]),
    );
    expect(reachable(production, [I18N_REQUEST])).toEqual(
      expect.arrayContaining([
        I18N_REQUEST,
        'apps/web/src/server/public-request.ts',
        ADAPTER,
        ...catalogues,
      ]),
    );
    expect(privateServerFiles(production)).toEqual(
      expect.arrayContaining([
        ADAPTER,
        INGRESS,
        SDK_SERVER,
        I18N_REQUEST,
        'apps/web/src/server/public-request.ts',
      ]),
    );
    expect(reachable(production, [MIDDLEWARE])).toEqual(
      expect.arrayContaining([
        MIDDLEWARE,
        ADAPTER,
        INGRESS,
        'apps/web/src/server/public-entry.ts',
        SDK_SERVER,
        'packages/sdk/src/response-policy.ts',
      ]),
    );
    clean(production.unresolved, 'Fix: keep every runtime local import in the production census.');
    clean(extensionFindings, 'Fix: model every production runtime source extension.');
  });
  it('the source resolver covers inherited tsconfig aliases and workspace exports', () => {
    const { configs, packages } = resolutionCensus();
    expect(Object.keys(configs)).toEqual(
      expect.arrayContaining([
        'apps/web/tsconfig.json',
        'apps/web/tsconfig.server.json',
        'packages/sdk/tsconfig.json',
        'packages/ui/tsconfig.json',
      ]),
    );
    expect(Object.keys(packages)).toEqual(expect.arrayContaining(['packages/sdk', 'packages/ui']));
    clean(
      resolutionCensusFindings(configs, packages),
      'Fix: keep configured module resolution inside the source resolver census.',
    );
    for (const [directory, entry] of Object.entries(packages))
      for (const [key, value] of Object.entries(entry.exports as Record<string, string>)) {
        const specifier = entry.name + (key === '.' ? '' : key.slice(1));
        expect(production.resolveModule(specifier, MIDDLEWARE)).toBe(
          directory + '/' + value.slice(2),
        );
      }
  });
  it('Public_Renderer_Uses_Trusted_Ingress_And_Server_Only_Transport', () => {
    const missingMarker: Finding[] = hasServerOnlyMarker(production.files.get(ADAPTER)!)
      ? []
      : [
          {
            file: ADAPTER,
            line: 1,
            reason: 'Fix: import server-only in the configured public adapter.',
          },
        ];
    clean(
      [
        ...transportFindings(production),
        ...clientBoundaryFindings(production),
        ...rawAuthorityFindings(production, publicGraph),
        ...missingMarker,
      ],
      'Fix: keep authority behind native provenance and the configured server-only adapter.',
    );
  });
  it('Public_Renderer_Does_Not_Share_Tenant_Representations', () => {
    clean(
      [...cacheFindings(production, publicGraph), ...publicLayoutPolicyFindings(production)],
      'Fix: retain the dynamic/no-store public layout and request-local public data.',
    );
  });
  it('global fetch and all three hop-header setters have exactly one production adapter', () => {
    clean(transportFindings(production), 'Fix: use the configured public adapter.');
    // Allowed code must contain actual transport/setters: renaming it removes the exception.
    const renamed = Object.fromEntries(
      Object.entries(sources).map(([name, source]) => [name === ADAPTER ? probe : name, source]),
    );
    expect(
      transportFindings(buildSourceGraph(renamed)).filter((item) => item.file === probe),
    ).toHaveLength(4);
  });
  it('the configured production caller imports the server-only marker', () => {
    expect(hasServerOnlyMarker(production.files.get(ADAPTER)!)).toBe(true);
  });
  it('production Client Components cannot reach private public-server modules', () => {
    // This production census may be empty at P5. Planted graph cases below prove the mechanism.
    clean(clientBoundaryFindings(production), 'Fix: pass public data through server props.');
  });
  it('the actual public render closure contains no shared cache', () => {
    expect(publicGraph).toEqual(
      expect.arrayContaining([PUBLIC_LAYOUT, MIDDLEWARE, I18N_REQUEST, ADAPTER, SDK_SERVER]),
    );
    clean(
      cacheFindings(production, publicGraph),
      'Fix: keep public rendering dynamic and no-store.',
    );
  });
  it('the public render closure uses document anchors without client router imports', () => {
    clean(navigationFindings(production, publicGraph), 'Fix: preserve G40 document navigation.');
  });
  it('public pages and their transitive helpers do not read raw authority', () => {
    const helpers = publicGraph.filter((name) => name !== INGRESS);
    expect(helpers).toEqual(
      expect.arrayContaining([
        PUBLIC_LAYOUT,
        MIDDLEWARE,
        I18N_REQUEST,
        'apps/web/src/server/public-entry.ts',
        'apps/web/src/server/public-request.ts',
      ]),
    );
    clean(rawAuthorityFindings(production, helpers), 'Fix: use verified ingress host/peer.');
  });
});

describe('public transport fence planted controls', () => {
  it.each([
    'fetch("/api");',
    'globalThis.fetch("/api");',
    'window["fetch"]("/api");',
    'const key = "fetch"; globalThis[key]("/api");',
    'const get = fetch; get("/api");',
    'const root = globalThis; const get = root.fetch; get("/api");',
    'const { fetch: get } = globalThis; get("/api");',
    'const get = globalThis.fetch.bind(globalThis); get("/api");',
    'globalThis.fetch.call(globalThis, "/api");',
    'export function createServerSdk(transport = globalThis.fetch) { return transport(url); }',
    'headers.set("X-LearnStack-Hop-Secret", secret);',
    'const name = "x-learnstack-visitor-address"; headers.append(name, peer);',
    'const headers = { "X-LearnStack-Host": host };',
    'const name = "X-LearnStack-Hop-Secret"; const headers = { [name]: secret };',
    'headers["x-learnstack-host"] = host;',
    'new Headers([["X-LearnStack-Hop-Secret", secret]]);',
  ])('refuses transport or setters: %s', (source) => {
    expect(transportFindings(buildSourceGraph({ [probe]: source }))).not.toEqual([]);
  });
  it('leaves the narrow adapter exemption, injected transport and ordinary headers clean', () => {
    clean(
      transportFindings(
        buildSourceGraph({
          [ADAPTER]: 'fetch(url); headers.set("X-LearnStack-Hop-Secret", secret);',
          [SDK_SERVER]:
            'export const request = (transport: Function) => transport(url, { cache: "no-store", headers: { Accept: "application/json" } });',
          [probe]:
            'export function load(fetch: Function) { return fetch("local"); } const message = "fetch"; headers.set("content-type", message);',
        }),
      ),
      'Injected transports/local functions are permitted.',
    );
  });
  it('follows an imported alias of a global-fetch declaration', () => {
    const graph = buildSourceGraph({
      [probe]: 'import { get } from "./helper"; get("/api");',
      'apps/web/src/app/(public)/helper.ts': 'export const get = globalThis.fetch;',
    });
    expect(transportFindings(graph).map((item) => item.file)).toContain(probe);
  });
});

describe('transport constructor aliases', () => {
  it.each([
    'new globalThis.Headers([["X-LearnStack-Hop-Secret", secret]]);',
    'const Copy = Headers; new Copy([["X-LearnStack-Hop-Secret", secret]]);',
    'const { Headers: Copy } = globalThis; new Copy([["X-LearnStack-Hop-Secret", secret]]);',
  ])('refuses the named hop setter outside the adapter: %s', (source) => {
    expect(transportFindings(buildSourceGraph({ [probe]: source }))).toEqual([
      expect.objectContaining({
        file: probe,
        reason: 'Fix: construct API-hop headers only in the configured adapter.',
      }),
    ]);
  });
  it.each([
    'new globalThis.Headers([["accept", "application/json"]]);',
    'function read(Headers: Function) { return new Headers([["X-LearnStack-Hop-Secret", secret]]); }',
    'function read(globalThis: { Headers: Function }) { const { Headers: Copy } = globalThis; return new Copy([["X-LearnStack-Hop-Secret", secret]]); }',
  ])('keeps inert or shadowed constructors clean: %s', (source) => {
    clean(transportFindings(buildSourceGraph({ [probe]: source })), 'Only real hop setters fail.');
  });
  it('catches a constructor mutation in the real render helper and preserves the adapter exemption', () => {
    const source =
      '\nexport const setterControl = (secret: string) => new globalThis.Headers([["X-LearnStack-Hop-Secret", secret]]);\n';
    const helper = 'apps/web/src/server/public-entry.ts';
    const graph = graphWith(helper, sources[helper]! + source);
    expect(reachable(graph, renderRoots)).toContain(helper);
    expect(transportFindings(graph)).toEqual([
      expect.objectContaining({
        file: helper,
        reason: 'Fix: construct API-hop headers only in the configured adapter.',
      }),
    ]);
    clean(
      transportFindings(graphWith(ADAPTER, sources[ADAPTER]! + source)),
      'The adapter owns hop setters.',
    );
  });
});

describe('server boundary planted controls', () => {
  it('refuses a deleted or type-only server marker', () => {
    expect(
      hasServerOnlyMarker(
        graphWith(ADAPTER, sources[ADAPTER]!.replace("import 'server-only';", '')).files.get(
          ADAPTER,
        )!,
      ),
    ).toBe(false);
    expect(
      hasServerOnlyMarker(
        buildSourceGraph({ [ADAPTER]: 'import type {} from "server-only";' }).files.get(ADAPTER)!,
      ),
    ).toBe(false);
  });
  it.each([
    'import { createConfiguredPublicClient } from "@/server/configured-public-client";',
    'export { createConfiguredPublicClient } from "@/server/configured-public-client";',
    'export * from "@/server/configured-public-client";',
    'const load = () => import("@/server/configured-public-client");',
    'const path = "@/server/configured-public-client"; const load = () => import(path);',
  ])('detects a client reaching the adapter through a helper: %s', (edge) => {
    const graph = buildSourceGraph({
      ...sources,
      [probe]: '"use client"; import "./helper"; export const View = () => null;',
      'apps/web/src/app/(public)/helper.ts': edge,
    });
    expect(clientBoundaryFindings(graph)).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ file: probe, reason: expect.stringContaining(ADAPTER) }),
      ]),
    );
  });
  it.each([
    ['import { type ConfiguredPublicClient }', true],
    ['export { type ConfiguredPublicClient }', true],
    ['import type { ConfiguredPublicClient }', false],
    ['export type { ConfiguredPublicClient }', false],
  ] as const)(
    'matches TypeScript 5.6.3 verbatim emission and conservative graph policy for %s',
    (declaration, runtime) => {
      const specifier = '@/server/configured-public-client';
      const source = `"use client"; ${declaration} from "${specifier}";`;
      const config = ts.readConfigFile(
        join(frontend, 'packages/config/tsconfig/base.json'),
        ts.sys.readFile,
      );
      expect(config.error).toBeUndefined();
      const { options, errors } = ts.convertCompilerOptionsFromJson(
        config.config.compilerOptions,
        frontend,
      );
      expect(errors).toEqual([]);
      expect(options.verbatimModuleSyntax).toBe(true);
      const emitted = ts.transpileModule(source, { compilerOptions: options }).outputText;
      expect(emitted.includes(specifier)).toBe(runtime);

      const graph = buildSourceGraph({ ...sources, [probe]: source });
      expect(reachable(graph, [probe]).includes(ADAPTER)).toBe(runtime);
      if (runtime)
        expect(clientBoundaryFindings(graph)).toEqual(
          expect.arrayContaining([
            expect.objectContaining({ file: probe, reason: expect.stringContaining(ADAPTER) }),
          ]),
        );
      else clean(clientBoundaryFindings(graph), 'Declaration-level type edges are erased.');
    },
  );
  it.each([
    ['inline type import', 'import { type Secret } from "private-edge";', false],
    ['inline type reexport', 'export { type Secret } from "private-edge";', false],
    ['declaration type import', 'import type { Secret } from "private-edge";', false],
    ['declaration type reexport', 'export type { Secret } from "private-edge";', false],
    ['used value import', 'import { value } from "private-edge"; console.log(value);', true],
    [
      'mixed import',
      'import { type Secret, value } from "private-edge"; console.log(value);',
      true,
    ],
    ['value reexport', 'export { value } from "private-edge";', true],
    ['mixed reexport', 'export { type Secret, value } from "private-edge";', true],
    ['side-effect import', 'import "private-edge";', true],
  ] as const)(
    'pinned Next 15.5.27 production SWC emission: %s',
    async (_name, declaration, retained) => {
      expect((require('next/package.json') as { version: string }).version).toBe('15.5.27');
      const filename = join(frontend, probe);
      const config = ts.readConfigFile(
        join(frontend, 'packages/config/tsconfig/base.json'),
        ts.sys.readFile,
      );
      expect(config.error).toBeUndefined();
      const options = getLoaderSWCOptions({
        filename,
        development: false,
        isServer: false,
        isPageFile: false,
        isCacheComponents: false,
        useCacheEnabled: false,
        hasReactRefresh: false,
        modularizeImports: undefined,
        swcPlugins: undefined,
        compilerOptions: {},
        jsConfig: config.config,
        supportedBrowsers: undefined,
        swcCacheDir: '',
        relativeFilePathFromRoot: probe,
        serverComponents: true,
        serverReferenceHashSalt: 'source-boundary-control',
        bundleLayer: WEBPACK_LAYERS.appPagesBrowser,
        esm: true,
        cacheHandlers: undefined,
      });
      const emitted = await transform('"use client"; ' + declaration, { ...options, filename });
      const file = ts.createSourceFile(probe, emitted.code as string, ts.ScriptTarget.ES2022, true);
      expect(
        file.statements.some(
          (statement) =>
            (ts.isImportDeclaration(statement) || ts.isExportDeclaration(statement)) &&
            statement.moduleSpecifier &&
            ts.isStringLiteral(statement.moduleSpecifier) &&
            statement.moduleSpecifier.text === 'private-edge',
        ),
      ).toBe(retained);
    },
  );
  it('detects an indirect private-env module outside the server directory', () => {
    const graph = buildSourceGraph({
      [probe]: '"use client"; import "./helper";',
      'apps/web/src/app/(public)/helper.ts':
        'export const secret = process.env.LEARNSTACK_PUBLIC_HOP_SECRET;',
    });
    expect(clientBoundaryFindings(graph)).toHaveLength(1);
  });
  it('refuses a direct client import of each bundled UI catalogue', () => {
    const catalogues = Object.keys(sources).filter(
      (name) => name.startsWith('apps/web/src/i18n/messages/') && name.endsWith('.json'),
    );
    expect(catalogues.length).toBeGreaterThan(0);
    for (const catalogue of catalogues) {
      const graph = buildSourceGraph({
        ...sources,
        [probe]: `"use client"; import messages from "@/${catalogue.slice('apps/web/src/'.length)}"; export const leaked = messages;`,
      });
      expect(clientBoundaryFindings(graph)).toEqual(
        expect.arrayContaining([
          expect.objectContaining({ file: probe, reason: expect.stringContaining(catalogue) }),
        ]),
      );
    }
  });
  it('permits ordinary public JSON data in a client graph', () => {
    const data = 'apps/web/src/lib/public-options.json';
    const graph = buildSourceGraph({
      [probe]: '"use client"; import options from "@/lib/public-options.json"; export { options };',
      [data]: '{"sizes":[10,20]}',
    });
    expect(reachable(graph, [probe])).toContain(data);
    expect(privateServerFiles(graph)).not.toContain(data);
    expect(clientBoundaryFindings(graph)).toEqual([]);
  });
  it('permits UI-only imports and erased type imports/re-exports', () => {
    const graph = buildSourceGraph({
      ...sources,
      [probe]:
        '"use client"; import { View } from "@learnstack/ui"; import type { ConfiguredPublicClient } from "@/server/configured-public-client"; import type { IngressContext } from "@/server/ingress"; export type { PublicSite } from "@/server/public-entry"; export type { ConfiguredPublicClient } from "@/server/configured-public-client";',
      'packages/ui/src/index.ts': 'export const View = () => null;',
    });
    expect(hasDirective(graph.files.get(probe)!, 'use client')).toBe(true);
    expect(reachable(graph, [probe])).toEqual([probe, 'packages/ui/src/index.ts'].sort());
    clean(clientBoundaryFindings(graph), 'UI and erased type edges are clean.');
  });
  it('refuses an omitted helper and a runtime-generated module path', () => {
    expect(buildSourceGraph({ [probe]: 'import "./missing";' }).unresolved).toHaveLength(1);
    expect(buildSourceGraph({ [probe]: 'import(computeModule());' }).unresolved).toHaveLength(1);
  });
});

describe('public cache fence planted controls', () => {
  it('permits the audited request-identity memo independent of formatting and comments', () => {
    const original = sources[REQUEST_MEMO]!;
    expect(original).toContain('export function requestMemo');
    const graph = graphWith(
      REQUEST_MEMO,
      original.replace('const incoming', '/* same key */ const   incoming'),
    );
    expect(cacheFindings(graph, [REQUEST_MEMO])).toEqual([]);
  });
  it.each([
    ['strong key retention', 'new WeakMap<', 'new Map<'],
    ['header value key', 'requests.get(incoming)', "requests.get(incoming.get('host'))"],
    [
      'changed insertion key',
      'requests.set(incoming, pending)',
      'requests.set(new Headers(), pending)',
    ],
    [
      'copied request identity',
      'const incoming = await headers()',
      'const incoming = new Headers(await headers())',
    ],
    ['caller-selected key', 'return async () =>', 'return async (incoming) =>'],
    [
      'eager loader before publication',
      'Promise.resolve().then(() => load(incoming))',
      'load(incoming)',
    ],
    [
      'retry after rejection',
      'return pending;',
      'return pending.finally(() => requests.delete(incoming));',
    ],
    ['exposed collection', 'return pending;', 'return requests;'],
    ['untyped value', 'Promise<T>>();', 'Promise<unknown>>();'],
    ['changed unary guard', 'if (!pending)', 'if (+pending)'],
  ])('refuses mutated request memo mechanism: %s', (_name, before, after) => {
    const original = sources[REQUEST_MEMO]!;
    expect(original).toContain(before);
    const mutated = original.replace(before, after);
    expect(mutated).not.toBe(original);
    expect(cacheFindings(graphWith(REQUEST_MEMO, mutated), [REQUEST_MEMO])).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: REQUEST_MEMO })]),
    );
  });
  it.each([
    'const shared = new Map();',
    'const shared = new WeakMap();',
    'export const leaked = new Map();',
  ])('the request helper is not exempt from additional shared collections: %s', (added) => {
    const graph = graphWith(REQUEST_MEMO, sources[REQUEST_MEMO]! + '\n' + added);
    expect(cacheFindings(graph, [REQUEST_MEMO])).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: REQUEST_MEMO })]),
    );
  });
  it('the layout policy detector refuses missing or weakened actual production declarations', () => {
    const withoutDynamic = graphWith(
      PUBLIC_LAYOUT,
      sources[PUBLIC_LAYOUT]!.replace("export const dynamic = 'force-dynamic';", ''),
    );
    expect(publicLayoutPolicyFindings(withoutDynamic)).toHaveLength(1);
    const withCaching = graphWith(
      PUBLIC_LAYOUT,
      sources[PUBLIC_LAYOUT]!.replace(
        "fetchCache = 'force-no-store'",
        "fetchCache = 'default-cache'",
      ),
    );
    expect(publicLayoutPolicyFindings(withCaching)).toHaveLength(1);
  });
  it.each([
    'export const revalidate = 60;',
    'export const dynamic = "force-static";',
    'export const fetchCache = "force-cache";',
    'export const fetchCache = "only-cache";',
    'export function generateStaticParams() { return []; }',
    'export const generateStaticParams = () => [];',
    'const params = () => []; export { params as generateStaticParams };',
    'import { unstable_cache as memo } from "next/cache"; memo(load);',
    'import * as caching from "next/cache"; caching.unstable_cache(load);',
    'import * as caching from "next/cache"; const alias = caching; alias.unstable_cache(load);',
    'import * as caching from "next/cache"; const { unstable_cache: memo } = caching; memo(load);',
    'const shared = new Map();',
    'const shared = new WeakMap();',
    'const Collection = globalThis.Map; const shared = new Collection();',
    'const bootstrapCache = {};',
    'let cachedSite;',
    'const bootstrapPromise = Promise.resolve(load());',
    'const bootstrapPromise = new Promise(load);',
    'const bootstrap = client.getSite();',
    'export const options = { cache: "force-cache" };',
    'const options = {}; options.cache = "force-cache";',
    'const mode = "force-cache"; export const options = { ["cache"]: mode };',
    'export const options = { next: { revalidate: 60 } };',
  ])('refuses caches in a transitive public helper: %s', (source) => {
    const helper = 'apps/web/src/lib/public-data.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import "@/lib/public-data";',
      [helper]: source,
    });
    expect(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])).map((item) => item.file),
    ).toContain(helper);
  });
  it('refuses an imported namespace alias reexported by a local helper', () => {
    const helper = 'apps/web/src/lib/cache-alias.ts';
    const barrel = 'apps/web/src/lib/cache-barrel.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { alias } from "@/lib/cache-barrel"; alias.unstable_cache(load);',
      [barrel]: 'export { alias } from "./cache-alias";',
      [helper]: 'import * as caching from "next/cache"; export const alias = caching;',
    });
    clean(graph.unresolved, 'The imported alias belongs to the supported local source graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: PUBLIC_LAYOUT })]),
    );
  });
  it('allows dynamic/no-store and request-local data reuse', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'export const dynamic = "force-dynamic"; export const revalidate = 0; export const fetchCache = "force-no-store"; const options = { cache: "no-store", next: { revalidate: 0 } }; export function load() { const byCourse = new Map(); return byCourse; }',
    });
    clean(
      cacheFindings(graph, [PUBLIC_LAYOUT]),
      'Dynamic/no-store/request-local reuse is permitted.',
    );
  });
  it('allows React cache because its RSC value lifetime is request-local', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { cache as memo } from "react"; import * as React from "react"; const siteCache = memo(load); const bootstrapCache = React.cache(load); export function render() { const site = siteCache(); return site; }',
    });
    clean(
      cacheFindings(graph, [PUBLIC_LAYOUT]),
      'ADR-0053 permits request-local reuse including React.cache.',
    );
  });
  it('detects actual production layout and SDK no-store mutations', () => {
    const layout = graphWith(
      PUBLIC_LAYOUT,
      sources[PUBLIC_LAYOUT]!.replace('revalidate = 0', 'revalidate = 60'),
    );
    expect(
      cacheFindings(layout, reachable(layout, renderRoots)).map((item) => item.file),
    ).toContain(PUBLIC_LAYOUT);
    const sdk = graphWith(
      SDK_SERVER,
      sources[SDK_SERVER]!.replace("cache: 'no-store'", "cache: 'force-cache'"),
    );
    expect(cacheFindings(sdk, reachable(sdk, [MIDDLEWARE])).map((item) => item.file)).toContain(
      SDK_SERVER,
    );
  });
});

describe('raw authority fence planted controls', () => {
  it.each([
    'request.headers.get("host");',
    'const name = "x-forwarded-for"; request.headers.get(name);',
    'const { headers: incoming } = request; incoming.get("host");',
    'const incoming = request.headers; incoming["host"];',
    'headers().get("X-LearnStack-Host");',
    'import { headers as readHeaders } from "next/headers"; readHeaders().get("host");',
    'request.headers["x-forwarded-host"];',
    'request.headers.host;',
    'request.socket.remoteAddress;',
    'request.nextUrl.hostname;',
    'const url = new URL(request.url); url.host;',
    'request.ip;',
  ])('refuses unsigned host/peer reads: %s', (source) => {
    expect(rawAuthorityFindings(buildSourceGraph({ [probe]: source }), [probe])).not.toEqual([]);
  });
  it('allows native capture and verified provenance in public helpers', () => {
    const graph = buildSourceGraph({
      [INGRESS]: 'request.socket.remoteAddress; request.headers.host;',
      [probe]:
        'const context = verifyProvenance(envelope, secret); const host = context.host; const peer = context.peer; headers.get("x-learnstack-ingress-provenance"); new URL("https://fixed.invalid").hostname; function describe(values: Map<string, string>) { return values.get("host"); }',
    });
    clean(
      rawAuthorityFindings(graph, [INGRESS, probe]),
      'Native ingress and authenticated payload reads are permitted.',
    );
  });
  it('detects a raw-host read planted into the actual public-entry helper', () => {
    const helper = 'apps/web/src/server/public-entry.ts';
    const graph = graphWith(
      helper,
      sources[helper]! + '\nconst unsignedHost = headers().get("host");\n',
    );
    expect(rawAuthorityFindings(graph, [helper]).map((item) => item.file)).toContain(helper);
  });
});

// R2 regressions stay within static declarations and the already supported module graph.
describe('R2 bounded source-proof regressions', () => {
  it.each([
    'export { unstable_cache as memo } from "next/cache";',
    'import { unstable_cache as externalMemo } from "next/cache"; export { externalMemo as memo };',
  ])('refuses a named external cache function through a local barrel: %s', (barrelSource) => {
    const barrel = 'apps/web/src/lib/named-cache-barrel.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { memo } from "@/lib/named-cache-barrel"; memo(load);',
      [barrel]: barrelSource,
    });
    clean(graph.unresolved, 'The named barrel is a complete supported local module graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: PUBLIC_LAYOUT })]),
    );
  });

  it.each([
    '(await headers()).get("host");',
    'import { headers as readHeaders } from "next/headers"; const incoming = await readHeaders(); incoming.get("x-forwarded-host");',
    'const incoming = request.headers; incoming.host;',
    'const { host } = request.headers;',
    'const { hostname } = request.nextUrl;',
  ])('refuses awaited or bound raw authority reads: %s', (source) => {
    const graph = buildSourceGraph({ [probe]: source });
    clean(graph.unresolved, 'The authority control has no unresolved local modules.');
    expect(rawAuthorityFindings(graph, [probe])).not.toEqual([]);
  });

  it.each([
    'const env = process.env; export const secret = env.LEARNSTACK_PUBLIC_HOP_SECRET;',
    'const { LEARNSTACK_PUBLIC_HOP_SECRET } = process.env; export const secret = LEARNSTACK_PUBLIC_HOP_SECRET;',
  ])('classifies aliased or destructured private environment modules: %s', (source) => {
    const helper = 'apps/web/src/lib/private-environment.ts';
    const graph = buildSourceGraph({
      [probe]: '"use client"; import { secret } from "@/lib/private-environment";',
      [helper]: source,
    });
    clean(graph.unresolved, 'The environment helper is a complete supported local module graph.');
    expect(privateServerFiles(graph)).toContain(helper);
    expect(clientBoundaryFindings(graph)).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: probe })]),
    );
  });

  it('refuses a default global-fetch export and its imported call', () => {
    const helper = 'apps/web/src/lib/default-transport.ts';
    const graph = buildSourceGraph({
      [probe]: 'import get from "@/lib/default-transport"; get("/api");',
      [helper]: 'export default globalThis.fetch;',
    });
    clean(graph.unresolved, 'The default-export transport is a complete supported local graph.');
    expect(transportFindings(graph).map((item) => item.file)).toEqual(
      expect.arrayContaining([probe, helper]),
    );
  });

  it('permits a default injected transport and request-local React cache through a named barrel', () => {
    const transport = 'apps/web/src/lib/default-transport.ts';
    const barrel = 'apps/web/src/lib/request-local-cache.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import get from "@/lib/default-transport"; import { memo } from "@/lib/request-local-cache"; const siteCache = memo(load); get(transport, url);',
      [transport]: 'export default (transport: Function, url: string) => transport(url);',
      [barrel]: 'export { cache as memo } from "react";',
    });
    clean(graph.unresolved, 'Both clean exports belong to a complete supported local graph.');
    clean(
      transportFindings(graph),
      'A default export remains legal when its transport is injected.',
    );
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'React cache remains request-local through a named barrel.',
    );
  });

  it('leaves ordinary public environment aliases and bindings clean', () => {
    const helper = 'apps/web/src/lib/public-environment.ts';
    const graph = buildSourceGraph({
      [probe]: '"use client"; import { title } from "@/lib/public-environment";',
      [helper]:
        'const env = process.env; const { NEXT_PUBLIC_TITLE } = env; export const title = env.NEXT_PUBLIC_TITLE ?? NEXT_PUBLIC_TITLE;',
    });
    clean(graph.unresolved, 'Ordinary public environment controls have a complete graph.');
    expect(privateServerFiles(graph)).toEqual([]);
    clean(
      clientBoundaryFindings(graph),
      'Public environment variables carry no private authority.',
    );
  });

  it('leaves verified payload bindings, inert header reads and erased private type edges clean', () => {
    const helper = 'apps/web/src/lib/private-environment.ts';
    const graph = buildSourceGraph({
      [probe]:
        '"use client"; import type { SecretShape } from "@/lib/private-environment"; const { host, peer } = verifyProvenance(envelope, secret); const incoming = await headers(); incoming.get("x-learnstack-ingress-provenance"); const { pathname } = request.nextUrl;',
      [helper]:
        'const { LEARNSTACK_PUBLIC_HOP_SECRET } = process.env; export type SecretShape = string;',
    });
    clean(graph.unresolved, 'The clean control has no unresolved runtime module edges.');
    clean(clientBoundaryFindings(graph), 'Type-only private edges are erased.');
    clean(
      rawAuthorityFindings(graph, [probe]),
      'Verified payloads and inert request inputs carry no raw authority.',
    );
  });
});

describe('R2 static export and environment collection regressions', () => {
  it('resolves repeated constants on separate branches of a hop-header expression', () => {
    const graph = buildSourceGraph({
      [probe]:
        'const hyphen = "-"; const header = "x" + hyphen + "learnstack" + hyphen + "hop" + hyphen + "secret"; headers.set(header, secret);',
    });
    clean(graph.unresolved, 'The reused constants have no unresolved local module edges.');
    expect(transportFindings(graph)).not.toEqual([]);
  });

  it('terminates cyclic constant declarations as an unresolved static module path', () => {
    const graph = buildSourceGraph({
      [probe]: 'const left = right; const right = left; import(left);',
    });
    expect(graph.unresolved).toHaveLength(1);
  });
  it.each([
    [
      'export * from "next/cache";',
      'import { unstable_cache as memo } from "@/lib/cache-exports"; memo(load);',
    ],
    [
      'import { unstable_cache } from "next/cache"; export default unstable_cache;',
      'import memo from "@/lib/cache-exports"; memo(load);',
    ],
    [
      'export { unstable_cache as memo } from "next/cache";',
      'import * as caching from "@/lib/cache-exports"; caching.memo(load);',
    ],
  ])('refuses cache functions through static export forms: %s', (helperSource, consumerSource) => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: consumerSource,
      'apps/web/src/lib/cache-exports.ts': helperSource,
    });
    clean(graph.unresolved, 'The static exports belong to a complete local source graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: PUBLIC_LAYOUT })]),
    );
  });

  it('refuses an awaited next/headers function reached through a local namespace barrel', () => {
    const graph = buildSourceGraph({
      [probe]:
        'import * as edge from "@/lib/header-exports"; (await edge.readHeaders()).get("host");',
      'apps/web/src/lib/header-exports.ts':
        'export { headers as readHeaders } from "next/headers";',
    });
    clean(graph.unresolved, 'The headers namespace belongs to a complete local source graph.');
    expect(rawAuthorityFindings(graph, [probe])).not.toEqual([]);
  });

  it('classifies private environment reads through a destructured process collection', () => {
    const helper = 'apps/web/src/lib/environment-collection.ts';
    const graph = buildSourceGraph({
      [probe]: '"use client"; import { secret } from "@/lib/environment-collection";',
      [helper]: 'const { env } = process; export const secret = env.LEARNSTACK_PUBLIC_HOP_SECRET;',
    });
    clean(
      graph.unresolved,
      'The destructured collection belongs to a complete local source graph.',
    );
    expect(privateServerFiles(graph)).toContain(helper);
    expect(clientBoundaryFindings(graph)).not.toEqual([]);
  });

  it('keeps request-local React cache clean across star, default and namespace exports', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { cache as memo } from "@/lib/react-star"; import defaultMemo from "@/lib/react-default"; import * as ReactMemo from "@/lib/react-named"; const siteCache = memo(load); const bootstrapCache = defaultMemo(load); const dataCache = ReactMemo.memo(load);',
      'apps/web/src/lib/react-star.ts': 'export * from "react";',
      'apps/web/src/lib/react-default.ts': 'import { cache } from "react"; export default cache;',
      'apps/web/src/lib/react-named.ts': 'export { cache as memo } from "react";',
    });
    clean(graph.unresolved, 'All request-local export forms have a complete local source graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'React cache preserves its request-local lifetime through static exports.',
    );
  });

  it('keeps public environment collection bindings and type-only star exports clean', () => {
    const graph = buildSourceGraph({
      [probe]:
        '"use client"; import { title } from "@/lib/environment-collection"; import type { SecretShape } from "@/lib/private-types";',
      'apps/web/src/lib/environment-collection.ts':
        'const { env: publicEnvironment } = process; export const title = publicEnvironment.NEXT_PUBLIC_TITLE;',
      'apps/web/src/lib/private-types.ts': 'export type * from "@/server/private-types";',
      'apps/web/src/server/private-types.ts': 'export type SecretShape = string;',
    });
    clean(graph.unresolved, 'The clean controls have no unresolved runtime edges.');
    clean(
      clientBoundaryFindings(graph),
      'Public environment data and erased exports carry no private authority.',
    );
  });

  it('terminates when ordinary static export stars form a cycle', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { memo } from "@/lib/cycle-a"; memo(load);',
      'apps/web/src/lib/cycle-a.ts': 'export * from "./cycle-b";',
      'apps/web/src/lib/cycle-b.ts':
        'export * from "./cycle-a"; export const memo = (value: unknown) => value;',
    });
    clean(graph.unresolved, 'The cycle remains a complete local source graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'A local pure function is not an external shared cache.',
    );
  });
});

describe('R2 namespace exports and explicit export precedence', () => {
  it('refuses shared cache through a named import of an exported namespace', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { caching } from "@/lib/namespace-cache"; caching.unstable_cache(load);',
      'apps/web/src/lib/namespace-cache.ts': 'export * as caching from "next/cache";',
    });
    clean(graph.unresolved, 'The exported namespace belongs to a complete local graph.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).not.toEqual([]);
  });

  it('refuses awaited raw headers through an exported namespace property path', () => {
    const graph = buildSourceGraph({
      [probe]:
        'import * as edge from "@/lib/namespace-headers"; (await edge.requestHeaders.headers()).get("host");',
      'apps/web/src/lib/namespace-headers.ts': 'export * as requestHeaders from "next/headers";',
    });
    clean(graph.unresolved, 'The namespace property path belongs to a complete local graph.');
    expect(rawAuthorityFindings(graph, [probe])).not.toEqual([]);
  });

  it('allows request-local React cache through an exported namespace alias', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { requestCache as ReactCache } from "@/lib/namespace-react"; const siteCache = ReactCache.cache(load);',
      'apps/web/src/lib/namespace-react.ts': 'export * as requestCache from "react";',
    });
    clean(graph.unresolved, 'The request-local namespace belongs to a complete local graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'A namespace export preserves React cache request-local lifetime.',
    );
  });

  it('lets an explicit pure runtime export override an external cache star', () => {
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import { unstable_cache as memo } from "@/lib/explicit-cache"; memo(load);',
      'apps/web/src/lib/explicit-cache.ts':
        'export * from "next/cache"; export const unstable_cache = (value: unknown) => value;',
    });
    clean(graph.unresolved, 'The explicit override belongs to a complete local graph.');
    clean(
      cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'Explicit runtime exports override same-named star exports.',
    );
  });
});

describe('remediation bounded header conversions', () => {
  it.each([
    ['Headers clone', 'new Headers(request.headers).get("host");'],
    ['qualified constructor', 'new globalThis["Headers"](request.headers).get("x-forwarded-for");'],
    ['constructor alias', 'const Copy = Headers; new Copy(request.headers).get("host");'],
    [
      'bound global constructor',
      'const { Headers: Copy } = globalThis; new Copy(request.headers).get("host");',
    ],
    [
      'bound aliased global constructor',
      'const root = globalThis; const { Headers: Copy } = root; new Copy(request.headers).get("host");',
    ],
    ['entries record', 'Object.fromEntries(request.headers).host;'],
    ['entries iterator', 'Object.fromEntries(request.headers.entries()).host;'],
    [
      'aliased entries iterator',
      'const entries = request.headers.entries(); Object.fromEntries(entries).host;',
    ],
    [
      'aliased iterator clone',
      'const entries = request.headers.entries(); const copy = entries; new Headers(copy).get("host");',
    ],
    ['conversion alias', 'const convert = Object.fromEntries; convert(request.headers).host;'],
    ['bound conversion', 'const { fromEntries: convert } = Object; convert(request.headers).host;'],
    [
      'bound global conversion owner',
      'const { Object: Obj } = globalThis; Obj.fromEntries(request.headers).host;',
    ],
    [
      'computed conversion',
      'const key = "fromEntries"; globalThis.Object[key](request.headers)["host"];',
    ],
    ['converted binding', 'const { host } = Object.fromEntries(request.headers);'],
    [
      'converted rest binding',
      'const { accept, ...incoming } = Object.fromEntries(request.headers); incoming.host;',
    ],
    [
      'converted spread',
      'const values = Object.fromEntries(request.headers); const copy = { ...values }; copy.host;',
    ],
    ['spread binding', 'const { host } = { ...Object.fromEntries(request.headers) };'],
    [
      'awaited Next collection',
      'import { headers as incoming } from "next/headers"; Object.fromEntries(await incoming()).host;',
    ],
  ])('refuses %s at the named source location', (_name, source) => {
    const graph = buildSourceGraph({ [probe]: source });
    clean(graph.unresolved, 'Header conversions have complete static inputs.');
    expect(rawAuthorityFindings(graph, [probe])).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          file: probe,
          line: 1,
          reason: expect.stringContaining('verified ingress provenance'),
        }),
      ]),
    );
  });
  it.each([
    ['inert clone read', 'new Headers(request.headers).get("accept");'],
    ['inert converted read', 'Object.fromEntries(request.headers)["accept"];'],
    ['ordinary entries', 'Object.fromEntries([["host", "fixed.invalid"]]).host;'],
    [
      'inert bound global constructor',
      'const { Headers: Copy } = globalThis; new Copy(request.headers).get("accept");',
    ],
    [
      'inert aliased entries iterator',
      'const entries = request.headers.entries(); Object.fromEntries(entries).accept;',
    ],
    ['iterator has no host property', 'const entries = request.headers.entries(); entries.host;'],
    ['plain iterator spread', 'const entries = request.headers.entries(); ({ ...entries }).host;'],
    [
      'aliased ordinary iterator',
      'const entries = new Map([["host", "fixed.invalid"]]).entries(); Object.fromEntries(entries).host;',
    ],
    [
      'shadowed bound global constructor',
      'function read(globalThis: { Headers: Function }) { const { Headers: Copy } = globalThis; return new Copy(request.headers).get("host"); }',
    ],
    [
      'shadowed bound conversion owner',
      'function read(globalThis: { Object: { fromEntries: Function } }) { const { Object: Obj } = globalThis; return Obj.fromEntries(request.headers).host; }',
    ],
    ['plain Headers spread', '({ ...request.headers }).host;'],
    ['cloned Headers spread', '({ ...new Headers(request.headers) }).host;'],
    [
      'plain Headers rest binding',
      'const { accept, ...incoming } = request.headers; incoming.host;',
    ],
    [
      'shadowed constructor',
      'function read(Headers: Function) { return new Headers(request.headers).get("host"); }',
    ],
    [
      'shadowed conversion',
      'const Object = { fromEntries: (value: unknown) => ({ host: "fixed.invalid" }) }; Object.fromEntries(request.headers).host;',
    ],
    [
      'verified payload spread',
      'const context = verifyProvenance(envelope, secret); ({ ...context }).host;',
    ],
    ['cyclic aliases', 'const left = right; const right = left; Object.fromEntries(left).host;'],
  ])('keeps %s clean', (_name, source) => {
    clean(
      rawAuthorityFindings(buildSourceGraph({ [probe]: source }), [probe]),
      'Only tracked unsigned authority is refused.',
    );
  });
  it.each([
    ['clone', 'new Headers(request.headers).get("host")'],
    ['conversion', 'Object.fromEntries(request.headers).host'],
    ['converted spread', '({ ...Object.fromEntries(request.headers) }).host'],
    [
      'bound global constructor',
      '(() => { const { Headers: Copy } = globalThis; return new Copy(request.headers).get("host"); })()',
    ],
    [
      'aliased iterator',
      '(() => { const entries = request.headers.entries(); return Object.fromEntries(entries).host; })()',
    ],
  ])('finds the %s mutation in the real public-entry render closure', (_name, expression) => {
    const helper = 'apps/web/src/server/public-entry.ts';
    const graph = graphWith(
      helper,
      sources[helper]! + `\nexport const authorityControl = (request: Request) => ${expression};\n`,
    );
    expect(reachable(graph, renderRoots)).toContain(helper);
    expect(rawAuthorityFindings(graph, reachable(graph, renderRoots))).toEqual(
      expect.arrayContaining([
        expect.objectContaining({
          file: helper,
          reason: 'Fix: derive visitor host/peer only from verified ingress provenance.',
        }),
      ]),
    );
  });
  it('keeps converted inert reads clean in the real public-entry render closure', () => {
    const helper = 'apps/web/src/server/public-entry.ts';
    const graph = graphWith(
      helper,
      sources[helper]! +
        '\nexport const authorityControl = (request: Request) => ({ ...Object.fromEntries(request.headers) }).accept;\n',
    );
    expect(reachable(graph, renderRoots)).toContain(helper);
    clean(
      rawAuthorityFindings(graph, reachable(graph, renderRoots)),
      'The real helper may read inert header values.',
    );
  });
});

describe('remediation public representation lifetimes', () => {
  it.each([
    ['Set', 'const entries = new Set();'],
    ['WeakSet', 'const entries = new WeakSet();'],
    ['bound collection', 'const { Set: Entries } = globalThis; const entries = new Entries();'],
    ['factory Map', 'function create() { return new Map(); } const retained = create();'],
    [
      'aliased factory',
      'const create = () => new Set(); const alias = create; const retained = alias();',
    ],
    ['IIFE Map', 'const retained = (() => new Map())();'],
    [
      'object method factory',
      'const factories = { create() { const entries = new Map(); return (key) => entries.get(key); } }; const retained = factories.create();',
    ],
    [
      'arrow property factory',
      'const factories = { create: () => new Map() }; const retained = factories.create();',
    ],
    [
      'aliased computed member factory',
      'const factories = { create: () => new Map() }; const owner = factories; const key = "create"; const create = owner[key]; const retained = create();',
    ],
    [
      'object binding closure capture',
      'function create() { const { entries } = { entries: new Map() }; return (key) => entries.get(key); } const retained = create();',
    ],
    [
      'array binding closure capture',
      'function create() { const [entries] = [new Map()]; return (key) => entries.get(key); } const retained = create();',
    ],
    [
      'nested aliased binding closure capture',
      'function create() { const holder = { nested: { entries: new Map(), prefix: "ok" } }; const alias = holder; const { nested: { entries: values } } = alias; return (key) => values.get(key); } const retained = create();',
    ],
    [
      'mutable primitive binding closure capture',
      'function create() { let { value } = { value: "initial" }; return (next) => value = next; } const retained = create();',
    ],
    [
      'nested factory',
      'function inner() { return new Map(); } function outer() { return inner(); } const retained = outer();',
    ],
    [
      'object factory',
      'function create() { return { entries: new Map() }; } const retained = create();',
    ],
    [
      'shorthand collection factory',
      'function create() { const entries = new Map(); return { entries }; } const retained = create();',
    ],
    [
      'shorthand mutable closure factory',
      'function create() { let pending: Promise<unknown> | undefined; const read = (load: () => Promise<unknown>) => pending ??= load(); return { read }; } const retained = create(); export const read = retained.read;',
    ],
    [
      'closure factory',
      'function create() { const entries = new Map(); return (key) => entries.get(key); } const retained = create();',
    ],
    [
      'named closure factory',
      'function create() { const entries = new Map(); function read(key) { return entries.get(key); } return read; } const retained = create();',
    ],
    [
      'method closure factory',
      'function create() { const entries = new Map(); return { read(key) { return entries.get(key); } }; } const retained = create();',
    ],
    [
      'mutable closure',
      'function create() { let value; return () => value ??= client.getSite(); } const retained = create();',
    ],
    [
      'neutral mutable binding',
      'let value; export async function read() { return value ??= await client.getCourse(); }',
    ],
    [
      'module object write',
      'const holder = {}; export async function read() { holder.value = await client.getCourse(); }',
    ],
    [
      'module object alias write',
      'const holder = {}; export async function read() { const alias = holder; alias.value = await client.getCourse(); }',
    ],
    [
      'module object destructuring write',
      'const holder = { nested: {} as Record<string, unknown> }; export function remember(value: unknown) { const { nested } = holder; nested.value = value; }',
    ],
    [
      'module array destructuring write',
      'const holders = [{} as Record<string, unknown>]; export function remember(value: unknown) { const [nested] = holders; nested.value = value; }',
    ],
    [
      'module array write',
      'const holder = []; export async function read() { holder.push(await client.getCourse()); }',
    ],
    ['static collection', 'class Holder { static entries = new Map(); }'],
    [
      'static factory collection',
      'function create() { return new WeakSet(); } class Holder { static entries = create(); }',
    ],
    [
      'static response',
      'class Holder { static value; } export async function read() { Holder.value = await client.getCourse(); }',
    ],
    [
      'static this write',
      'class Holder { static value; static async read() { this.value = await client.getCourse(); } }',
    ],
    [
      'global response',
      'export async function read() { globalThis.value = await client.getCourse(); }',
    ],
    [
      'aliased global response',
      'const root = globalThis; export async function read() { root["value"] = await client.getCourse(); }',
    ],
  ])('rejects %s in a transitive helper and the real render closure', (_label, source) => {
    const helper = 'apps/web/src/lib/lifetime-control.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]: 'import "@/lib/lifetime-control";',
      [helper]: source,
    });
    clean(graph.unresolved, 'The lifetime control uses supported static modules.');
    expect(cacheFindings(graph, reachable(graph, [PUBLIC_LAYOUT]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: helper })]),
    );
    const realHelper = 'apps/web/src/server/public-entry.ts';
    const real = graphWith(realHelper, sources[realHelper]! + '\n' + source);
    expect(reachable(real, renderRoots)).toContain(realHelper);
    expect(cacheFindings(real, reachable(real, renderRoots))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: realHelper })]),
    );
  });

  it.each([
    [
      'local collections',
      'export function read() { const entries = new Map(); const keys = new Set(); const weak = new WeakSet(); entries.set("a", 1); keys.add("a"); return { entries, keys, weak }; }',
    ],
    [
      'request factory result',
      'function create() { const entries = new Map(); return (key) => entries.get(key); } export function read() { return create(); }',
    ],
    [
      'request object method factory',
      'const factories = { create() { const entries = new Map(); return (key) => entries.get(key); } }; export function read() { return factories.create(); }',
    ],
    [
      'request arrow property factory',
      'const factories = { create: () => new Map() }; export function read() { return factories["create"](); }',
    ],
    [
      'request binding closure capture',
      'function create() { const { entries } = { entries: new Map() }; return (key) => entries.get(key); } export function read() { return create(); }',
    ],
    [
      'pure object method factory',
      'const factories = { create() { const entries = new Set([1, 2]); return entries.size; } }; const retained = factories.create();',
    ],
    [
      'pure object binding beside collection',
      'function create() { const { prefix } = { prefix: "ok", entries: new Map() }; return () => prefix; } const retained = create();',
    ],
    [
      'pure array binding beside collection',
      'function create() { const [prefix] = ["ok", new Map()]; return () => prefix; } const retained = create();',
    ],
    [
      'pure nested aliased binding beside collection',
      'function create() { const holder = { nested: { prefix: "ok", entries: new Map() } }; const alias = holder; const { nested: { prefix } } = alias; return () => prefix; } const retained = create();',
    ],
    [
      'request mutable value',
      'export async function read() { let value; value = await client.getSite(); return value; }',
    ],
    [
      'request object writes',
      'export async function read() { const holder = {}; const alias = holder; alias.value = await client.getCourse(); return holder; }',
    ],
    [
      'request object destructuring write',
      'export function remember(value: unknown) { const holder = { nested: {} as Record<string, unknown> }; const { nested } = holder; nested.value = value; return holder; }',
    ],
    [
      'request array destructuring write',
      'export function remember(value: unknown) { const holders = [{} as Record<string, unknown>]; const [nested] = holders; nested.value = value; return holders; }',
    ],
    [
      'request class instance',
      'class Holder { value; async read() { this.value = await client.getCourse(); } } export const read = () => new Holder();',
    ],
    [
      'pure factory scratch collection',
      'function count() { const entries = new Set([1, 2]); return entries.size; } const countValue = count();',
    ],
    [
      'pure immutable factory closure',
      'function create() { const prefix = "ok"; return () => prefix; } const read = create();',
    ],
    ['shadowed collection', 'const Map = class {}; const value = new Map();'],
    [
      'React cache closure',
      'import { cache as memo } from "react"; const read = memo(async () => { const entries = new Map(); entries.set("a", await client.getSite()); return entries; });',
    ],
  ])('keeps %s clean', (_label, source) => {
    clean(
      cacheFindings(buildSourceGraph({ [probe]: source }), [probe]),
      'Request-local work and immutable declarations do not retain public representations.',
    );
  });

  it('rejects a renamed copy of the request memo primitive retained by a module', () => {
    const copy = 'apps/web/src/lib/copied-memo.ts';
    const graph = buildSourceGraph({
      [probe]:
        'import { requestMemo as memo } from "@/lib/copied-memo"; export const read = memo(load);',
      [copy]: sources[REQUEST_MEMO]!,
    });
    clean(graph.unresolved, 'The copied primitive is fully present in the graph.');
    expect(cacheFindings(graph, reachable(graph, [probe]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: probe })]),
    );
    const canonical = buildSourceGraph({
      [probe]:
        'import { requestMemo as memo } from "@/server/request-memo"; export const read = memo(load);',
      [REQUEST_MEMO]: sources[REQUEST_MEMO]!,
    });
    clean(
      cacheFindings(canonical, reachable(canonical, [probe])),
      'Only the exact canonical audited request memo receives the lifetime exception.',
    );
  });

  it('follows an imported namespace factory returning retained state', () => {
    const helper = 'apps/web/src/lib/lifetime-factory.ts';
    const graph = buildSourceGraph({
      [probe]:
        'import * as factories from "@/lib/lifetime-factory"; const retained = factories.create();',
      [helper]: 'export function create() { return new Map(); }',
    });
    clean(graph.unresolved, 'The local factory implementation is included in the census.');
    expect(cacheFindings(graph, reachable(graph, [probe]))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: probe })]),
    );
  });

  it('terminates on recursive factories without inventing retained state', () => {
    const graph = buildSourceGraph({
      [probe]: 'function create() { return create(); } const retained = create();',
    });
    clean(
      cacheFindings(graph, [probe]),
      'Source recursion terminates within the bounded analysis.',
    );
  });

  it.each([
    'apps/web/src/app/(studio)/studio/page.tsx',
    'apps/web/src/app/(portal)/portal/page.tsx',
  ])('includes admitted scaffold %s in the production retention fence', (file) => {
    const graph = graphWith(file, sources[file]! + '\nconst entries = new Set();');
    expect(reachable(graph, renderRoots)).toContain(file);
    expect(cacheFindings(graph, reachable(graph, renderRoots))).toEqual(
      expect.arrayContaining([expect.objectContaining({ file })]),
    );
  });
});

describe('remediation public document navigation', () => {
  it.each([
    'import Link from "next/link"; export const View = () => <Link href="/en/courses">Courses</Link>;',
    'import { default as CourseLink } from "next/link";',
    'export { default as Link } from "next/link";',
    'export * from "next/link";',
    'const links = import("next/link");',
    'const links = require("next/link");',
    'import Links = require("next/link");',
    'import { useRouter as router } from "next/navigation";',
    'import * as navigation from "next/navigation";',
    'export { useRouter as router } from "next/navigation";',
    'export * from "next/navigation";',
    'const navigation = import("next/navigation");',
    'import Router from "next/router";',
  ])('rejects runtime router navigation: %s', (source) => {
    const graph = buildSourceGraph({ [PUBLIC_LAYOUT]: source });
    expect(navigationFindings(graph, [PUBLIC_LAYOUT])).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: PUBLIC_LAYOUT })]),
    );
  });

  it('follows aliased local barrels into the actual product component closure', () => {
    const component = 'apps/web/src/components/public/catalog.tsx';
    const barrel = 'apps/web/src/lib/navigation-control.ts';
    const graph = buildSourceGraph({
      ...sources,
      [component]:
        sources[component]! + '\nimport { CourseLink as Alias } from "@/lib/navigation-control";',
      [barrel]: 'export { default as CourseLink } from "next/link";',
    });
    clean(graph.unresolved, 'The aliased router barrel is in the production source graph.');
    expect(reachable(graph, renderRoots)).toContain(component);
    expect(navigationFindings(graph, reachable(graph, renderRoots))).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ file: component }),
        expect.objectContaining({ file: barrel }),
      ]),
    );
  });

  it('permits ordinary anchors, server control flow and erased router types', () => {
    const helper = 'apps/web/src/lib/server-navigation.ts';
    const graph = buildSourceGraph({
      [PUBLIC_LAYOUT]:
        'import { missing, move } from "@/lib/server-navigation"; import type Link from "next/link"; import type { useRouter } from "next/navigation"; export const View = () => <a href="/en/courses">Courses</a>;',
      [helper]: 'export { notFound as missing, redirect as move } from "next/navigation";',
    });
    clean(graph.unresolved, 'Allowed document navigation has a complete static graph.');
    clean(
      navigationFindings(graph, reachable(graph, [PUBLIC_LAYOUT])),
      'Server refusal/redirect control flow does not retain client Router Cache.',
    );
  });
});

describe('remediation source resolution census controls', () => {
  it.each([
    'import messages from "@/lib/control.json";',
    'export { default as messages } from "@/lib/control.json";',
    'const messages = import("@/lib/control.json");',
    'const messages = require("@/lib/control.json");',
  ])('includes valid JSON as an inert graph leaf: %s', (source) => {
    const json = 'apps/web/src/lib/control.json';
    const graph = buildSourceGraph({
      [probe]: source,
      [json]: JSON.stringify({
        cache: 'force-cache',
        'x-learnstack-host': 'An inert message key',
        body: 'import("./missing"); fetch("/api");',
      }),
    });
    clean(graph.unresolved, 'Existing strict JSON resolves like other local modules.');
    expect(reachable(graph, [probe])).toEqual([probe, json].sort());
    expect(graph.edges.get(json)).toEqual([]);
    clean(
      [...transportFindings(graph), ...cacheFindings(graph, reachable(graph, [probe]))],
      'JSON property names and text are data, not executable transport or cache configuration.',
    );
  });
  it('rejects an absent JSON import instead of exempting its extension', () => {
    const graph = buildSourceGraph({ [probe]: 'import messages from "@/lib/missing.json";' });
    expect(graph.unresolved).toEqual([
      expect.objectContaining({
        file: probe,
        reason: expect.stringContaining('Unresolved local module @/lib/missing.json'),
      }),
    ]);
  });
  it.each(['{"message":', '{"message": "hello",}', '/* comment */ {}', 'fetch("/api");'])(
    'rejects malformed JSON in the census: %s',
    (source) => {
      const json = 'apps/web/src/lib/control.json';
      const graph = buildSourceGraph({
        [probe]: 'import messages from "@/lib/control.json";',
        [json]: source,
      });
      expect(reachable(graph, [probe])).toContain(json);
      expect(graph.unresolved).toEqual([
        {
          file: json,
          line: 1,
          reason: 'Malformed JSON module: use strict inert JSON in the production census.',
        },
      ]);
    },
  );
  it('does not treat a top-level JSON string as a client directive', () => {
    const json = 'apps/web/src/lib/control.json';
    const graph = buildSourceGraph({ [json]: '"use client"' });
    clean(graph.unresolved, 'A JSON string is valid inert data.');
    expect(hasDirective(graph.files.get(json)!, 'use client')).toBe(false);
  });
  it('keeps the plugin-loaded request configuration inside authority and cache fences', () => {
    const graph = graphWith(
      I18N_REQUEST,
      sources[I18N_REQUEST]! +
        '\nimport { headers as unsafeHeaders } from "next/headers";\n' +
        'export async function dirty() { return (await unsafeHeaders()).get("host"); }\n' +
        'export const dynamic = "force-static";',
    );
    const subjects = reachable(graph, renderRoots);
    expect(subjects).toContain(I18N_REQUEST);
    expect(rawAuthorityFindings(graph, subjects)).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: I18N_REQUEST })]),
    );
    expect(cacheFindings(graph, subjects)).toEqual(
      expect.arrayContaining([expect.objectContaining({ file: I18N_REQUEST })]),
    );
  });
  it.each([
    [
      'new local alias',
      { paths: { '@/*': ['./src/*'], '~/*': ['./src/*'] }, pathsBasePath: 'apps/web' },
    ],
    ['remapped alias', { paths: { '@/*': ['./other/*'] }, pathsBasePath: 'apps/web' }],
    ['multiple targets', { paths: { '@/*': ['./src/*', './other/*'] }, pathsBasePath: 'apps/web' }],
    ['different inherited base', { paths: { '@/*': ['./src/*'] }, pathsBasePath: 'packages/ui' }],
    [
      'bare baseUrl resolution',
      { paths: { '@/*': ['./src/*'] }, pathsBasePath: 'apps/web', baseUrl: 'apps/web/src' },
    ],
  ])('refuses %s through the production alias predicate', (_name, config) => {
    const { configs, packages } = resolutionCensus();
    const file = 'apps/web/tsconfig.json';
    expect(resolutionCensusFindings({ ...configs, [file]: config }, packages)).toEqual([
      {
        file,
        line: 1,
        reason:
          'Unsupported inherited module aliases: update the bounded source resolver and its controls.',
      },
    ]);
  });
  it.each([
    ['conditional exports', { '.': { import: './src/index.ts' } }],
    ['remapped subpath', { '.': './src/index.ts', './server': './src/other.ts' }],
    ['wildcard exports', { '.': './src/index.ts', './*': './src/*.ts' }],
    ['missing root export', { './server': './src/server.ts' }],
  ])('refuses %s through the production workspace predicate', (_name, exports) => {
    const { configs, packages } = resolutionCensus();
    expect(
      resolutionCensusFindings(configs, {
        ...packages,
        'packages/sdk': { ...packages['packages/sdk'], exports },
      }),
    ).toEqual([
      {
        file: 'packages/sdk/package.json',
        line: 1,
        reason:
          'Unsupported workspace exports: update the bounded source resolver and its controls.',
      },
    ]);
  });
  it('refuses an unsupported alias targeting the actual private adapter', () => {
    const { configs, packages } = resolutionCensus();
    const graph = graphWith(probe, '"use client"; import "~private/configured-public-client";');
    const config = {
      ...configs['apps/web/tsconfig.json'],
      paths: { '@/*': ['./src/*'], '~private/*': ['./src/server/*'] },
    };
    expect(
      resolutionCensusFindings({ ...configs, 'apps/web/tsconfig.json': config }, packages).map(
        (item) => item.file,
      ),
    ).toEqual(['apps/web/tsconfig.json']);
    // The independent config guard closes the alias that this bounded graph cannot model.
    expect(reachable(graph, [probe])).toEqual([probe]);
  });
  it('refuses unsupported production JS/JSX/mts/cts including routes while excluding test files', () => {
    const names = [
      'apps/web/src/lib/runtime.mts',
      'packages/sdk/src/runtime.cts',
      'apps/web/src/app/(public)/unsafe/page.js',
      'apps/web/src/app/(public)/unsafe/layout.jsx',
      'packages/sdk/src/runtime.mjs',
      'packages/ui/src/runtime.cjs',
    ];
    expect(
      unsupportedSourceFindings([
        ...names,
        'packages/sdk/src/schema.d.mts',
        'apps/web/src/lib/probe.test.cts',
        'apps/web/src/lib/probe.test.js',
        'apps/web/src/lib/probe.spec.jsx',
      ]),
    ).toEqual(
      names.map((file) => ({
        file,
        line: 1,
        reason:
          'Unsupported production module extension: include it in the source census and resolver before use.',
      })),
    );
    clean(
      unsupportedSourceFindings(['apps/web/src/lib/runtime.ts', 'packages/sdk/src/schema.d.cts']),
      'Supported sources and declarations are clean.',
    );
  });
  it('uses named clean and dirty clients over the real production adapter graph', () => {
    const helper = 'apps/web/src/lib/client-boundary-control.ts';
    const cleanGraph = graphWith(
      probe,
      '"use client"; import "@learnstack/ui"; import type { ConfiguredPublicClient } from "@/server/configured-public-client";',
    );
    expect(hasDirective(cleanGraph.files.get(probe)!, 'use client')).toBe(true);
    expect(reachable(cleanGraph, [probe])).toContain('packages/ui/src/index.ts');
    clean(
      clientBoundaryFindings(cleanGraph),
      'A nonempty UI client with private types remains clean.',
    );
    const dirtyGraph = buildSourceGraph({
      ...sources,
      [probe]: '"use client"; import "@/lib/client-boundary-control";',
      [helper]: 'export { createConfiguredPublicClient } from "@/server/configured-public-client";',
    });
    clean(dirtyGraph.unresolved, 'The dirty client uses real supported local imports.');
    expect(clientBoundaryFindings(dirtyGraph)).toEqual(
      expect.arrayContaining([
        {
          file: probe,
          line: 1,
          reason: `Fix: pass public data through server props; client imports reach private module ${ADAPTER}.`,
        },
      ]),
    );
  });
});
