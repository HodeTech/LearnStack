# Data export

Operating the subject data export of
[ADR-0107 § 4](../decisions/0107-data-request-fulfillment-and-account-closure.md): its
key ring, a lost or exposed key, a request that never finishes, an artifact whose purge
died, deployments, capacity, and the memory of the processes that handle it. The
request, artifact and download model is in the
[Identity module](../modules/identity/README.md); the settings are in
[Identity providers § Configuration surface](../modules/identity/providers.md#configuration-surface-and-source-bindings).

## What it needs

- `DataExport:KeyRing` (environment `DataExport__KeyRing`), a secret resolved through
  `ISecretProvider`:
  `{ "active": "<label>", "keys": { "<label>": "<base64 of 32 bytes>" } }` with one to
  four distinct keys. Labels are 1 to 32 characters of `A-Z a-z 0-9 _ -`.
- `DataExport:MaxBytes` (optional), the bundle ceiling in bytes, 1 MiB to 64 MiB, 16 MiB
  when absent.

The API refuses to start when the ring is absent or malformed. The refusal names the
setting and never its value; supply the setting and restart. A secret provider that
fails to answer is reported as its own failure, not as a bad ring. A missing ring is not
replaced with a generated key.

Both settings are read once per process. A change takes effect on a host only when that
host restarts, so restart every API host after changing either one; until then, hosts
answer by their old values.

## What the person sees

The person files and downloads on `/account/data`
([Frontend Architecture § Subject data requests](../standards/07-frontend-architecture.md#subject-data-requests)):
through a live session for that session's group, or through the retained entry for any
group where they hold or held a Membership, of any status. Each request shows its status
and next step; an available export shows when it stops being available, its size, any
part that was shortened at a cap, and a download and a delete button. The download
arrives as one `data-export.json` file. The BFF mints one ticket per download; when the
person's credential is refreshed during a long download, the next chunk read answers
`404`, and the BFF mints at most two more tickets and resumes at the failed index, each
mint counting against the ticket bounds. A download that fails after it began ends as a
failed browser download, never as a short file shown complete; the person downloads
again. A `409 data_export_ticket_limit` is shown as "wait 15 minutes, or ask for a new
copy", and `data_export_unavailable` as "ask for a new copy".

## What an administrator sees

A holder of `identity.membership.admin` sees the requests of their context's group on
`/studio/data-requests` and on each member's page, and files an export from the member's
page, which exists for an Active or Suspended Membership
([Frontend Architecture § Subject data requests](../standards/07-frontend-architecture.md#subject-data-requests)).
The administrator sees the status, the dates, who filed it and, once it completes, the
file's size and when it stops being available to the person; never the file. The page
says that the file reaches only the person, on their own data page. The form is not
offered while the member's list shows an export in progress or a file still available to
the person, and the API refuses it then anyway: an export in progress as
`request_already_active`, and an available file as `subject_export_available`, because a
new export would delete the person's file, so wait until it expires or is deleted. Only
the administrator who filed an export can cancel it before it starts or delete its file.
For an export the person or another administrator filed, the page says who may cancel it
while it still can be cancelled, and offers no way to delete its file.

## Signals

| Signal | Meaning |
|---|---|
| Start refused: `the setting DataExport:KeyRing (environment DataExport__KeyRing) is absent or malformed` | The host has no usable ring. |
| Critical log `DataExportChunkCustodyRefused` (event 3193) with tenant, artifact, chunk index and key label | A stored chunk could not be opened: its key left the ring, or the ciphertext was altered. The subject saw `404 not_found`. Logged critical the first time an artifact is refused under a key label in the process; every repeat (the other chunks, retries) is the same event at warning. A restart reports it critical again. |
| `409 data_export_unavailable` on a ticket mint for an artifact the receipt shows `available` and `readable` | The ring no longer holds the artifact's key. Only a new export helps. |
| `409 data_export_ticket_limit` on a ticket mint | A ticket bound is reached: the session already holds the most open tickets (it frees when one is consumed or expires), or the artifact has had its last ticket, which is final. The numbers are in the [Identity module](../modules/identity/README.md). |
| `learnstack_data_export_total{outcome}` | Export job runs: `completed`, `failed_too_large`, `noop`, `failed`. A steady `failed` rate means attempts keep rolling back; see [A stuck or dead export request](#a-stuck-or-dead-export-request). |
| `learnstack_data_export_duration_seconds{outcome}`, `learnstack_data_export_bundle_bytes` | Run time and stored bundle size. Bundles near `DataExport:MaxBytes` precede `failed_too_large`. |
| `learnstack_data_export_truncations_total` | Collections stored bundles state as truncated at their cap. Not an error; a rising rate means more people reach a section cap. |
| `learnstack_data_export_purges_total{outcome}` | Purge job runs: `purged`, `noop`, `not_due`, `failed`. Alert on any sustained `failed`: expired ciphertext is staying in storage. `not_due` is a purge that ran before the database clock reached the expiry; the runtime retries it. |
| `learnstack_data_export_downloads_total{operation,outcome}` | Ticket mints (`mint`) and chunk reads (`chunk`) by `served`, `refused`, `limit`, `custody_failed`. Page on any `custody_failed`: a key left the ring too early or ciphertext was altered (a chunk read racing a revocation or the expiry can also land here once). A sustained `limit` rate from few artifacts suggests a client retry loop. |
| A request shown `requested` long after it was filed | The export job keeps failing and rolling back, or it stopped; see below. |
| An artifact past `expires_at` still `available` or `expired` | Its purge job died; see below. |

Every one of these names identifiers only. A key, a ticket, a digest or bundle content
in a log or an error-tracker event is itself an incident. The counters are recorded
inside the operation's transaction: a commit that then fails is counted by the job
runtime (`learnstack_job_failed_total`) or, for a download, answered `503`.

## Capacity

Each API host runs the background-job runtime with one transactional worker, which runs
every export and every purge that host claims, one after the other, together with every
other job on the transactional queue. There is no per-tenant cap: one tenant filing
many exports delays the others' on that host. An export holds no Identity fence while it
builds its bundle and takes the shared tenant fence only for its writes, so it does not
hold up administration in its tenant; it does hold its worker for up to the job
runtime's stage budget. Add API hosts to add export throughput.

## Deploying

Every API host must run a build that registers the export and purge definitions
(`identity.data_export_*`) before anyone files an export. An older host that claims one
of those jobs does not know its definition, treats it as an invalid envelope and ends
it terminal (`invalid_envelope`); the receipt then shows `failed` (`execution_failed`)
and the subject files again. Finish a rolling deployment of the build that adds them
before the data request API is reachable.

## Rotating the key

Rotation is a two-phase rollout, because every host must open what any other host
sealed:

1. **Add.** Add the new key under a new label to the ring of every API host, keeping the
   old key active. Roll every host. Until each host has restarted, a chunk sealed
   elsewhere under the new key would not open on it, which is why the new key is not
   active yet.
2. **Switch.** Once every host runs the two-key ring, make the new label `active` and
   roll every host again. New artifacts are sealed under it; older ones still open under
   the old key.
3. **Retire.** Remove the old key seven days after the last host switched: an artifact
   is readable for seven days, so by then every artifact sealed under the old key has
   expired. That wait is the rule. A check confirms it before the key goes; it reads the
   label column only, never `data_export_chunks`. Row level security is forced on the
   artifact table and `learnstack_platform` may read only the five metadata columns the
   operator inspection lists, not the key label, so a database administrator runs the
   check as the application role (`learnstack_app`) in each tenant's own announced
   context (`app.tenant_id`, and `app.scope = 'tenant'` to see its organizations' rows):

   ```sql
   SELECT count(*) FROM public.data_export_artifacts
   WHERE key_version = '<old label>' AND status = 'available' AND expires_at > now();
   ```

   Zero in every tenant means the key can go. A non-zero count after seven days means a
   host switched late; wait until those artifacts expire.

The ring holds at most four keys, so a rotation never needs more than two at once.

## A lost key

A key that is gone cannot be recovered, and nothing sealed under it can be read again.
The artifacts are not repaired; the subject files a new export.

- Ticket mints for those artifacts answer `409 data_export_unavailable`, and any chunk
  read in flight answers `404 not_found` with the custody log above.
- Revocation does not need the key: the subject or the requesting administrator revokes
  the artifact (`POST .../artifact/revocations` with the artifact's version), which
  deletes its chunks. A new export by the subject also revokes their previous artifact
  in that group before it stores its own. An administrator's export is refused while the
  subject's artifact is still readable, and an artifact whose key is lost still counts
  as readable until it is revoked or expires: the subject revokes it, or files
  themselves.
- Put a new key in the ring as above. Then tell the affected subjects to file a new
  export; the old requests stay `completed` as receipts.

## An exposed key

A key that leaked (a dump, a misplaced secret, a compromised host) exposes every chunk
sealed under it to whoever also holds the ciphertext.

1. **Stop serving it.** Put a fresh key in the ring as `active` and remove the exposed
   one, on every host at once, without the seven-day wait. From then on no ticket is
   minted and no chunk is opened for an artifact sealed under it; each answers as a lost
   key (above), and the subjects file new exports. Until every host has restarted, a
   host on the old ring also cannot open what a restarted host sealed under the fresh
   key; that is the price of not waiting.
2. **Let the ciphertext go.** The expiry purge needs no key: each artifact's chunks are
   deleted within seven days of its creation, or at once when the subject revokes it.
   An operator act that revokes every artifact sealed under one label does not exist in
   this step;
   [Phase 11](../roadmap/phase-11-production-hardening.md) owns it, triggered by the
   first key-exposure drill. Until then, do not write those rows by hand: an UPDATE or
   DELETE outside the API writes no audit row.
3. **Backups.** Revocation and purge do not reach WAL archives or backups; the chunks in
   them stay readable to anyone holding both a backup and the key until those backups
   age out. Rotate on a fixed schedule whose interval is no longer than the backup
   retention, and destroy every stored copy of a retired key, so one key's exposure
   reaches only the artifacts of one rotation interval.
4. **Database access.** Reading the artifact tables is an exceptional, recorded act; the
   checks in this runbook read metadata columns and never the `ciphertext` column. A
   dump or copy of `data_export_chunks` or `data_export_artifacts` is personal data:
   handle it as the dumps below.

## A stuck or dead export request

An export runs as one transactional job: it builds the bundle and counts the retained
references with no fence held, then takes the shared tenant fence, re-reads the request
and either commits it `completed` with its artifact or rolls back to `requested` for the
runtime's retry (six attempts in all, the last over an hour after the first). The
request row carries no job identity, and the job row carries the request only in its
parameters.

- While the job is retrying, the receipt shows `requested`. The quarantined-jobs
  inspection of [Operator recovery](operator-recovery.md) lists only jobs in the
  `quarantined` phase: it does not show a job that is still retrying, nor one that ran
  out of attempts (`terminal_pending`, then `terminal_reason = 'retry_exhausted'`). It
  inspects and does not requeue.
- To see the others, a database administrator holding the `learnstack_platform`
  credential reads the job table directly. That role's column grant on `background_jobs`
  covers the metadata below but not `parameters`, so a job is matched to its request by
  tenant, group and time, not by request identifier: the job is created on the
  transaction that files the request, so its `created_at` (database clock) is within
  minutes of the request's (API clock).

  ```sql
  -- as learnstack_platform
  SELECT id, organization_id, definition_key, phase, normal_attempts, last_error,
         quarantine_reason, terminal_reason, dead_lettered_at, created_at
  FROM background_jobs
  WHERE tenant_id = '<tenant>'
    AND definition_key IN ('identity.data_export_tenant.v1',
                           'identity.data_export_organization.v1')
    AND created_at BETWEEN '<request created_at>'::timestamptz - interval '10 minutes'
                       AND '<request created_at>'::timestamptz + interval '10 minutes'
  ORDER BY created_at;
  ```

  `last_error` is a closed code, never exception text: `dependency_unavailable` for a
  database failure in a participant or a store, or a fence not granted in time, and
  `unexpected_fault` for anything unexplained. The exception's type is in the API's
  warning log, event 7504, under the request identifier.
- Once the job is quarantined, dead-lettered or terminal, the receipt already shows
  `failed` (`execution_failed`) without any write, while the stored row still reads
  `requested`. The next creation by the subject or an administrator stores the request
  `failed` under its fences before it judges the one-active rule, so the slot is free.
  No operator write is needed to unblock the person.
- A request that is stuck `requested` with a live job can be cancelled by its subject,
  or by the administrator who filed it, and filed again. A cancellation that lands while
  the job is building its bundle wins: the job finds the request cancelled under the
  fence, stores nothing and completes as a recorded no-op.
- A request `failed` with `export_too_large` means the bundle passed
  `DataExport:MaxBytes`. Raising the ceiling (at most 64 MiB, on every host, by restart)
  lets a new request succeed; the failed one stays.

## A download that lost its last chunk

Serving the final chunk consumes the ticket on the same transaction. A client that
never received that response (a dropped connection) finds the ticket consumed: it mints
a new ticket and reads the final index again. Each such mint counts against the ticket
bounds.

## An expired artifact whose purge died

Expiry is a time check: an artifact past `expires_at` is unreadable whether or not its
purge ran, so nothing is disclosed. Its chunks still occupy storage until something
purges them. There is no operator purge in this step and no sweeper: the automated
retention sweep is Phase 11's (ADR-0107 § 6).

- The operator's data-request inspection lists them across every tenant, oldest expiry
  first: `state=expired_artifact` in [Data erasure § Inspect](data-erasure.md#inspect),
  under `platform.data_request.read`. Within one tenant, a database administrator finds
  them by `status IN ('available','expired') AND expires_at <= now()` as the application
  role in its context, as above.
- The purge job is retried by the runtime. A quarantined purge job is not requeued by
  [Operator recovery](operator-recovery.md), which only inspects jobs; escalate it with
  that inspection as evidence.
- A new export by the same subject in the same group, filed by the subject or, since an
  expired artifact is not readable, by an administrator, withdraws the earlier artifact
  before it stores its own; for one past its expiry that withdrawal is the purge, so the
  chunks go.
- Do not delete chunk rows by hand: the guards refuse every role but the application's,
  and the chunks of an `available` artifact are never deleted piecemeal.

## Process and crash dumps

The API processes hold decrypted export chunks, request bodies with download tickets,
the export key ring and session material in memory. Clearing the buffers the code owns
is best effort; managed strings, serializer internals and the runtime keep copies.

- Do not collect process dumps or crash dumps from production by default.
- A dump that is taken is personal data and a secret: store it access-controlled, never
  attach it to a ticket or share it outside the incident, and delete it when the
  incident closes.
- A dump taken from a host with the export key ring is a key exposure: follow
  [An exposed key](#an-exposed-key).
