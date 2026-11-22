# Data export

Operating the subject data export of
[ADR-0107 § 4](../decisions/0107-data-request-fulfillment-and-account-closure.md): its key
ring, a lost key, a request that never finishes, an artifact whose purge died, and the
memory of the processes that handle it. The request, artifact and download model is in the
[Identity module](../modules/identity/README.md); the settings are in
[Identity providers § Configuration surface](../modules/identity/providers.md#configuration-surface-and-source-bindings).

## What it needs

- `DataExport:KeyRing` (environment `DataExport__KeyRing`), a secret resolved through
  `ISecretProvider`: `{ "active": "<label>", "keys": { "<label>": "<base64 of 32 bytes>" } }`
  with one to four distinct keys. Labels are 1 to 32 characters of `A-Z a-z 0-9 _ -`.
- `DataExport:MaxBytes` (optional), the bundle ceiling in bytes, 1 MiB to 64 MiB, 16 MiB
  when absent.

The API refuses to start when the ring is absent or malformed. The refusal names the
setting and never its value; supply the setting and restart. A missing ring is not
replaced with a generated key.

## Signals

| Signal | Meaning |
|---|---|
| Start refused: `the setting DataExport:KeyRing (environment DataExport__KeyRing) is absent or malformed` | The host has no usable ring. |
| Critical log `DataExportChunkCustodyRefused` (event 3193) with tenant, artifact, chunk index and key label | A stored chunk could not be opened: its key left the ring, or the ciphertext was altered. The subject saw `404 not_found`. |
| `409 data_export_unavailable` on a ticket mint for an artifact the receipt shows `available` and `readable` | The ring no longer holds the artifact's key, or the artifact already has three open tickets (wait fifteen minutes or finish a download). |
| A request shown `requested` long after it was filed | The export job keeps failing and rolling back, or it stopped; see below. |
| An artifact past `expires_at` still `available` or `expired` | Its purge job died; see below. |

Every one of these names identifiers only. A key, a ticket, a digest or bundle content in a
log or an error-tracker event is itself an incident.

## Rotating the key

Rotation is a two-phase rollout, because every host must open what any other host sealed:

1. **Add.** Add the new key under a new label to the ring of every API host and worker,
   keeping the old key active. Roll every host. Until each host has restarted, a chunk
   sealed elsewhere under the new key would not open on it, which is why the new key is not
   active yet.
2. **Switch.** Once every host runs the two-key ring, make the new label `active` and roll
   every host again. New artifacts are sealed under it; older ones still open under the old
   key.
3. **Retire.** Remove the old key only after the last artifact sealed under it has expired:
   seven days after the switch completed everywhere. Row level security is forced on the
   artifact table and `learnstack_platform` holds no grant on it, so a database
   administrator checks each tenant in its own announced context (`app.tenant_id`, and
   `app.scope = 'tenant'` to see its organizations' rows) before removing the key; the
   label is not secret:

   ```sql
   SELECT count(*) FROM public.data_export_artifacts
   WHERE key_version = '<old label>' AND status = 'available' AND expires_at > now();
   ```

   Zero in every tenant means the key can go.

The ring holds at most four keys, so a rotation never needs more than two at once.

## A lost key

A key that is gone cannot be recovered, and nothing sealed under it can be read again. The
artifacts are not repaired; the subject files a new export.

- Ticket mints for those artifacts answer `409 data_export_unavailable`, and any chunk read
  in flight answers `404 not_found` with the critical log above.
- Revocation does not need the key: the subject or the requesting administrator revokes the
  artifact (`POST .../artifact/revocations` with the artifact's version), which deletes its
  chunks. A new export also revokes the subject's previous artifact in that group before it
  stores its own.
- Put a new key in the ring as above. Then tell the affected subjects to file a new export;
  the old requests stay `completed` as receipts.

## A stuck or dead export request

An export runs as one transactional job: it either commits the request `completed` with its
artifact, or rolls back to `requested` and the runtime retries it. The request row carries no
job identity; its job is found by its group's export definition and its parameters.

- While the job is retrying, the receipt shows `requested`. Use the quarantined-jobs
  inspection of [Operator recovery](operator-recovery.md) to see why a job stopped; it
  inspects and does not requeue.
- Once the job is quarantined, dead-lettered or terminal, the receipt already shows
  `failed` (`execution_failed`) without any write, and the next creation by the subject or
  an administrator stores the request `failed` under its fences before it judges the
  one-active rule, so the slot is free. No operator write is needed to unblock the person.
- A request `failed` with `export_too_large` means the bundle passed `DataExport:MaxBytes`.
  Raising the ceiling (at most 64 MiB) lets a new request succeed; the failed one stays.

## An expired artifact whose purge died

Expiry is a time check: an artifact past `expires_at` is unreadable whether or not its purge
ran, so nothing is disclosed. Its chunks still occupy storage until something purges them.

- Find them by `status IN ('available','expired') AND expires_at <= now()` in a tenant's
  context, as above. The platform `inspect` read of ADR-0107 § 8 lists them once the step
  that registers `platform.data_request.read` ships it.
- The purge job is retried by the runtime. A quarantined purge job is not requeued by
  [Operator recovery](operator-recovery.md), which only inspects jobs; escalate it with that
  inspection as evidence.
- A new export by the same subject in the same group withdraws the earlier artifact before
  it stores its own; for one past its expiry that withdrawal is the purge, so the chunks go.
- Do not delete chunk rows by hand: the guards refuse every role but the application's, and
  the chunks of an `available` artifact are never deleted piecemeal.

## Process and crash dumps

The API and worker processes hold decrypted export chunks, request bodies with download
tickets, the export key ring and session material in memory. Clearing the buffers the code
owns is best effort; managed strings, serializer internals and the runtime keep copies.

- Do not collect process dumps or crash dumps from production by default.
- A dump that is taken is personal data and a secret: store it access-controlled, never
  attach it to a ticket or share it outside the incident, and delete it when the incident
  closes.
- A dump taken from a host with the export key ring is a key exposure: rotate the ring as
  above.
