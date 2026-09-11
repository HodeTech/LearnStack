using System.Collections.Frozen;

namespace LearnStack.SharedKernel.Entitlements;

/// <summary>
/// Every feature key the platform knows, with its descriptor.
/// </summary>
/// <remarks>
/// <para>
/// <b>The spellings are the Hub's, verbatim.</b> Measured at Packet 9: the intersection
/// between the keys LearnStack's documents named and the keys the Hub sends was empty for
/// limits and near-total for features, and the Hub has merged code, a plan editor and two
/// validators built on its spelling while this side had a declaration and no implementing
/// line. A key the Hub never sends misses on every real projection and falls through to
/// its catalog default — a paid tenant silently reading as unentitled, which is the
/// failure mode hardest to see from inside
/// (<see href="../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
/// Amendment 1 § 1</see>).
/// </para>
/// <para>
/// <b>Every key is declared before anything gates on it, and that is deliberate rather
/// than speculative.</b> ADR-0045 § 6 forbids inventing keys, and no gate ships here —
/// Phase 02c owns the enforcement path. What ships is the vocabulary, because the
/// SPELLING is the one-way door (it lands in the Hub's validators, in persisted jsonb and
/// in a pinned wire schema) while MEMBERSHIP has a written exit: Feature Flags § Removing
/// a key already fixes a deprecation cycle.
/// </para>
/// </remarks>
public static class FeatureKeys
{
    // ── Plan-level, projected from the Hub ────────────────────────────────────
    //
    // The degraded posture on each is ADR-0034's requirement that every feature key class
    // declare fail-open or fail-closed explicitly, resolved against the table in
    // architecture/26 § Failure policy by key class, which has a row for every key below. A
    // key added here before its row exists answers FailClosed: an unknown answer must not
    // open a surface, and a posture guessed by an implementer is a security default nobody
    // re-reads.

    /// <summary>Recording a live class. Gated by the one killswitch a descriptor names.</summary>
    public static readonly FeatureKey ClassroomRecording = new("classroom.recording");

    /// <summary>Breakout rooms within a live class.</summary>
    public static readonly FeatureKey ClassroomBreakoutRooms = new("classroom.breakout_rooms");

    /// <summary>Serving a tenant on its own domain.</summary>
    public static readonly FeatureKey CustomDomain = new("tenancy.custom_domain");

    /// <summary>Replacing LearnStack's branding with the tenant's.</summary>
    public static readonly FeatureKey WhiteLabelBranding = new("tenancy.white_label_branding");

    /// <summary>Authoring content types beyond the plan's ceiling.</summary>
    public static readonly FeatureKey UnlimitedContentTypes = new("customization.unlimited_content_types");

    /// <summary>SAML single sign-on.</summary>
    public static readonly FeatureKey SsoSaml = new("identity.sso.saml");

    /// <summary>OIDC single sign-on.</summary>
    public static readonly FeatureKey SsoOidc = new("identity.sso.oidc");

    /// <summary>SCIM user provisioning.</summary>
    public static readonly FeatureKey Scim = new("identity.scim");

    /// <summary>Reporting beyond the built-in dashboards.</summary>
    public static readonly FeatureKey AdvancedReporting = new("analytics.advanced_reporting");

    /// <summary>Bulk import through the admin surface.</summary>
    public static readonly FeatureKey BulkImport = new("admin.bulk_import");

    /// <summary>The public API.</summary>
    public static readonly FeatureKey ApiAccess = new("integrations.api_access");

    /// <summary>Outbound webhooks.</summary>
    public static readonly FeatureKey Webhooks = new("integrations.webhooks");

    /// <summary>Exporting the audit log.</summary>
    public static readonly FeatureKey AuditExport = new("audit.export");

    /// <summary>Choosing where the tenant's data is stored.</summary>
    public static readonly FeatureKey DataResidencySelection = new("compliance.data_residency");

    // ── Tenant-flag level: experiments, rollouts, opt-ins ─────────────────────
    //
    // Never anything a plan is sold on. These are the two LearnStack declares on its own
    // side, and they are the only keys here the Hub does not carry.

    /// <summary>The second-generation lesson player, behind a per-tenant opt-in.</summary>
    public static readonly FeatureKey LessonPlayerV2 = new("learning.lesson_player.v2");

    /// <summary>Pronunciation feedback, behind a per-tenant opt-in.</summary>
    public static readonly FeatureKey AiPronunciationFeedback = new("ai.pronunciation_feedback");

    /// <summary>Every descriptor, by key.</summary>
    /// <remarks>
    /// Frozen and built once: this is read on a resolution path and never written.
    /// </remarks>
    public static readonly FrozenDictionary<FeatureKey, FeatureDescriptor> All =
        new FeatureDescriptor[]
        {
            // Product capabilities: losing one mid-use is the worse failure, so the last
            // known value stands — but a COLD start is still closed, because the platform
            // must not invent an entitlement it has never seen.
            new(ClassroomRecording, FeatureSource.PlanProjected, false,
                DegradedPosture.FailOpenToLastKnown, KillswitchKeys.RecordingEnabled),
            new(ClassroomBreakoutRooms, FeatureSource.PlanProjected, false,
                DegradedPosture.FailOpenToLastKnown),
            new(CustomDomain, FeatureSource.PlanProjected, false,
                DegradedPosture.FailOpenToLastKnown),
            new(AdvancedReporting, FeatureSource.PlanProjected, false,
                DegradedPosture.FailOpenToLastKnown),

            // Security surfaces: an unknown answer must not open an export or an API.
            new(SsoSaml, FeatureSource.PlanProjected, false, DegradedPosture.FailClosed),
            new(SsoOidc, FeatureSource.PlanProjected, false, DegradedPosture.FailClosed),
            new(Scim, FeatureSource.PlanProjected, false, DegradedPosture.FailClosed),
            new(AuditExport, FeatureSource.PlanProjected, false, DegradedPosture.FailClosed),
            new(ApiAccess, FeatureSource.PlanProjected, false, DegradedPosture.FailClosed),
            new(Webhooks, FeatureSource.PlanProjected, false, DegradedPosture.FailClosed),
            new(BulkImport, FeatureSource.PlanProjected, false, DegradedPosture.FailClosed),

            // A compliance cap: a wrong answer here is a regulatory finding.
            new(DataResidencySelection, FeatureSource.PlanProjected, false,
                DegradedPosture.FailClosed),

            // The table's "everything else plan-projected" row: neither a security surface
            // nor a live-session capability. Closed, because the cost of being wrong is a
            // tenant seeing branding or authoring headroom it has not bought, and the cost
            // of being right late is a banner.
            new(WhiteLabelBranding, FeatureSource.PlanProjected, false,
                DegradedPosture.FailClosed),
            new(UnlimitedContentTypes, FeatureSource.PlanProjected, false,
                DegradedPosture.FailClosed),

            // Tenant flags. Their default is already false and their source is a table in
            // this deployment, so "unreachable past the grace window" is a database
            // outage — under which an experiment must be off, not on.
            new(LessonPlayerV2, FeatureSource.TenantFlag, false, DegradedPosture.FailClosed),
            new(AiPronunciationFeedback, FeatureSource.TenantFlag, false,
                DegradedPosture.FailClosed),
        }.ToFrozenDictionary(descriptor => descriptor.Key);
}
