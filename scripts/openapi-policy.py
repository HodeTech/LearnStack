#!/usr/bin/env python3
"""Run pinned oasdiff and ADR-0024 compatibility controls without external references."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
POLICY = ROOT / 'backend/openapi/policy'
HTTP_METHODS = {'get', 'head', 'post', 'put', 'patch', 'delete', 'options', 'trace'}
FIXTURE_NAMES = {
    'remove-optional-response-field', 'remove-required-response-field', 'rename-response-field',
    'remove-request-field', 'change-field-type', 'add-required-request-field', 'remove-response-enum-value',
    'add-closed-response-enum-value', 'tighten-representable-validator', 'change-documented-status',
    'rename-path-segment', 'open-response-enum-becomes-closed', 'add-response-field', 'add-endpoint',
    'add-optional-query', 'loosen-validator', 'add-open-response-enum-value', 'add-response-header',
    'reorder-object-keys', 'improve-error-message-same-code', 'change-internal-implementation',
    'open-enum-value-removed', 'open-enum-addition-does-not-hide-other-break',
    'add-closed-root-response-enum-value', 'add-closed-array-response-enum-value',
    'add-closed-referenced-response-enum-value', 'referenced-open-response-enum-becomes-closed',
    'add-closed-request-enum-value', 'open-request-enum-becomes-closed',
    'add-open-request-enum-value', 'request-integer-becomes-number', 'response-number-becomes-integer',
}


def write(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def reachable_schemas(document):
    """Visit request/response schemas by logical position, including local object/schema refs."""
    found = {}

    def resolve(value, trail=()):
        if not isinstance(value, dict):
            return value, value, trail
        ref = value.get('$ref')
        if ref is not None:
            if not isinstance(ref, str) or not ref.startswith('#/'):
                raise ValueError('External or invalid reference refused')
            if ref in trail:
                return None, None, trail
            target = document
            for part in (part.replace('~1', '/').replace('~0', '~') for part in ref[2:].split('/')):
                target = target[part]
            resolved, enum_owner, trail = resolve(target, trail + (ref,))
            if not isinstance(resolved, dict):
                return None, None, trail
            effective = {**resolved, **{key: child for key, child in value.items() if key != '$ref'}}
            return effective, value if 'enum' in value else enum_owner, trail
        return value, value, trail

    def visit(schema, location, direction, trail=()):
        schema, enum_owner, trail = resolve(schema, trail)
        if not isinstance(schema, dict):
            return
        found[(direction, location)] = (schema, enum_owner)
        for name, child in schema.get('properties', {}).items():
            visit(child, location + ('properties', name), direction, trail)
        for keyword in ('items', 'additionalProperties', 'not'):
            visit(schema.get(keyword), location + (keyword,), direction, trail)
        for keyword in ('allOf', 'anyOf', 'oneOf', 'prefixItems'):
            for index, child in enumerate(schema.get(keyword, [])):
                visit(child, location + (keyword, index), direction, trail)

    def content(value, location, direction):
        value, _, trail = resolve(value)
        if isinstance(value, dict):
            for media, entry in value.get('content', {}).items():
                visit(entry.get('schema'), location + ('content', media, 'schema'), direction, trail)

    def parameters(values, location, direction):
        for index, value in enumerate(values):
            value, _, trail = resolve(value)
            if isinstance(value, dict):
                # Names, not list offsets, identify parameters when an optional one is added.
                position = location + (value.get('in', index), value.get('name', index))
                visit(value.get('schema'), position + ('schema',), direction, trail)
                content(value, position, direction)

    for path, item in document.get('paths', {}).items():
        item, _, _ = resolve(item)
        if not isinstance(item, dict):
            continue
        parameters(item.get('parameters', []), ('paths', path, 'parameters'), 'request')
        for method, operation in item.items():
            if method not in HTTP_METHODS:
                continue
            position = ('paths', path, method)
            parameters(operation.get('parameters', []), position + ('parameters',), 'request')
            content(operation.get('requestBody'), position + ('requestBody',), 'request')
            for status, response in operation.get('responses', {}).items():
                response_position = position + ('responses', status)
                content(response, response_position, 'response')
                response, _, _ = resolve(response)
                if isinstance(response, dict):
                    for name, header in response.get('headers', {}).items():
                        header, _, trail = resolve(header)
                        if isinstance(header, dict):
                            visit(header.get('schema'), response_position + ('headers', name, 'schema'), 'response', trail)
    return found


def companion_policy(base, head):
    """Apply ADR-0024's type/enum policy where 1.33.0 uses narrower compatibility rules."""
    normalized_base, normalized_head = copy.deepcopy(base), copy.deepcopy(head)
    old, new = reachable_schemas(normalized_base), reachable_schemas(normalized_head)
    violations = []
    for (direction, location), (schema, enum_owner) in old.items():
        current, current_enum_owner = new.get((direction, location), ({}, {}))
        def refuse(reason):
            violations.append({'id': direction + '-' + reason, 'location': list(location)})
        before_type, after_type = schema.get('type'), current.get('type')
        if before_type is not None and after_type is not None:
            before_types = set(before_type if isinstance(before_type, list) else [before_type])
            after_types = set(after_type if isinstance(after_type, list) else [after_type])
            if before_types != after_types:
                refuse('field-type-changed')
        before, after = schema.get('enum'), current.get('enum')
        if schema.get('x-extensible-enum') is True:
            if current.get('x-extensible-enum') is not True:
                refuse('extensible-enum-closed')
                continue
            if isinstance(before, list) and isinstance(after, list):
                if any(value not in after for value in before):
                    refuse('open-enum-value-removed')
                # Both remain explicitly open: additions are compatible under ADR-0024.
                # Drop only their enum lists, never types/properties/validators.
                enum_owner.pop('enum', None)
                current_enum_owner.pop('enum', None)
        elif isinstance(before, list) and isinstance(after, list):
            if any(value not in before for value in after):
                refuse('closed-enum-value-added')
            if direction == 'request' and any(value not in after for value in before):
                refuse('closed-enum-value-removed')
        if direction == 'response' and isinstance(before, list) and (not isinstance(after, list) or any(value not in after for value in before)):
            refuse('enum-value-removed')
    return normalized_base, normalized_head, violations


def run_tool(tool, base, head, output, prefix):
    command = [str(tool), '--config', str(POLICY / 'oasdiff.json'), 'breaking', str(base), str(head),
               '--allow-external-refs=false', '--severity-levels', str(POLICY / 'severity.txt'), '--fail-on', 'WARN']
    result = subprocess.run(command + ['--format', 'json'], text=True, capture_output=True, check=False)
    (output / (prefix + '.json')).write_text(result.stdout)
    (output / (prefix + '.stderr.txt')).write_text(result.stderr)
    if result.returncode not in (0, 1):
        raise RuntimeError('oasdiff failed before producing a comparison')
    findings = json.loads(result.stdout)
    if not isinstance(findings, list) or any(not isinstance(item, dict) or not isinstance(item.get('level'), int) for item in findings):
        raise ValueError('oasdiff returned an invalid report')
    refused = any(item['level'] >= 2 for item in findings)
    if refused != (result.returncode == 1):
        raise ValueError('oasdiff exit code disagrees with its findings')
    readable = subprocess.run(command + ['--format', 'text', '--color', 'never'], text=True, capture_output=True, check=False)
    (output / (prefix + '.txt')).write_text(readable.stdout)
    if readable.returncode != result.returncode:
        raise ValueError('oasdiff readable and machine reports disagree')
    return refused


def compare(tool, base, head, output):
    output.mkdir(parents=True, exist_ok=True)
    write(output / 'base.json', base)
    write(output / 'head.json', head)
    raw = run_tool(tool, output / 'base.json', output / 'head.json', output, 'raw-tool')
    old, new, companion = companion_policy(base, head)
    write(output / 'normalized-base.json', old)
    write(output / 'normalized-head.json', new)
    checked = run_tool(tool, output / 'normalized-base.json', output / 'normalized-head.json', output, 'policy-tool')
    write(output / 'companion.json', companion)
    write(output / 'result.json', {'rawToolRefused': raw, 'policyRefused': checked or bool(companion)})
    return checked or bool(companion)


def apply_changes(document, changes):
    result = copy.deepcopy(document)
    for change in changes:
        parent = result
        for part in change['path'][:-1]:
            parent = parent[part]
        key = change['path'][-1]
        if change['op'] == 'remove':
            del parent[key]
        elif change['op'] == 'set':
            parent[key] = copy.deepcopy(change['value'])
        else:
            raise ValueError('Unknown fixture operation')
    return result


def fixtures(tool, output):
    base = json.loads((POLICY / 'fixtures/base.json').read_text())
    cases = json.loads((POLICY / 'fixtures/cases.json').read_text())
    if len(cases) != len(FIXTURE_NAMES) or {case['name'] for case in cases} != FIXTURE_NAMES:
        raise ValueError('Policy fixture census is empty, incomplete or duplicated')
    results = []
    for case in cases:
        head = apply_changes(base, case['changes'])
        if case['name'] == 'reorder-object-keys':
            head = dict(reversed(list(head.items())))
        refused = compare(tool, base, head, output / case['name'])
        if refused != case['breaking']:
            raise AssertionError('Policy control failed: ' + case['name'])
        results.append({'name': case['name'], 'breaking': refused, 'scope': case['scope']})
    write(output / 'fixtures.json', results)
    print(f'Policy controls: {len(results)} passed; {sum(row["breaking"] for row in results)} breaking')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tool', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--fixtures', action='store_true')
    parser.add_argument('--base', type=Path)
    parser.add_argument('--head', type=Path)
    args = parser.parse_args()
    version = subprocess.run([str(args.tool), '--version'], text=True, capture_output=True, check=True).stdout.strip()
    if version != 'oasdiff version 1.33.0':
        raise ValueError('Expected pinned oasdiff 1.33.0')
    args.output.mkdir(parents=True, exist_ok=True)
    write(args.output / 'policy.json', {'version': version, 'severity': (POLICY / 'severity.txt').read_text(),
        'severitySha256': hashlib.sha256((POLICY / 'severity.txt').read_bytes()).hexdigest()})
    if args.fixtures:
        fixtures(args.tool, args.output)
        return 0
    if args.base is None or args.head is None:
        parser.error('--base and --head are required outside fixture mode')
    return int(compare(args.tool, json.loads(args.base.read_text()), json.loads(args.head.read_text()), args.output))


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, AssertionError, subprocess.SubprocessError) as error:
        print('OpenAPI policy failed: ' + str(error), file=sys.stderr)
        sys.exit(2)
