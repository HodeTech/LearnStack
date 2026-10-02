using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Settings;

/// <summary>Typed registered settings; uncached and scoped to the trusted ambient context.</summary>
public interface ITenantSettingsAccessor
{
    Task<Result<TenantSettingRead<T>>> ReadAsync<T>(
        TenantSettingKey<T> key, CancellationToken cancellationToken = default) where T : class;
}

/// <summary>A typed lookup token, admitted only by the server's registration.</summary>
public sealed record TenantSettingKey<T>(string Value) where T : class;

/// <summary>An absent setting is a successful empty value, never raw configuration.</summary>
public sealed record TenantSettingRead<T>(T? Value) where T : class
{
    public bool IsPresent => Value is not null;
}

public sealed record BrandingTheme(string Primary, string Background, string Foreground, string Muted);

public static class TenantSettingKeys
{
    public static TenantSettingKey<BrandingTheme> BrandingTheme { get; } = new("branding.theme");
}
