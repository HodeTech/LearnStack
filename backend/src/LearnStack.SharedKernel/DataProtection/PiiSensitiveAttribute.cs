namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// Marks a property whose value must never reach an audit snapshot in the clear.
/// </summary>
/// <remarks>
/// <para>
/// Honoured by <c>AuditChangeTrackerInterceptor</c> <b>inside the capture</b>, before
/// anything reaches <c>IAuditStateCapture</c> — so the value is not stored and then
/// scrubbed, it is never stored. The property is <b>not dropped</b>: its value is
/// replaced with <c>SensitiveTokenCatalog.RedactedValue</c>, so the diff still records
/// <em>that</em> it changed, which is the fact a reviewer is usually asking about.
/// </para>
/// <para>
/// <b>Property-granular, and that includes a <c>jsonb</c> property.</b> The whole value
/// is replaced; the marker does not descend into a document looking for personal data
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 4 § 1</see>). Descending is the shape that appears to work: it would redact the
/// fields a reviewer thought of and leave the ones a tenant invented, in a column whose
/// shape is by construction unknown to us. A module that needs a finer grain models the
/// sensitive part as its own property, where the schema is.
/// </para>
/// <para>
/// The name-token list in <c>SensitiveTokenCatalog</c> runs beside this attribute rather
/// than instead of it: the list catches what nobody marked, the marker catches what the
/// list's tokens do not name.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class PiiSensitiveAttribute : Attribute;
