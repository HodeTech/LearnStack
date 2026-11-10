"""ADR-0096 admission for one Git-backed, fully classified incompatibility."""
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import stat

ADR = 'docs/decisions/0096-p035-administration-contract-baseline.md'
CARRIER = 'backend/openapi/policy/transition-0096.json'
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
LIMIT = 2 * 1024 * 1024
status_spec = importlib.util.spec_from_file_location('transition_adr_status', Path(__file__).with_name('adr-status.py'))
status_reader = importlib.util.module_from_spec(status_spec)
status_spec.loader.exec_module(status_reader)


def require(condition, reason):
    if not condition:
        raise ValueError('ADR-0096 approval refused: ' + reason)


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
        raise ValueError('ADR-0096 approval refused: nonfinite JSON number')

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
    require(stat.S_ISREG(info.st_mode) and info.st_size <= max(LIMIT, 64 * 1024 * 1024), 'nonregular/oversized input')
    return path.read_bytes()


def execution_identity(root, tool):
    """Capture before and after execution; an unavailable identity cannot approve."""
    try:
        return {name: digest(file_bytes(tool if name == TOOL else root / name)) for name in INPUTS}
    except (OSError, ValueError):
        return None


def decision_tables(source):
    text = source.decode('utf-8')
    require(text.startswith('# ADR-0096: '), 'unknown decision identity')
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
    expected = {'Prior contract', 'Reviewed P03-5 contract', *INPUTS,
                *(label for label, _ in REPORTS.values())}
    require(set(rows) == expected, 'missing/extra decision identity')
    for key in ('Prior contract', 'Reviewed P03-5 contract'):
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


def candidate(root, read_committed, committed_paths, base, head, tool, output, before):
    """Only the verified Git wrapper supplies these committed bytes/callbacks."""
    source = read_committed(CARRIER)
    if source is None:
        return None
    transitions = {p for p in committed_paths if p.startswith('backend/openapi/policy/')
                   and Path(p).name.startswith('transition-') and Path(p).suffix == '.json'}
    require(transitions == {CARRIER}, 'missing/extra transition carrier')
    record = strict_json(source)
    fields(record, ('schemaVersion', 'adr', 'baseSha256', 'headSha256', 'findings', 'inputs'))
    require(type(record['schemaVersion']) is int and record['schemaVersion'] == 1 and record['adr'] == '0096', 'invalid carrier identity')
    require(hex_digest(record['baseSha256']) and hex_digest(record['headSha256']), 'invalid snapshot digest')
    if (digest(base), digest(head)) != (record['baseSha256'], record['headSha256']):
        return None
    decision = read_committed(ADR)
    require(decision is not None, 'missing decision')
    rows = decision_tables(decision)
    require(record['baseSha256'] == rows['Prior contract'][1]
            and record['headSha256'] == rows['Reviewed P03-5 contract'][1], 'carrier repointed from decision')
    fields(record['findings'], REPORTS)
    for name, (label, _) in REPORTS.items():
        item = record['findings'][name]
        fields(item, ('count', 'sha256'))
        require(type(item['count']) is int and item['count'] == int(rows[label][0])
                and item['sha256'] == rows[label][1], 'finding declaration differs from decision')
    fields(record['inputs'], INPUTS)
    require(record['inputs'] == {name: rows[name][0] for name in INPUTS}, 'input declaration differs from decision')
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
