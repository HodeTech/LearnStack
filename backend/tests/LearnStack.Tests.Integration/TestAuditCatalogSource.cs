using LearnStack.SharedKernel.Audit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// Registers the suite's own request types as audited-nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered, not exempted.</b> Every <c>IRequest&lt;Result&lt;T&gt;&gt;</c> reaching
/// pipeline step 3 must be in the catalogue, silence included — an operation nobody
/// classified is refused with <c>audit_unclassified_operation</c>, which is the fail-closed
/// rule's first half. A test-only type is a decision to audit nothing, and
/// <c>Off&lt;T&gt;()</c> is how that decision is expressed rather than assumed
/// (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 6</see>).
/// </para>
/// <para>
/// Exempting them instead would be worse than tedious: the exemption would have to live in
/// the behavior, where it could not tell a test type from a real one that nobody
/// classified — and the rule exists precisely to catch the second.
/// </para>
/// </remarks>
internal sealed class TestAuditCatalogSource : IAuditCatalogSource
{
    /// <summary>
    /// A module segment of its own, so a suite type can never be mistaken for a shipped
    /// one — and so nothing looks for a <c>docs/modules/tests/audit.md</c>: every entry
    /// here is <c>Off</c>, and an <c>Off</c> registration contributes no catalogue entry
    /// for the matrix join to read.
    /// </summary>
    public string ModuleName => "tests";

    /// <inheritdoc />
    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .Off<TestValidationCommand>()
            .Off<CeilingGuardedQuery>()
            .Off<CeilingPublicQuery>()
            .Off<Database.ProbeQuery>()
            .Off<Database.UnresolvedProbeQuery>()
            .Off<Database.UnresolvedCustomizationProbeQuery>()
            .Off<Database.ForeignWriteCommand>();
    }
}
