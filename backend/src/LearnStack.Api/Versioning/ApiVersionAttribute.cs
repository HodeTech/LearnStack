namespace LearnStack.Api.Versioning;

/// <summary>
/// Declares the API major version a controller belongs to. The
/// <see cref="VersionedRouteConvention"/> turns it into the
/// <c>/api/v{N}/…</c> route prefix that
/// <see href="../../../../docs/decisions/0024-api-versioning-policy.md">ADR-0024</see>
/// fixes as the only canonical public route shape.
/// </summary>
/// <remarks>
/// <para>
/// Absent this attribute a controller is <see cref="DefaultMajor"/>. That is
/// deliberate: ADR-0024 says mainline development continues in <c>/api/v1</c>
/// and a new major appears only when a breaking change is unavoidable, so the
/// common case should need no ceremony. What must never happen is a controller
/// landing outside a versioned prefix by accident, and
/// <c>Every_Endpoint_Is_Under_Versioned_Route</c> is what prevents that.
/// </para>
/// <para>
/// This is <b>not</b> the <c>Asp.Versioning</c> library's attribute of the same
/// short name. ADR-0024 § Implementation Notes wires the URL convention through
/// ASP.NET Core's own application-model conventions; the library would add a
/// media-type and query-string negotiation surface the ADR explicitly does not
/// use ("Header-based versioning is not used").
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ApiVersionAttribute : Attribute
{
    /// <summary>The major version applied to controllers that declare none.</summary>
    public const int DefaultMajor = 1;

    public ApiVersionAttribute(int major)
    {
        // A zero or negative major would produce `/api/v0/…`, which ADR-0024
        // § What we explicitly punted on rules out by name: "No /api/v0/*
        // endpoints exist or will exist; /api/v1 is the first contract."
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 1);
        Major = major;
    }

    public int Major { get; }
}
