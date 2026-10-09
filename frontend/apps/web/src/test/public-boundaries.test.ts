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
  INGRESS,
  MIDDLEWARE,
  privateServerFiles,
  publicLayoutPolicyFindings,
  PUBLIC_LAYOUT,
  rawAuthorityFindings,
  reachable,
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
      } else if (/\.tsx?$/.test(entry.name) && !/\.(?:test|spec|d)\.tsx?$/.test(entry.name)) {
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
// P5 middleware supplies bootstrap; public page composition remains P6.
const ROOT_LAYOUT = 'apps/web/src/app/layout.tsx';
const renderRoots = [...publicRoots, ROOT_LAYOUT, MIDDLEWARE];
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
        MIDDLEWARE,
        PUBLIC_LAYOUT,
        ROOT_LAYOUT,
        SDK_SERVER,
        'packages/sdk/src/index.ts',
        'packages/sdk/src/response-policy.ts',
        'packages/ui/src/index.ts',
        'apps/web/src/server/public-entry.ts',
      ]),
    );
    expect(publicRoots).toContain(PUBLIC_LAYOUT);
    expect(privateServerFiles(production)).toEqual(
      expect.arrayContaining([ADAPTER, INGRESS, SDK_SERVER]),
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
    clean(extensionFindings, 'Fix: model every production TypeScript runtime extension.');
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
      expect.arrayContaining([PUBLIC_LAYOUT, MIDDLEWARE, ADAPTER, SDK_SERVER]),
    );
    clean(
      cacheFindings(production, publicGraph),
      'Fix: keep public rendering dynamic and no-store.',
    );
  });
  it('public pages and their transitive helpers do not read raw authority', () => {
    const helpers = publicGraph.filter((name) => name !== INGRESS);
    expect(helpers).toEqual(
      expect.arrayContaining([PUBLIC_LAYOUT, MIDDLEWARE, 'apps/web/src/server/public-entry.ts']),
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
    'pinned Next 15.5.18 production SWC emission: %s',
    async (_name, declaration, retained) => {
      expect((require('next/package.json') as { version: string }).version).toBe('15.5.18');
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

describe('remediation source resolution census controls', () => {
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
  it('refuses unsupported production mts/cts while excluding declarations and test files', () => {
    const names = ['apps/web/src/lib/runtime.mts', 'packages/sdk/src/runtime.cts'];
    expect(
      unsupportedSourceFindings([
        ...names,
        'packages/sdk/src/schema.d.mts',
        'apps/web/src/lib/probe.test.cts',
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
