using System.Collections.Frozen;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Settings;

public interface ITenantSettingRegistration
{
    string Key { get; }
    Type ValueType { get; }
    bool AllowsOrganizationScope { get; }
}

/// <summary>Server-owned scope and grammar; a lookup caller cannot supply these.</summary>
public sealed record TenantSettingRegistration<T>(
    TenantSettingKey<T> Token, bool AllowsOrganizationScope, Func<string, Result<T>> Parse)
    : ITenantSettingRegistration where T : class
{
    public string Key => Token.Value;
    public Type ValueType => typeof(T);
}

public sealed class TenantSettingRegistry
{
    private readonly FrozenDictionary<string, ITenantSettingRegistration> _registrations;

    public TenantSettingRegistry(IEnumerable<ITenantSettingRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _registrations = registrations.ToFrozenDictionary(row => row.Key, StringComparer.Ordinal);
    }

    public static TenantSettingRegistry Default { get; } = new(
    [
        new TenantSettingRegistration<BrandingTheme>(TenantSettingKeys.BrandingTheme, false,
            BrandingThemeRegistry.Read),
    ]);

    public TenantSettingRegistration<T>? Find<T>(TenantSettingKey<T> key) where T : class =>
        _registrations.TryGetValue(key.Value, out var registration)
            ? registration as TenantSettingRegistration<T> : null;
}
