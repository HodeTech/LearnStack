; Unshipped analyzer release.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------------
LS0001 | Design | Warning | Rule "LearnStackException-DomainExceptionThrow" (ADR-0032 § Sub-decision 4 + Amendment 1). DomainException is reserved for programmer errors; expected business-rule violations return Result.Fail(business_rule_violation, ...). Roslyn diagnostic ids must be valid identifiers, so the wire id is LS0001; the hyphenated string is the human-readable rule name. Severity escalates to Error after Phase 03 exit.
