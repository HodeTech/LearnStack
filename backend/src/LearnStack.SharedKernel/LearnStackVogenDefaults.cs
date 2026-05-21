using Vogen;

namespace LearnStack.SharedKernel;

/// <summary>
/// Canonical <c>Conversions</c> mask for every LearnStack-declared
/// <c>[ValueObject&lt;T&gt;]</c> per ADR-0023. Lives at the SharedKernel
/// root namespace because the mask covers both aggregate-root IDs
/// (e.g. <c>TenantId</c>, <c>UserId</c>) and richer value objects
/// (<c>Email</c>, <c>Slug</c>, <c>LocaleCode</c>, <c>Money</c>) — neither
/// surface owns the constant exclusively.
/// </summary>
public static class LearnStackVogenDefaults
{
    /// <summary>
    /// Conversion set every aggregate-root ID and cross-cutting value object
    /// opts into: EF Core value converter, System.Text.Json converter, and
    /// the TypeConverter (which carries ASP.NET Core minimal-API and MVC
    /// route-parameter binding — Vogen does not expose a separate
    /// <c>AspNetCoreRouteParameter</c> flag).
    /// </summary>
    public const Conversions IdMask =
        Conversions.EfCoreValueConverter
        | Conversions.SystemTextJson
        | Conversions.TypeConverter;
}
