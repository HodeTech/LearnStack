import { Audit } from 'lighthouse/core/audits/audit.js';
import { initializeConfig } from 'lighthouse/core/config/config.js';
import BaseGatherer from 'lighthouse/core/gather/base-gatherer.js';

import {
  decodedSource,
  normalizedJavaScript,
  productDomSnapshot,
} from './public-demo-contract.mjs';
import { readDemoToolchain } from './public-demo-toolchain.mjs';

/** Observe the initial audited target; never refetch a body or navigate a second time. */
export class PublicDemoDocument extends BaseGatherer {
  meta = { supportedModes: ['navigation'] };
  async startInstrumentation({ driver }) {
    this.driver = driver;
    this.session = driver.defaultSession;
    this.requests = new Map();
    this.operations = [];
    this.errors = [];
    this.closed = false;
    this.listeners = [];
    const listen = (event, listener) => {
      this.session.on(event, listener);
      this.listeners.push(() => this.session.off(event, listener));
    };
    this.foreign = (event) => {
      if (
        event.method === 'Network.responseReceived' &&
        event.params.type === 'Script' &&
        event.sessionId !== this.session.id()
      )
        this.errors.push('Unhandled Script target');
    };
    driver.targetManager.on('protocolevent', this.foreign);
    await this.session.sendCommand('Network.enable', {
      maxTotalBufferSize: 32 * 1024 * 1024,
      maxResourceBufferSize: 8 * 1024 * 1024,
    });
    const { frameTree } = await this.session.sendCommand('Page.getFrameTree');
    this.frame = frameTree.frame.id;
    listen('Network.requestWillBeSent', (event) => {
      if (this.closed || !['Document', 'Script'].includes(event.type)) return;
      if (event.type === 'Document' && event.frameId !== this.frame) return;
      if (this.requests.size >= 1000) {
        this.errors.push('Request bound');
        return;
      }
      const previous = this.requests.get(event.requestId);
      if (previous || event.redirectResponse) this.errors.push('Unexpected initial redirect');
      this.requests.set(event.requestId, {
        id: event.requestId,
        url: event.request.url,
        type: event.type,
        complete: false,
        bytesObserved: 0,
      });
    });
    listen('Network.responseReceived', (event) => {
      if (this.closed) return;
      const row = this.requests.get(event.requestId);
      if (!row) return;
      row.status = event.response.status;
      row.headers = Object.fromEntries(
        Object.entries(event.response.headers).map(([key, value]) => [
          key.toLowerCase(),
          String(value),
        ]),
      );
      if (event.response.fromServiceWorker || event.response.fromDiskCache)
        this.errors.push('Cold source came from a cache/worker');
    });
    listen('Network.dataReceived', (event) => {
      const row = this.requests.get(event.requestId);
      if (row) row.bytesObserved += event.dataLength;
    });
    listen('Network.loadingFailed', (event) => {
      if (this.requests.has(event.requestId)) this.errors.push('Initial source failed');
    });
    listen('Network.loadingFinished', (event) => {
      const row = this.requests.get(event.requestId);
      if (this.closed || !row) return;
      this.operations.push(
        this.session
          .sendCommand('Network.getResponseBody', { requestId: event.requestId })
          .then((body) => {
            row.body = body;
            row.complete = true;
          })
          .catch(() => {
            this.errors.push('Complete initial source body unavailable');
          }),
      );
    });
  }

  async getArtifact({ driver }) {
    try {
      const dom = await driver.executionContext.evaluate(productDomSnapshot, {
        args: [],
        useIsolation: true,
      });
      // Freeze the collection window before draining already owned body reads.
      this.closed = true;
      await Promise.all(this.operations);
      if (this.errors.length) throw new Error(this.errors[0]);
      const rows = [...this.requests.values()];
      if (rows.some((row) => !row.complete || row.status !== 200 || !row.body))
        throw new Error('Initial response/source inventory incomplete');
      const documents = rows.filter((row) => row.type === 'Document');
      if (documents.length !== 1) throw new Error('One actual main document is required');
      const document = documents[0];
      const html = decodedSource(document.body);
      const inventory = rows.filter((row) => row.type === 'Script');
      for (const row of rows) {
        // dataLength is decoded bytes, unlike encoded Content-Length/transferSize.
        if (row.bytesObserved && Buffer.byteLength(decodedSource(row.body)) !== row.bytesObserved)
          throw new Error('Decoded source completeness disagreement');
      }
      const gzip = normalizedJavaScript(html, inventory);
      return {
        url: document.url,
        status: document.status,
        headers: document.headers,
        html,
        dom,
        redirects: 0,
        gzip,
        inventory: inventory.map((row) => ({ id: row.id, url: row.url })),
        cutoff: Date.now(),
      };
    } finally {
      this.closed = true;
      for (const remove of this.listeners) remove();
      driver.targetManager.off('protocolevent', this.foreign);
    }
  }
}

export class InitialJavaScriptGzip extends Audit {
  static get meta() {
    return {
      id: 'initial-js-gzip-bytes',
      title: 'Normalized initial JavaScript gzip bytes',
      description: 'Complete initial external sources plus original inline executable bodies.',
      scoreDisplayMode: Audit.SCORING_MODES.INFORMATIVE,
      requiredArtifacts: ['PublicDemoDocument', 'URL'],
    };
  }
  static audit(artifacts) {
    return { score: 1, numericValue: artifacts.PublicDemoDocument.gzip.total, numericUnit: 'byte' };
  }
}

export const demoLighthouseConfiguration = {
  extends: 'lighthouse:default',
  artifacts: [{ id: 'PublicDemoDocument', gatherer: { instance: new PublicDemoDocument() } }],
  audits: [{ implementation: InitialJavaScriptGzip }],
  settings: {
    onlyCategories: ['performance', 'accessibility'],
    onlyAudits: ['initial-js-gzip-bytes'],
    skipAudits: ['bf-cache'],
    formFactor: 'mobile',
    throttlingMethod: 'simulate',
    screenEmulation: {
      mobile: true,
      width: 412,
      height: 823,
      deviceScaleFactor: 1.75,
      disabled: false,
    },
    throttling: {
      rttMs: 150,
      throughputKbps: 1638.4,
      cpuSlowdownMultiplier: 4,
      requestLatencyMs: 562.5,
      downloadThroughputKbps: 1474.56,
      uploadThroughputKbps: 675,
    },
  },
};

export function configurationCensus(config) {
  // Compare the serializable configuration receipt: absent optional properties
  // must not differ merely because Lighthouse represents them as undefined.
  return JSON.parse(
    JSON.stringify({
      versions: readDemoToolchain(),
      artifacts: config.artifacts
        .map((row) => ({ id: row.id, dependencies: row.dependencies }))
        .sort((a, b) => a.id.localeCompare(b.id)),
      audits: config.audits.map((row) => row.implementation.meta.id).sort(),
      categories: Object.fromEntries(
        Object.entries(config.categories).map(([key, value]) => [
          key,
          value.auditRefs.map((row) => ({ id: row.id, weight: row.weight })),
        ]),
      ),
      settings: {
        formFactor: config.settings.formFactor,
        screenEmulation: config.settings.screenEmulation,
        throttlingMethod: config.settings.throttlingMethod,
        throttling: config.settings.throttling,
        disableStorageReset: config.settings.disableStorageReset,
      },
    }),
  );
}

export async function resolvedDemoCensus(flags) {
  const { resolvedConfig } = await initializeConfig(
    'navigation',
    demoLighthouseConfiguration,
    flags,
  );
  return configurationCensus(resolvedConfig);
}
