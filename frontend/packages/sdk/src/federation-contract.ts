import type { components } from './generated/schema.js';

/** ADR-0099 nonsecret connection detail; never an actor, attempt identifier, secret or certificate. */
export type FederationDetail = components['schemas']['FederationConnectionResponse'];
export type FederationConnection = components['schemas']['FederationConnectionBody'];
/** A settled operation receipt; every settled outcome is a 200. */
export type FederationReceipt = components['schemas']['FederationOperationResponse'];
/** The closed configuration body; `clientSecret` is write-only and is never returned. */
export type FederationConfigurationInput = components['schemas']['FederationConfigurationInput'];
/**
 * Federation mutations accept no Idempotency-Key (ADR-0099 § Intent-first operations): they converge through If-Match
 * on the connection revision. `version` is null only for the tenant's first configuration.
 */
export type FederationWriteOptions = Readonly<{ version: string; signal?: AbortSignal }>;
export type FederationConfigureOptions = Readonly<{ version: string | null; signal?: AbortSignal }>;
