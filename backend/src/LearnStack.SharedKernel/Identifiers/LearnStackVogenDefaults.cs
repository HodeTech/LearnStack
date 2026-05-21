using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// Canonical <c>Conversions</c> mask for every LearnStack-declared
/// <c>[ValueObject&lt;T&gt;]</c> per ADR-0023. Using the constant keeps the
/// annotation uniform across modules: callers write
/// <c>[ValueObject&lt;Guid&gt;(LearnStackVogenDefaults.IdMask)]</c> rather
/// than hand-picking the flag set.
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
