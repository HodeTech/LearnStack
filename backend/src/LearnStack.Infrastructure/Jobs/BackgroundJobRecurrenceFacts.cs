using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Jobs;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Frozen manifest values, including historical revisions; these facts grant no execution authority.</summary>
internal sealed record BackgroundJobRecurrenceFacts(string Key, long Revision, DateTimeOffset ActivatesAt,
    string Target, BackgroundJobRecurrenceSchedule Schedule, DateTimeOffset Anchor, TimeSpan Period,
    TimeSpan Jitter, BackgroundJobRecipientPolicy RecipientPolicy, BackgroundJobMisfirePolicy MisfirePolicy,
    BackgroundJobScope Scope)
{
    internal string Fingerprint()
    {
        // Preserve the original Step 1 canonical representation byte for byte.
        var canonical = string.Join('\n', Key, Revision.ToString(CultureInfo.InvariantCulture),
            ActivatesAt.UtcTicks.ToString(CultureInfo.InvariantCulture), Target,
            ((int)Schedule).ToString(CultureInfo.InvariantCulture), Anchor.UtcTicks.ToString(CultureInfo.InvariantCulture),
            Period.Ticks.ToString(CultureInfo.InvariantCulture), Jitter.Ticks.ToString(CultureInfo.InvariantCulture),
            ((int)RecipientPolicy).ToString(CultureInfo.InvariantCulture),
            ((int)MisfirePolicy).ToString(CultureInfo.InvariantCulture), ((int)Scope).ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    internal string StoredSchedule => Schedule switch
    {
        BackgroundJobRecurrenceSchedule.Interval => "interval",
        BackgroundJobRecurrenceSchedule.Daily => "daily",
        _ => throw new InvalidOperationException("A recurrence requires a closed UTC schedule."),
    };

    internal string StoredRecipientPolicy => RecipientPolicy switch
    {
        BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants => "provisioned_non_archived_tenants",
        BackgroundJobRecipientPolicy.ServingTenants => "serving_tenants",
        BackgroundJobRecipientPolicy.ServingOrganizations => "serving_organizations",
        _ => throw new InvalidOperationException("A recurrence requires a closed recipient policy."),
    };

    internal static BackgroundJobRecurrenceFacts? Read(NpgsqlDataReader row)
    {
        var policy = row.GetString(9) switch
        {
            "provisioned_non_archived_tenants" => BackgroundJobRecipientPolicy.ProvisionedNonArchivedTenants,
            "serving_tenants" => BackgroundJobRecipientPolicy.ServingTenants,
            "serving_organizations" => BackgroundJobRecipientPolicy.ServingOrganizations,
            _ => throw new InvalidOperationException("The stored recurrence recipient policy is invalid."),
        };
        var schedule = row.GetString(4) switch
        {
            "interval" => BackgroundJobRecurrenceSchedule.Interval,
            "daily" => BackgroundJobRecurrenceSchedule.Daily,
            _ => throw new InvalidOperationException("The stored recurrence schedule is invalid."),
        };
        var activation = ReadTimestamp(row, 5);
        var anchor = ReadTimestamp(row, 6);
        if (activation is null || anchor is null) return null;
        return new(row.GetString(0), row.GetInt64(1), activation.Value, row.GetString(3), schedule,
            anchor.Value, TimeSpan.FromTicks(checked(row.GetInt64(7) * TimeSpan.TicksPerMicrosecond)),
            TimeSpan.FromTicks(checked(row.GetInt64(8) * TimeSpan.TicksPerMicrosecond)), policy, BackgroundJobMisfirePolicy.CoalesceLatest,
            policy == BackgroundJobRecipientPolicy.ServingOrganizations ? BackgroundJobScope.Organization : BackgroundJobScope.TenantWide);
    }

    // PostgreSQL's finite timestamp range exceeds DateTimeOffset's range.
    // Absent decoded facts grant no authority; the caller retains the locked
    // locator and quarantines that row after closing its actual reader.
    internal static DateTimeOffset? ReadTimestamp(NpgsqlDataReader row, int ordinal)
    {
        try { return row.GetFieldValue<DateTimeOffset>(ordinal); }
        catch (InvalidCastException) { return null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
