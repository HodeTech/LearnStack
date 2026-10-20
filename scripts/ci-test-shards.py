#!/usr/bin/env python3
"""ADR-0063 complete selector assignment. Timing weights never define membership."""
from collections import defaultdict
import hashlib
import json
import importlib.util
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
PLAN = ROOT / 'scripts/ci-test-shards-plan.json'
MAX_CASES = 100000
MAX_PLAN_BYTES = 4 * 1024 * 1024
ASSEMBLY = re.compile(r'[a-z][a-z0-9._-]{0,199}')
METHOD = re.compile(r'[A-Za-z_][A-Za-z0-9_.+`]{0,2047}')
UUID = re.compile(r'[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}')
PARTS = ('allTests', 'nonDocker', 'all', 'native', 'ordinary')
FILTERS = {'base': 'Requires!=Docker', 'architecture': 'Requires!=Docker',
           'ordinary': 'Requires=Docker&NativeCrashProof!=true',
           'native': 'Requires=Docker&NativeCrashProof=true'}


class Refusal(Exception):
    """Only closed reason codes leave this owner; never interpolate input."""
    def __init__(self, reason):
        self.reason = reason
        super().__init__(reason)


def require(value, reason):
    if not value:
        raise Refusal(reason)


def integer(value, low, high):
    return type(value) is int and low <= value <= high


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True).encode()


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def unique_object(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, 'duplicate_json_key')
        result[name] = value
    return result


def load_plan(path=PLAN):
    try:
        with Path(path).open('rb') as stream:
            raw = stream.read(MAX_PLAN_BYTES + 1)
        require(len(raw) <= MAX_PLAN_BYTES, 'plan_bound')
        plan = json.loads(raw, object_pairs_hook=unique_object)
    except (OSError, ValueError, UnicodeError):
        raise Refusal('plan_unreadable') from None
    validate_plan(plan)
    return plan


def method_key(case):
    return case['assembly'], case['method']


def case_key(case):
    return case['assembly'], case['id']


def validate_selector(assembly, method):
    require(isinstance(assembly, str) and ASSEMBLY.fullmatch(assembly), 'assembly_invalid')
    # The first contract deliberately refuses syntax it has not qualified. No
    # escaping fallback, shell evaluation or display-name parser selects cases.
    require(isinstance(method, str) and METHOD.fullmatch(method) and '.' in method,
            'selector_unsupported')


def validate_plan(plan):
    require(isinstance(plan, dict) and set(plan) == {'schemaVersion', 'algorithmVersion',
            'architectureAssemblies', 'counts', 'salt', 'defaultMethodMs', 'bounds',
            'sources', 'weights', 'sharedFixtureClasses', 'fixedCosts'}, 'plan_shape_invalid')
    require(plan['schemaVersion'] == 1 and type(plan['schemaVersion']) is int
            and plan['algorithmVersion'] == 1 and type(plan['algorithmVersion']) is int,
            'plan_version_invalid')
    counts = plan['counts']
    require(isinstance(counts, dict) and set(counts) == {'architecture', 'ordinary'}
            and integer(counts['architecture'], 4, 6) and integer(counts['ordinary'], 8, 12)
            and 2 + sum(counts.values()) <= 20, 'plan_topology_invalid')
    architectures = plan['architectureAssemblies']
    require(isinstance(architectures, list) and architectures == ['learnstack.tests.architecture'],
            'architecture_project_invalid')
    require(plan['salt'] == 'LearnStack.CI.B.v1' and integer(plan['defaultMethodMs'], 1, 86400000),
            'plan_estimate_invalid')
    require(plan['bounds'] == {'filterBytes': 32768, 'argvBytes': 65536}
            and all(type(x) is int for x in plan['bounds'].values()), 'plan_bounds_invalid')
    sources = plan['sources']
    require(isinstance(sources, dict) and 0 < len(sources) <= 100, 'weight_sources_invalid')
    for name, source in sources.items():
        require(isinstance(name, str) and re.fullmatch(r'[a-z0-9_-]{1,64}', name)
                and isinstance(source, dict) and set(source) == {'kind', 'artifactSha256',
                    'sourceSha', 'runId', 'note'}
                and source['kind'] in ('local_trx', 'hosted_console', 'hosted_trx')
                and isinstance(source['artifactSha256'], str)
                and re.fullmatch(r'[0-9a-f]{64}', source['artifactSha256'])
                and (source['sourceSha'] is None or isinstance(source['sourceSha'], str)
                     and re.fullmatch(r'[0-9a-f]{40}', source['sourceSha']))
                and (source['runId'] is None or isinstance(source['runId'], str)
                     and re.fullmatch(r'[1-9][0-9]{0,19}', source['runId']))
                and isinstance(source['note'], str) and len(source['note']) <= 400
                and not any(ord(c) < 32 for c in source['note']), 'weight_source_invalid')
    weights, seen = plan['weights'], set()
    require(isinstance(weights, list) and len(weights) <= MAX_CASES, 'weights_invalid')
    for item in weights:
        require(isinstance(item, dict) and set(item) == {'assembly', 'method', 'milliseconds', 'source'},
                'weight_invalid')
        validate_selector(item['assembly'], item['method'])
        key = method_key(item)
        require(key not in seen and integer(item['milliseconds'], 1, 86400000)
                and isinstance(item['source'], str) and item['source'] in sources, 'weight_invalid')
        seen.add(key)
    classes = plan['sharedFixtureClasses']
    require(isinstance(classes, list) and classes and len(classes) <= 1000
            and all(isinstance(x, str) and METHOD.fullmatch(x) for x in classes)
            and len(set(classes)) == len(classes), 'fixture_observations_invalid')
    require(isinstance(plan['fixedCosts'], dict)
            and set(plan['fixedCosts']) == {'basePreTestMs', 'discoveryMs', 'fixtureStartupMs', 'note'}
            and isinstance(plan['fixedCosts']['basePreTestMs'], list)
            and 0 < len(plan['fixedCosts']['basePreTestMs']) <= 100
            and all(integer(x, 1, 86400000) for x in plan['fixedCosts']['basePreTestMs'])
            and all(plan['fixedCosts'][key] is None or integer(plan['fixedCosts'][key], 1, 86400000)
                    for key in ('discoveryMs', 'fixtureStartupMs'))
            and isinstance(plan['fixedCosts']['note'], str)
            and 0 < len(plan['fixedCosts']['note']) <= 400
            and not any(ord(c) < 32 for c in plan['fixedCosts']['note']),
            'fixed_cost_observations_invalid')


def population(cases):
    require(isinstance(cases, list) and len(cases) <= MAX_CASES, 'population_invalid')
    result, selectors = {}, {}
    for case in cases:
        require(isinstance(case, dict) and set(case) == {'assembly', 'id', 'name', 'method'},
                'case_shape_invalid')
        validate_selector(case['assembly'], case['method'])
        require(isinstance(case['id'], str) and UUID.fullmatch(case['id'])
                and isinstance(case['name'], str) and 0 < len(case['name']) <= 8192
                and not any(ord(c) < 32 for c in case['name']), 'case_invalid')
        key = case_key(case)
        require(key not in result, 'duplicate_case_identity')
        alias = case['assembly'], case['method'].casefold()
        require(alias not in selectors or selectors[alias] == case['method'], 'selector_case_alias')
        selectors[alias], result[key] = case['method'], case
    return result


def validate_manifest(manifest):
    require(isinstance(manifest, dict) and set(manifest) == {*PARTS, 'assemblies'}, 'manifest_shape_invalid')
    assemblies = manifest['assemblies']
    require(isinstance(assemblies, list) and assemblies and len(assemblies) <= 100
            and all(isinstance(a, str) and ASSEMBLY.fullmatch(a) for a in assemblies)
            and len(set(assemblies)) == len(assemblies), 'manifest_assemblies_invalid')
    populations = {part: population(manifest[part]) for part in PARTS}
    whole = populations['allTests']
    require(whole and populations['nonDocker'] and populations['native'] and populations['ordinary'],
            'population_empty')
    require(all(key[0] in assemblies for key in whole), 'case_assembly_unowned')
    require(not populations['native'].keys() & populations['ordinary'].keys()
            and populations['all'] == populations['native'] | populations['ordinary']
            and not populations['nonDocker'].keys() & populations['all'].keys()
            and whole == populations['nonDocker'] | populations['all'], 'population_union_invalid')
    selector_owners = {}
    for part in ('nonDocker', 'native', 'ordinary'):
        for case in manifest[part]:
            key = method_key(case)
            require(selector_owners.get(key, part) == part, 'selector_partition_conflict')
            selector_owners[key] = part
    return populations


def variant(attempt):
    require(isinstance(attempt, str) and re.fullmatch(r'[1-9][0-9]{0,9}', attempt), 'attempt_invalid')
    return 'A' if int(attempt) % 2 else 'B'


def selector_bytes(key):
    # Unambiguous length-prefix encoding, independent of Python hash randomization.
    return b''.join(len(part.encode()).to_bytes(4, 'big') + part.encode() for part in key)


def manifest_digest(manifest):
    return digest({'assemblies': sorted(manifest['assemblies']),
                   **{part: sorted(manifest[part], key=case_key) for part in PARTS}})


def assign(manifest, plan, attempt):
    validate_plan(plan)
    validate_manifest(manifest)
    layout = variant(attempt)
    architectures = set(plan['architectureAssemblies'])
    require(architectures <= set(manifest['assemblies']), 'architecture_project_missing')
    result = {'base': [], 'native': list(manifest['native'])}
    weights = {method_key(item): item['milliseconds'] for item in plan['weights']}
    unknown = []
    for scope in ('architecture', 'ordinary'):
        selected = (manifest['ordinary'] if scope == 'ordinary' else
                    [case for case in manifest['nonDocker'] if case['assembly'] in architectures])
        groups = defaultdict(list)
        for case in selected:
            groups[method_key(case)].append(case)
        count = plan['counts'][scope]
        require(len(groups) >= count, 'empty_shard')
        cost = {key: weights.get(key, plan['defaultMethodMs']) for key in groups}
        unknown.extend(key for key in groups if key not in weights)
        order = sorted(groups, key=(lambda key: (-cost[key], key[0].encode(), key[1].encode())) if layout == 'A' else
                       lambda key: (hashlib.sha256(plan['salt'].encode() + selector_bytes(key)).digest(),
                                    key[0].encode(), key[1].encode()))
        loads = [0] * count
        for index in range(count):
            result[f'{scope}-{index}'] = []
        for key in order:
            index = min(range(count), key=lambda i: (loads[i], i))
            result[f'{scope}-{index}'].extend(groups[key])
            loads[index] += cost[key]
    result['base'] = [case for case in manifest['nonDocker'] if case['assembly'] not in architectures]
    require(all(result.values()), 'empty_shard')
    for cases in result.values():
        cases.sort(key=case_key)
    return {'variant': layout, 'planDigest': digest(plan), 'manifestDigest': manifest_digest(manifest),
            'owners': result, 'unknownWeights': sorted(unknown)}


def filter_for(owner, assembly, cases, plan):
    population(cases)
    require(cases and all(case['assembly'] == assembly for case in cases), 'selection_assembly_invalid')
    if owner in ('base', 'native'):
        expression = FILTERS[owner]
    else:
        scope, separator, index = owner.rpartition('-')
        require(separator and scope in ('architecture', 'ordinary') and re.fullmatch(r'0|[1-9][0-9]?', index)
                and str(int(index)) == index and int(index) < plan['counts'][scope], 'owner_invalid')
        expression = '(' + '|'.join('FullyQualifiedName=' + method
            for method in sorted({case['method'] for case in cases})) + ')&(' + FILTERS[scope] + ')'
    require(len(expression.encode()) <= plan['bounds']['filterBytes'], 'filter_bound')
    return expression


def validate_argv(argv, plan):
    require(isinstance(argv, list) and argv and all(isinstance(x, str) and '\0' not in x for x in argv)
            and sum(len(x.encode()) + 1 for x in argv) <= plan['bounds']['argvBytes'], 'argv_bound')


def selected_equal(expected, actual):
    require(population(expected) == population(actual), 'selected_population_mismatch')


def validate_union(manifest, plan, attempt, provided, scope):
    expected = assign(manifest, plan, attempt)['owners']
    require(scope in ('backend', 'integration') and isinstance(provided, dict), 'aggregate_scope_invalid')
    names = {name for name in expected if
             (name == 'base' or name.startswith('architecture-')) == (scope == 'backend')}
    require(set(provided) == names, 'aggregate_owners_incomplete')
    seen = set()
    for name in sorted(names):
        selected_equal(expected[name], provided[name])
        current = set(population(provided[name]))
        require(not seen & current, 'aggregate_overlap')
        seen.update(current)
    target = set(population(manifest['nonDocker'] if scope == 'backend' else manifest['all']))
    require(seen == target, 'aggregate_union_incomplete')


def different_layouts(manifest, plan):
    first, second = (assign(manifest, plan, attempt)['owners'] for attempt in ('1', '2'))
    fixture_classes = set(plan['sharedFixtureClasses'])
    shared = {method_key(case) for case in manifest['ordinary']
              if case['method'].rsplit('.', 1)[0] in fixture_classes}
    require(shared, 'shared_fixture_witness_missing')
    for scope in ('architecture', 'ordinary', 'shared'):
        def groups(owners):
            return {frozenset(method_key(case) for case in cases
                    if scope != 'shared' or method_key(case) in shared)
                    for name, cases in owners.items() if name.startswith(
                        ('ordinary' if scope == 'shared' else scope) + '-')}
        require(groups(first) != groups(second), 'assignment_not_distinct')
    return True


def main():
    if len(sys.argv) != 2 or sys.argv[1] not in ('topology', 'aggregate-backend', 'aggregate-integration'):
        print('{"protocol":1,"error":"mode_refused"}')
        return 1
    result = None
    try:
        spec = importlib.util.spec_from_file_location('ci_shard_results', Path(__file__).with_name('ci-test-shard-results.py'))
        result = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(result)
        if sys.argv[1] == 'topology':
            result.source_topology(load_plan())
            print('{"protocol":1,"topology":"complete"}')
            return 0
        return result.aggregate(sys.argv[1].removeprefix('aggregate-'))
    except Exception as failure:
        # Known owners expose only closed refusals. An unexpected import/parser
        # or API-shape fault also fails, without exporting provider text.
        refusals = (Refusal, getattr(result, 'Refusal', Refusal),
                    getattr(getattr(result, 'OWNER', None), 'Refusal', Refusal))
        reason = failure.reason if isinstance(failure, refusals) else 'aggregate_internal_refusal'
        print(json.dumps({'protocol': 1, 'error': reason}, sort_keys=True))
        return 1


if __name__ == '__main__':
    sys.exit(main())
