; Unshipped analyzer release.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------
LearnStackException-DomainExceptionThrow | Design | Warning | ADR-0032 § Sub-decision 4 — DomainException is reserved for programmer errors; expected business-rule violations return Result.Fail(business_rule_violation, ...). Severity escalates to Error after Phase 03 exit.
