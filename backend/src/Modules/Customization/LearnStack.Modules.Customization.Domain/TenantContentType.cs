using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Customization.Domain;

/// <summary>
/// A content shape a tenant declares as data: a JSON Schema plus the composite
/// renderer that draws it.
/// </summary>
/// <remarks>
/// <para>
/// The first half of what
/// <see href="../../../../../docs/decisions/0018-tenant-driven-customization-model.md">ADR-0018</see>
/// means by "the difference lives in their database rows". A language school's
/// <c>vocabulary-card</c> and a yoga studio's <c>asana-pose</c> are two rows in
/// this table, not two types in an assembly, and
/// <see href="../../../../../docs/roadmap/phase-02d-walking-skeleton.md">Phase 02d</see>
/// renders both from the same code path.
/// </para>
/// <para>
/// <b>The schema is text here and validated elsewhere.</b> This aggregate refuses
/// a blank or malformed-JSON body, and it does <i>not</i> know what a valid draft
/// 2020-12 document is: that is
/// <see href="../../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043</see>'s
/// four gates, which live behind <c>IJsonSchemaValidator</c> and run in the
/// handler before this factory is reached. A domain layer that took the port
/// would put a service inside an aggregate and a library behind a
/// <c>Domain</c> reference; a domain layer that re-implemented the gates would be
/// a second validator disagreeing with the first.
/// </para>
/// </remarks>
[TenantOwned]
public sealed class TenantContentType
    : CustomizationDefinition<TenantContentTypeId>, IAggregateRoot<TenantContentTypeId>
{
    private TenantContentType(TenantContentTypeId id)
        : base(id)
    {
        JsonSchema = null!;
        RendererKey = null!;
    }

    // EF materialization.
    private TenantContentType()
    {
        JsonSchema = null!;
        RendererKey = null!;
    }

    /// <summary>The draft 2020-12 document, as authored, already gated.</summary>
    public string JsonSchema { get; private set; }

    /// <summary>A key from the closed set in <see cref="CompositeRendererKey"/>.</summary>
    public string RendererKey { get; private set; }

    public static TenantContentType Create(
        TenantContentTypeId id,
        TenantId tenantId,
        string key,
        int schemaVersion,
        LocalizedText displayName,
        string jsonSchema,
        string rendererKey,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);

        // Everything the aggregate can refuse is refused before anything is
        // assigned, so a rejected call leaves no half-built aggregate behind.
        if (!id.IsInitialized() || id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The identifier was never assigned; construct it through its factory.",
                nameof(id));
        }

        JsonValue.EnsureWellFormed(jsonSchema, nameof(jsonSchema));
        CompositeRendererKey.EnsureKnown(rendererKey, nameof(rendererKey));

        var contentType = new TenantContentType(id)
        {
            JsonSchema = jsonSchema,
            RendererKey = rendererKey,
        };

        contentType.InitializeDefinition(tenantId, key, schemaVersion, displayName);
        contentType.MarkCreated(clock.UtcNow, createdBy);
        return contentType;
    }

    /// <summary>
    /// Replaces the schema with a strictly additive revision of it.
    /// </summary>
    /// <remarks>
    /// <b>The additive claim is not checked here.</b> Deciding that a submitted
    /// document only adds — an optional field, a widened enum, a new facet — is a
    /// diff against the current revision, and
    /// <see href="../../../../../docs/roadmap/phase-04-cms-media-pages.md">Phase 04</see>
    /// gives that diff to the editor: "on save, the editor diffs the submitted
    /// schema against the current revision and refuses an additive claim that
    /// removes or narrows anything". This aggregate enforces the half a diff
    /// cannot: that the body is still a draft, so no stored instance has been
    /// validated against it yet.
    /// </remarks>
    public void ReviseSchema(string jsonSchema, IClock clock, UserId by)
    {
        ArgumentNullException.ThrowIfNull(clock);
        JsonValue.EnsureWellFormed(jsonSchema, nameof(jsonSchema));
        EnsureBodyMutable();

        MarkUpdated(clock.UtcNow, by);
        JsonSchema = jsonSchema;
        RaiseRevision();
    }

    /// <summary>
    /// Points the row at a different composite renderer.
    /// </summary>
    /// <remarks>
    /// Presentation metadata, so it stays mutable after publish — the renderer
    /// decides how an instance is drawn, never whether it is valid, so changing it
    /// cannot invalidate a stored row.
    /// </remarks>
    public void SetRendererKey(string rendererKey, IClock clock, UserId by)
    {
        ArgumentNullException.ThrowIfNull(clock);
        CompositeRendererKey.EnsureKnown(rendererKey, nameof(rendererKey));

        MarkUpdated(clock.UtcNow, by);
        RendererKey = rendererKey;
    }
}
