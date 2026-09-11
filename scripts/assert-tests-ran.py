#!/usr/bin/env python3
"""Fail a CI test step whose filter quietly matched nothing.

Usage: assert-tests-ran.py RESULTS_DIR ASSEMBLY [ASSEMBLY ...]

`dotnet test --filter` exits 0 when the filter matches no test, so a renamed trait, a
mistyped filter or a project that dropped out of the solution turns a job green having run
nothing. Each ASSEMBLY named here (e.g. LearnStack.Tests.Unit) must have executed at least
one test in some .trx file under RESULTS_DIR. A file whose run matched nothing names no
assembly at all, so it counts for none of them — which is the failure this exists to see.
"""

import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def executed_by_assembly(results_dir):
    counts = {}
    for path in glob.glob(os.path.join(results_dir, "**", "*.trx"), recursive=True):
        root = ET.parse(path).getroot()
        counters = root.find("t:ResultSummary/t:Counters", NS)
        executed = int(counters.get("executed", "0")) if counters is not None else 0
        # Case-folded: VSTest writes `storage` lowercased — measured — so the name a
        # caller passes and the one in the file agree only once both are folded.
        assemblies = {
            os.path.splitext(os.path.basename(test.get("storage", "")))[0].casefold()
            for test in root.iterfind("t:TestDefinitions/t:UnitTest", NS)
        }
        for assembly in assemblies:
            counts[assembly] = counts.get(assembly, 0) + executed
    return counts


def main(argv):
    if len(argv) < 3:
        print(__doc__.strip().splitlines()[2], file=sys.stderr)
        return 2

    results_dir, required = argv[1], argv[2:]
    counts = executed_by_assembly(results_dir)

    for assembly, executed in sorted(counts.items()):
        print(f"{assembly}: {executed} test(s) executed")

    missing = [assembly for assembly in required if counts.get(assembly.casefold(), 0) == 0]
    for assembly in missing:
        print(f"::error::{assembly} executed no tests under {results_dir}. A filter that "
              "matches nothing exits 0, so this job would otherwise pass having run nothing.")

    return 1 if missing else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
