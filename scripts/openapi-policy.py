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
    'shared-reference-open-first',
    'shared-reference-closed-first',
    'inherited-parameter-validator-tightened',
    'inherited-parameter-removed',
    'inherited-parameter-became-required',
    'inherited-parameter-relocated-type',
    'inherited-parameter-relocated-enum-add',
    'inherited-parameter-relocated-enum-remove',
    'inherited-parameter-relocated-clean',
    'content-parameter-field-removed',
    'content-parameter-validator-tightened',
    'content-header-field-removed',
    'content-header-output-bound-strengthened',
    'content-header-field-type-changed',
    'content-header-closed-enum-added',
    'content-header-open-enum-closed',
    'content-parameter-validator-loosened',
    'content-header-field-added',
}


def write(path, value):
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def materialized_document(document):
    """Resolve each reachable local reference into its own occurrence; keep cycles bounded."""
    result = copy.deepcopy(document)

    def resolve(value, trail=()):
        if not isinstance(value, dict) or '$ref' not in value:
            return copy.deepcopy(value), trail
        ref = value['$ref']
        if not isinstance(ref, str) or not ref.startswith('#/'):
            raise ValueError('External or invalid reference refused')
        if ref in trail:
            return copy.deepcopy(value), trail
        target = document
        for part in (part.replace('~1', '/').replace('~0', '~') for part in ref[2:].split('/')):
            target = target[part]
        target, trail = resolve(target, trail + (ref,))
        if not isinstance(target, dict):
            raise ValueError('Reference does not identify an object')
        return {**target, **copy.deepcopy({key: child for key, child in value.items() if key != '$ref'})}, trail

    def schema(value, trail=()):
        value, trail = resolve(value, trail)
        if not isinstance(value, dict) or '$ref' in value:
            return value
        if 'properties' in value:
            value['properties'] = {name: schema(child, trail) for name, child in value['properties'].items()}
        for keyword in ('items', 'additionalProperties', 'not'):
            if keyword in value:
                value[keyword] = schema(value[keyword], trail)
        for keyword in ('allOf', 'anyOf', 'oneOf', 'prefixItems'):
            if keyword in value:
                value[keyword] = [schema(child, trail) for child in value[keyword]]
        return value

    def content_object(value):
        value, trail = resolve(value)
        if not isinstance(value, dict):
            return value
        if 'schema' in value:
            value['schema'] = schema(value['schema'], trail)
        for entry in value.get('content', {}).values():
            if 'schema' in entry:
                entry['schema'] = schema(entry['schema'], trail)
        return value

    for path, original in document.get('paths', {}).items():
        item, _ = resolve(original)
        inherited = [content_object(parameter) for parameter in item.pop('parameters', [])]
        for method, operation in item.items():
            if method not in HTTP_METHODS:
                continue
            effective = {(parameter['in'], parameter['name']): parameter for parameter in inherited}
            for parameter in operation.get('parameters', []):
                parameter = content_object(parameter)
                effective[(parameter['in'], parameter['name'])] = parameter
            if effective or 'parameters' in operation:
                operation['parameters'] = copy.deepcopy(list(effective.values()))
            if 'requestBody' in operation:
                operation['requestBody'] = content_object(operation['requestBody'])
            for status, response in operation.get('responses', {}).items():
                response = content_object(response)
                if 'headers' in response:
                    response['headers'] = {name: content_object(header) for name, header in response['headers'].items()}
                operation['responses'][status] = response
        result['paths'][path] = item
    return result


def project_parameter_headers(document):
    """Mirror schema/content forms into tool-supported bodies, retaining original metadata."""
    projections = []

    def project(value, location, direction):
        entries = list(value.get('content', {}).items())
        if 'schema' in value:
            entries.append(('application/json', {'schema': value['schema']}))
        for media, entry in entries:
            if 'schema' not in entry:
                continue
            identity = json.dumps([direction, *location, media], separators=(',', ':'))
            path = '/__learnstack_policy__/' + hashlib.sha256(identity.encode()).hexdigest()
            body = {'content': {media: {'schema': copy.deepcopy(entry['schema'])}}}
            if direction == 'request':
                body['required'] = value.get('required', False)
                operation = {'requestBody': body, 'responses': {'204': {'description': 'Policy projection'}}}
                method = 'post'
            else:
                operation = {'responses': {'200': {'description': 'Policy projection', **body}}}
                method = 'get'
            operation['x-learnstack-policy-source'] = list(location)
            projections.append((path, {method: operation}))

    for path, item in document.get('paths', {}).items():
        for method, operation in item.items():
            if method not in HTTP_METHODS:
                continue
            for parameter in operation.get('parameters', []):
                project(parameter, ('paths', path, method, 'parameters', parameter['in'], parameter['name']), 'request')
            for status, response in operation.get('responses', {}).items():
                for name, header in response.get('headers', {}).items():
                    project(header, ('paths', path, method, 'responses', status, 'headers', name), 'response')
    for path, operation in projections:
        if path in document['paths']:
            raise ValueError('Policy projection path collides with the source document')
        document['paths'][path] = operation


def reachable_schemas(document):
    """Index materialized request/response schemas by effective operation position."""
    found = {}

    def visit(value, location, direction):
        if not isinstance(value, dict):
            return
        found[(direction, location)] = value
        for name, child in value.get('properties', {}).items():
            visit(child, location + ('properties', name), direction)
        for keyword in ('items', 'additionalProperties', 'not'):
            visit(value.get(keyword), location + (keyword,), direction)
        for keyword in ('allOf', 'anyOf', 'oneOf', 'prefixItems'):
            for index, child in enumerate(value.get(keyword, [])):
                visit(child, location + (keyword, index), direction)

    def content(value, location, direction):
        if not isinstance(value, dict):
            return
        visit(value.get('schema'), location + ('schema',), direction)
        for media, entry in value.get('content', {}).items():
            visit(entry.get('schema'), location + ('content', media, 'schema'), direction)

    for path, item in document.get('paths', {}).items():
        for method, operation in item.items():
            if method not in HTTP_METHODS:
                continue
            position = ('paths', path, method)
            for parameter in operation.get('parameters', []):
                content(parameter, position + ('parameters', parameter['in'], parameter['name']), 'request')
            content(operation.get('requestBody'), position + ('requestBody',), 'request')
            for status, response in operation.get('responses', {}).items():
                response_position = position + ('responses', status)
                content(response, response_position, 'response')
                for name, header in response.get('headers', {}).items():
                    content(header, response_position + ('headers', name), 'response')
    return found


def companion_policy(base, head):
    """Analyze immutable occurrences, then normalize separate copies of matching open enums."""
    effective_base, effective_head = materialized_document(base), materialized_document(head)
    project_parameter_headers(effective_base)
    project_parameter_headers(effective_head)
    old, new = reachable_schemas(effective_base), reachable_schemas(effective_head)
    violations, open_locations = [], []
    for (direction, location), schema in old.items():
        current = new.get((direction, location), {})

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
                open_locations.append((direction, location))
        elif isinstance(before, list) and isinstance(after, list):
            if any(value not in before for value in after):
                refuse('closed-enum-value-added')
            if direction == 'request' and any(value not in after for value in before):
                refuse('closed-enum-value-removed')
        if direction == 'response' and isinstance(before, list) and (not isinstance(after, list) or any(value not in after for value in before)):
            refuse('enum-value-removed')
    normalized_base, normalized_head = copy.deepcopy(effective_base), copy.deepcopy(effective_head)
    normalized_old, normalized_new = reachable_schemas(normalized_base), reachable_schemas(normalized_head)
    for location in open_locations:
        # Per-occurrence copies leave every closed consumer of a shared reference intact.
        normalized_old[location].pop('enum', None)
        normalized_new[location].pop('enum', None)
    return normalized_base, normalized_head, violations


def run_tool(tool, base, head, output, prefix):
    command = [str(tool), '--config', str(POLICY / 'oasdiff.json'), 'breaking', str(base), str(head),
               '--allow-external-refs=false', '--flatten-params', '--severity-levels', str(POLICY / 'severity.txt'), '--fail-on', 'WARN']
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
