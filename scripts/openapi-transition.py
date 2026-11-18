"""Admission for Git-backed, fully classified incompatibilities that an Accepted ADR approves.

Each approved transition has one carrier, `backend/openapi/policy/transition-NNNN.json`,
bound to the Accepted `docs/decisions/NNNN-*.md` it names (ADR-0096, ADR-0100).
Every carrier is checked for shape, name and pair uniqueness on every run; only the one
whose exact snapshot pair matches the comparison is bound to its decision and can approve.
"""
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import stat

POLICY = 'backend/openapi/policy/'
DECISIONS = 'docs/decisions/'
TOOL = 'Linux AMD64 oasdiff 1.33.0 executable'
INPUTS = (
    'scripts/openapi-policy.py', 'backend/openapi/policy/oasdiff.json',
    'backend/openapi/policy/severity.txt', 'backend/openapi/policy/fixtures/base.json',
    'backend/openapi/policy/fixtures/cases.json', TOOL,
)
REPORTS = {'raw': ('Raw tool', 'raw-tool.json'),
           'normalized': ('Normalized tool', 'policy-tool.json'),
           'companion': ('Companion policy', 'companion.json')}
VERSION = 'oasdiff version 1.33.0'
LIMIT = 2 * 1024 * 1024  # JSON records and reports
FILE_LIMIT = 64 * 1024 * 1024  # any hashed input, including the tool executable
CARRIER = re.compile(re.escape(POLICY) + r'transition-([0-9]{4})\.json')
REVIEWED = re.compile(r'Reviewed P[0-9]{2}[a-z]?-[0-9]+ contract')
status_spec = importlib.util.spec_from_file_location('transition_adr_status', Path(__file__).with_name('adr-status.py'))
status_reader = importlib.util.module_from_spec(status_spec)
status_spec.loader.exec_module(status_reader)


def require(condition, reason):
    if not condition:
        raise ValueError('OpenAPI transition approval refused: ' + reason)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def strict_json(data):
    require(len(data) <= LIMIT, 'oversized JSON')

    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, 'duplicate JSON key')
            result[key] = value
        return result

    def invalid(_value):
        raise ValueError('OpenAPI transition approval refused: nonfinite JSON number')

    return json.loads(data, object_pairs_hook=unique, parse_constant=invalid)


def fields(value, keys):
    require(isinstance(value, dict) and set(value) == set(keys), 'invalid record fields')


def hex_digest(value):
    return isinstance(value, str) and re.fullmatch('[0-9a-f]{64}', value) is not None


def canonical_findings(findings):
    require(isinstance(findings, list) and all(isinstance(x, dict) for x in findings), 'invalid findings')
    objects = [json.dumps(x, sort_keys=True, separators=(',', ':'), ensure_ascii=True, allow_nan=False)
               for x in findings]
    require(len(objects) == len(set(objects)), 'duplicate finding')
    return ('[' + ','.join(sorted(objects)) + ']').encode('ascii')


def file_bytes(path):
    path = Path(path)
    info = path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_size <= FILE_LIMIT, 'nonregular/oversized input')
    return path.read_bytes()


def execution_identity(root, tool):
    """Capture before and after execution; an unavailable identity cannot approve."""
    try:
        return {name: digest(file_bytes(tool if name == TOOL else root / name)) for name in INPUTS}
    except (OSError, ValueError):
        # Paths and provider exception text are not diagnostic output. The
        # closed reason distinguishes unavailable evidence from byte drift.
        raise ValueError('OpenAPI transition approval refused: execution identity unavailable') from None


def carrier_paths(committed_paths):
    """Every committed `transition-*.json`, keyed by the ADR number its file name carries."""
    carriers = {}
    for path in committed_paths:
        name = Path(path).name
        if path.startswith(POLICY) and name.startswith('transition-') and Path(path).suffix == '.json':
            match = CARRIER.fullmatch(path)
            require(match is not None, 'unrecognized transition carrier name')
            carriers[match.group(1)] = path
    return carriers


def decision_path(adr, committed_paths):
    pattern = re.compile(re.escape(DECISIONS) + adr + r'-[a-z0-9][a-z0-9-]*\.md')
    found = [path for path in committed_paths if pattern.fullmatch(path)]
    require(len(found) == 1, 'missing/ambiguous decision')
    return found[0]


def reviewed_label(rows):
    labels = [label for label in rows if REVIEWED.fullmatch(label)]
    require(len(labels) == 1, 'missing/extra decision identity')
    return labels[0]


def decision_tables(adr, source):
    text = source.decode('utf-8')
    require(text.startswith('# ADR-' + adr + ': '), 'unknown decision identity')
    require(status_reader.lifecycle(text) == 'Accepted', 'decision is not Accepted')
    rows = {}
    for line in text.splitlines():
        if not line.startswith('|'):
            continue
        cells = [x.strip().strip('`') for x in line.strip('|').split('|')]
        if not hex_digest(cells[-1]):
            continue
        require(cells[0] not in rows, 'duplicate decision row')
        rows[cells[0]] = cells[1:]
    reviewed = reviewed_label(rows)
    expected = {'Prior contract', reviewed, *INPUTS, *(label for label, _ in REPORTS.values())}
    require(set(rows) == expected, 'missing/extra decision identity')
    for key in ('Prior contract', reviewed):
        require(len(rows[key]) == 2 and re.fullmatch('[0-9a-f]{8,40}', rows[key][0]), 'invalid snapshot row')
    for key in INPUTS:
        require(len(rows[key]) == 1, 'invalid input row')
    for key, _ in REPORTS.values():
        require(len(rows[key]) == 2 and re.fullmatch('[1-9][0-9]*', rows[key][0]), 'invalid finding row')
    return rows


def read_json(path):
    return strict_json(file_bytes(path))


def validate_policy_metadata(root, output):
    metadata = read_json(output / 'policy.json')
    fields(metadata, ('version', 'severity', 'severitySha256'))
    severity = file_bytes(root / 'backend/openapi/policy/severity.txt')
    require(metadata == {'version': VERSION, 'severity': severity.decode('utf-8'),
                         'severitySha256': digest(severity)}, 'comparison policy metadata mismatch')


def load_carrier(adr, source):
    """Shape only; the decision tables bind its values."""
    require(source is not None, 'missing carrier')
    record = strict_json(source)
    fields(record, ('schemaVersion', 'adr', 'baseSha256', 'headSha256', 'findings', 'inputs'))
    require(type(record['schemaVersion']) is int and record['schemaVersion'] == 1 and record['adr'] == adr,
            'invalid carrier identity')
    require(hex_digest(record['baseSha256']) and hex_digest(record['headSha256']), 'invalid snapshot digest')
    fields(record['findings'], REPORTS)
    for item in record['findings'].values():
        fields(item, ('count', 'sha256'))
    fields(record['inputs'], INPUTS)
    return record


def bind(record, rows):
    """A carrier is only the Accepted decision's tables restated; it can never differ from them."""
    require(record['baseSha256'] == rows['Prior contract'][1]
            and record['headSha256'] == rows[reviewed_label(rows)][1], 'carrier repointed from decision')
    for name, (label, _) in REPORTS.items():
        item = record['findings'][name]
        require(type(item['count']) is int and item['count'] == int(rows[label][0])
                and item['sha256'] == rows[label][1], 'finding declaration differs from decision')
    require(record['inputs'] == {name: rows[name][0] for name in INPUTS}, 'input declaration differs from decision')


def candidate(root, read_committed, committed_paths, base, head, tool, output, before):
    """Only the verified Git wrapper supplies these committed bytes/callbacks."""
    carriers = carrier_paths(committed_paths)
    if not carriers:
        return None
    # Every carrier is checked for shape, file name and pair uniqueness, so a malformed
    # or ambiguous one refuses all approval. Only the carrier that matches the comparison
    # is bound to its decision: a retained decision is history, and a later change to an
    # old ADR's status must not stop unrelated comparisons.
    records = {adr: load_carrier(adr, read_committed(path)) for adr, path in sorted(carriers.items())}
    pairs = [(record['baseSha256'], record['headSha256']) for record in records.values()]
    require(len(pairs) == len(set(pairs)), 'duplicate transition pair')
    matches = [record for record in records.values()
               if (record['baseSha256'], record['headSha256']) == (digest(base), digest(head))]
    if not matches:
        return None
    record = matches[0]
    decision = read_committed(decision_path(record['adr'], committed_paths))
    require(decision is not None, 'missing decision')
    bind(record, decision_tables(record['adr'], decision))
    require(before == record['inputs'] == execution_identity(root, tool), 'execution identity mismatch')
    for name in INPUTS:
        if name != TOOL:
            committed = read_committed(name)
            require(committed is not None and digest(committed) == record['inputs'][name], 'execution differs from verified head')
    validate_policy_metadata(root, output)
    for name, (_, filename) in REPORTS.items():
        report = read_json(output / filename)
        expected = record['findings'][name]
        require(isinstance(report, list) and len(report) == expected['count'] and digest(canonical_findings(report)) == expected['sha256'], 'unexpected ' + name + ' findings')
    summary = read_json(output / 'result.json')
    fields(summary, ('rawToolRefused', 'policyRefused'))
    require(summary == {'rawToolRefused': True, 'policyRefused': True}
            and all(type(v) is bool for v in summary.values()), 'incomplete/refusal summary mismatch')
    return record


def validate_controls(root, tool, output, record):
    require(execution_identity(root, tool) == record['inputs'], 'identity changed during controls')
    validate_policy_metadata(root, output)
    cases = read_json(root / 'backend/openapi/policy/fixtures/cases.json')
    expected = [{'name': x['name'], 'breaking': x['breaking'], 'scope': x['scope']} for x in cases]
    actual = read_json(output / 'fixtures.json')
    require(len(expected) == 60 and isinstance(actual, list) and len(actual) == 60
            and canonical_findings(actual) == canonical_findings(expected), 'incomplete/failed fixture controls')
