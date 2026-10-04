# OpenAPI compatibility controls

[ADR-0024](../../../docs/decisions/0024-api-versioning-policy.md) owns the policy.
[P02d-4 G31](../../../docs/roadmap/phase-02d-walking-skeleton.md#openapi-sdk-and-required-check-plan)
owns pins, severity overrides, artifact contents and required-check rollout.

`fixtures/base.json` and `fixtures/cases.json` cover each representable policy row
with breaking mutations and nonbreaking controls. The runner checks a literal
non-empty census against the real pinned executable. It retains raw tool reports,
normalized reports and companion findings; no external references are allowed.

The pinned tool misses some root enum additions, open-to-closed enum changes and
numeric field type changes. It also rejects additions to an explicitly open
response enum. The companion walks reachable request/response schemas, including
local schema and Response/RequestBody/Parameter references. It refuses type changes,
closed enum additions and open enum closure/removal, and removes only matching
open enum lists for the tool. Fields, validators and other constraints remain.
A combined open addition and field removal proves that normalization cannot hide
another breaking change. Current public enums are closed. A future open-set enum
still requires compatible generation and consumer proof before shipping.

Schema limits cannot describe every runtime rule or what a status means.
`PublicReadProtocolTests` and `PublicEducationHttpTests` prove locale, slug and
cursor grammar/multiplicity, unsupported locales and hidden-resource outcomes;
`PublicSiteHttpTests` proves site configuration/lifecycle status mapping. These
runtime cases complement documented validator/status mutations in the fixtures.

From the repository root, with the verified pinned executable:

```sh
python3 scripts/test-openapi-ci.py
python3 scripts/openapi-policy.py --tool /path/to/oasdiff --fixtures --output /tmp/openapi-controls
python3 scripts/openapi-ci.py --base BASE_REF --head HEAD_REF --tool /path/to/oasdiff --output /tmp/openapi-diff
```

The installer is Linux AMD64 only and verifies the approved archive before
extracting/executing it. A workstation can pass a separately checksum-verified
executable of the same pinned version. The first baseline is proved by building
and serving the verified base Program; a missing file alone is insufficient.
