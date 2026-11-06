using System.Globalization;
using System.Text.Json;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Settings;

/// <summary>One tenant-wide integer policy; no generic settings editor or organization override.</summary>
public static class InvitationLifetimePolicyRegistry
{
    public static Result<InvitationLifetimePolicy> Read(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 256) return Invalid();
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Invalid();
            var properties = document.RootElement.EnumerateObject().ToArray();
            return properties.Length == 1 && properties[0].NameEquals("days")
                && properties[0].Value.ValueKind == JsonValueKind.Number && properties[0].Value.TryGetInt32(out var days)
                && IsValid(days) ? Result.Ok(new InvitationLifetimePolicy(days)) : Invalid();
        }
        catch (JsonException) { return Invalid(); }
    }

    public static Result<string> Canonicalize(int days) => IsValid(days)
        ? Result.Ok("{\"days\":" + days.ToString(CultureInfo.InvariantCulture) + "}")
        : TenantWriteFailures.Field<string>("lockey_validation_failed", "Days", "lockey_invalid_value");

    private static bool IsValid(int days) => days is >= InvitationLifetimePolicy.MinimumDays and <= InvitationLifetimePolicy.MaximumDays;
    private static Result<InvitationLifetimePolicy> Invalid() => TenantWriteFailures.Field<InvitationLifetimePolicy>(
        "lockey_validation_failed", "Days", "lockey_invalid_value");
}
