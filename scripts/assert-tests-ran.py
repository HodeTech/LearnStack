#!/usr/bin/env python3
"""Fail a CI test step whose filter quietly matched nothing, or that skipped a test.

Usage: assert-tests-ran.py RESULTS_DIR ASSEMBLY [ASSEMBLY ...]

`dotnet test --filter` exits 0 when the filter matches no test, so a renamed trait, a
mistyped filter or a project that dropped out of the solution turns a job green having run
nothing. Each ASSEMBLY named here (e.g. LearnStack.Tests.Unit) must have executed at least
one test in some .trx file under RESULTS_DIR. A file whose run matched nothing names no
assembly at all, so it counts for none of them — which is the failure this exists to see.

A skipped test exits 0 the same way, and `No_Architecture_Test_Is_Skippable` cannot see the
one case it cannot catch — its own. A test that is skipped does not run, so a `Skip` on the
skip scan switches off the scan and nothing in the suite notices. The runner does: a skip is
a counter in the .trx whatever was skipped, so this refuses any run that reports one.
"""

import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def executed_by_assembly(results_dir):
    counts = {}
    skipped = {}
    for path in glob.glob(os.path.join(results_dir, "**", "*.trx"), recursive=True):
        root = ET.parse(path).getroot()
        counters = root.find("t:ResultSummary/t:Counters", NS)
        executed = int(counters.get("executed", "0")) if counters is not None else 0
        # `total` counts every case the runner knew about; `executed` counts the ones it
        # ran. A skip is the difference, and VSTest spells it in several attributes
        # depending on why — the subtraction is the one reading that covers all of them.
        total = int(counters.get("total", "0")) if counters is not None else 0
        not_run = max(0, total - executed)
        # Case-folded: VSTest writes `storage` lowercased — measured — so the name a
        # caller passes and the one in the file agree only once both are folded.
        assemblies = {
            os.path.splitext(os.path.basename(test.get("storage", "")))[0].casefold()
            for test in root.iterfind("t:TestDefinitions/t:UnitTest", NS)
        }
        for assembly in assemblies:
            counts[assembly] = counts.get(assembly, 0) + executed
            skipped[assembly] = skipped.get(assembly, 0) + not_run
    return counts, skipped


def main(argv):
    if len(argv) < 3:
        print(__doc__.strip().splitlines()[2], file=sys.stderr)
        return 2

    results_dir, required = argv[1], argv[2:]
    counts, skipped = executed_by_assembly(results_dir)

    for assembly, executed in sorted(counts.items()):
        print(f"{assembly}: {executed} test(s) executed, {skipped.get(assembly, 0)} not run")

    missing = [assembly for assembly in required if counts.get(assembly.casefold(), 0) == 0]
    for assembly in missing:
        print(f"::error::{assembly} executed no tests under {results_dir}. A filter that "
              "matches nothing exits 0, so this job would otherwise pass having run nothing.")

    # Every suite, not only the required ones: a skip anywhere is a rule nobody has to
    # satisfy (Testing Standards § Architecture tests), and the architecture suite's own
    # scan cannot report a Skip placed on the scan.
    unrun = sorted(
        (assembly, count) for assembly, count in skipped.items() if count > 0)
    for assembly, count in unrun:
        print(f"::error::{assembly} reported {count} test(s) that did not run under "
              f"{results_dir}. A skipped test exits 0, so this run would otherwise pass "
              "with a rule switched off.")

    return 1 if missing or unrun else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
