using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Modules.Audit.Domain;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// What the Audit model and the schema it generates have to agree about.
/// </summary>
public sealed class AuditConventionTests
{
    /// <summary>
    /// Every closed-set column on <c>audit_log</c> stores exactly what its own
    /// <c>CHECK</c> admits, and reads back what it stored.
    /// </summary>
    [Fact]
    public void Audit_Closed_Set_Columns_Store_What_Their_Check_Admits()
    {
        // Three closed-set text columns whose value lists are written in TWO places —
        // a value converter and a CHECK constraint — and the two are rendered in
        // DIFFERENT CASES, deliberately. `outcome` is lowercase because ADR-0033 § 3 and
        // ADR-0044 § 5 both write it that way; `operation_type` and `operation_class`
        // store the C# member name unchanged, on the ck_tenants_status precedent, which
        // is what lets the admin API's ?operationType=SecurityEvent filter be the same
        // string on both sides.
        //
        // That asymmetry is exactly the shape that drifts, and it drifts silently in
        // both directions. A converter that stopped lowercasing writes rows every INSERT
        // rejects with 23514 — on the write path whose whole job is that the record
        // survives. A parse that stopped being case-insensitive throws on EVERY row the
        // Phase 03 read API materialises, because the stored form is lowercase and the
        // member is `Success`. And an enum member added without its CHECK is a value the
        // code can produce and the column cannot hold.
        //
        // Read from the MODEL rather than from the source, so the rule sees what EF will
        // actually emit — including a converter some later configuration replaces.
        // The DESIGN-TIME model, not `Context.Model`: check constraints are migration
        // metadata and the runtime's read-optimized model drops them, throwing rather
        // than returning an empty list — which is the good failure, but only if the test
        // asks the right model.
        using var context = Modules.ModelOnly<AuditDbContext>();
        var model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var entity = model.FindEntityType(typeof(AuditEntry))!;

        var checks = entity.GetCheckConstraints()
            .ToDictionary(c => c.Name!, c => c.Sql, StringComparer.Ordinal);

        checks.Should().ContainKeys(
            "ck_audit_log_outcome", "ck_audit_log_operation_type", "ck_audit_log_operation_class");

        AssertColumn<AuditOutcome>(entity, checks, nameof(AuditEntry.Outcome), "ck_audit_log_outcome");
        AssertColumn<OperationType>(entity, checks, nameof(AuditEntry.OperationType), "ck_audit_log_operation_type");
        AssertColumn<OperationClass>(entity, checks, nameof(AuditEntry.OperationClass), "ck_audit_log_operation_class");
    }

    private static void AssertColumn<TEnum>(
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity,
        Dictionary<string, string> checks,
        string propertyName,
        string constraintName)
        where TEnum : struct, Enum
    {
        var property = entity.FindProperty(propertyName)!;
        var converter = property.GetValueConverter();

        converter.Should().NotBeNull(
            "{0} is a closed-set text column and maps through a converter", propertyName);

        var stored = Enum.GetValues<TEnum>()
            .Select(member => (string)converter!.ConvertToProvider(member)!)
            .ToList();

        // The CHECK's own literals, read out of the SQL the model carries rather than
        // restated here — a second copy in this file would be the third place the list
        // lives, and the one nothing compares against the database.
        var admitted = Regex
            .Matches(checks[constraintName], "'([^']*)'")
            .Select(match => match.Groups[1].Value)
            .ToList();

        stored.Should().BeEquivalentTo(admitted,
            "every value {0}'s converter can write is one {1} admits, and every value "
            + "{1} admits is one the enum can produce", propertyName, constraintName);

        foreach (var member in Enum.GetValues<TEnum>())
        {
            var roundTripped = converter!.ConvertFromProvider(converter.ConvertToProvider(member));

            roundTripped.Should().Be(member,
                "{0} must read back what it wrote — a case-sensitive parse against a "
                + "lowercased column throws on every row", propertyName);
        }
    }
}
