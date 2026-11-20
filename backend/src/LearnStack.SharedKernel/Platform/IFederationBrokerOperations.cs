namespace LearnStack.SharedKernel.Platform;

/// <summary>One broker that needs an operator's attention: its identity, why, and what the provider shows.</summary>
/// <param name="Alias">The derived alias, which is LearnStack's and no tenant's text.</param>
/// <param name="TenantId">The tenant the owner marker names.</param>
/// <param name="ConnectionId">The connection the owner marker names.</param>
/// <param name="Classes">Any of <c>orphan</c>, <c>unreadable_owned</c> and <c>nonconforming</c>; never empty.</param>
/// <param name="RowState">
/// <c>absent</c> when no row names the connection, <c>deleted</c> for a tombstone, otherwise the live row's lifecycle state.
/// </param>
/// <param name="Protocol"><c>oidc</c> or <c>saml</c> for a readable broker, null otherwise.</param>
/// <param name="Enabled">Whether the provider offers the broker as a sign-in option.</param>
/// <param name="Mappers">The mappers on a readable broker with a live row; null when it was not asked or could not be read.</param>
public sealed record FederationBrokerReportItem(
    string Alias, Guid TenantId, Guid ConnectionId, IReadOnlyList<string> Classes, string RowState, string? Protocol,
    bool Enabled, int? Mappers);

/// <summary>How a report page ended.</summary>
public enum FederationReportOutcome
{
    /// <summary>The page was read.</summary>
    Ok = 1,

    /// <summary>The cursor is not one this operation issued; nothing was entered.</summary>
    InvalidCursor,

    /// <summary>The provider could not answer, so nothing is reported.</summary>
    Unavailable,
}

/// <summary>One page of the report.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Items">The brokers on the page that have at least one class, in the provider's order.</param>
/// <param name="NextCursor">The opaque position of the next page, or null after the last.</param>
/// <param name="Scanned">How many instances the provider returned for the page.</param>
/// <param name="Ignored">How many of them are not LearnStack's: no marker, a foreign marker or a marker that does not match its row.</param>
/// <param name="Unread">
/// How many brokers that read as healthy and live had no mapper count, because the provider failed or the page's time ran
/// out: an extra mapper is drift only a mapper read shows, so these are unknown and not clean, and the next page of the
/// same size may read them.
/// </param>
public sealed record FederationReportResult(
    FederationReportOutcome Outcome, IReadOnlyList<FederationBrokerReportItem> Items, string? NextCursor, int Scanned, int Ignored,
    int Unread = 0)
{
    public static FederationReportResult Of(FederationReportOutcome outcome) => new(outcome, [], null, 0, 0);
}

/// <summary>How a remove or a disable ended.</summary>
public enum FederationActOutcome
{
    /// <summary>The provider's change was made and confirmed; the account review is owed.</summary>
    Applied = 1,

    /// <summary>The broker was already absent (remove) or already disabled (disable); nothing was sent and no tenant row was written.</summary>
    AlreadyApplied,

    /// <summary>A disable named an alias no instance has.</summary>
    NotFound,

    /// <summary>The instance's marker does not name a tenant and a connection of LearnStack's, or its row names another tenant.</summary>
    NotOwned,

    /// <summary>
    /// A live connection row names a broker that reads, so a remove refuses; the tenant's own Delete retires the pair. An
    /// unreadable owned broker is removed even with a live row (ADR-0099 Amendment 12).
    /// </summary>
    LiveConnection,

    /// <summary>A disable found a healthy owned broker with a live row: nothing about it needs an operator.</summary>
    NotApplicable,

    /// <summary>The provider definitively refused the change; nothing changed.</summary>
    Refused,

    /// <summary>The provider could not be reached before any change was sent; nothing changed.</summary>
    Unavailable,

    /// <summary>A change was sent and its outcome is unknown: the next report shows the broker present or gone.</summary>
    Uncertain,
}

/// <summary>The end of an operator remove or disable.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="TenantId">The tenant the marker names, when an owned broker was found.</param>
/// <param name="ConnectionId">The connection the marker names, when an owned broker was found.</param>
public sealed record FederationActResult(FederationActOutcome Outcome, Guid? TenantId = null, Guid? ConnectionId = null);

/// <summary>
/// The confined owner of the operator's federation report, remove and disable (ADR-0099 Amendment 10 and 11): it
/// consumes the operator's admission in the platform scope, reads the provider through
/// <see cref="LearnStack.SharedKernel.Identity.IFederationOperator"/> and the connection rows through the scope's
/// column grants, and writes an intent row and a completion row under the real tenant for each act.
/// </summary>
public interface IFederationBrokerOperations
{
    /// <summary>Reports one page of the brokers that are orphaned, unreadable or nonconforming; the realm is read first, then the rows.</summary>
    /// <exception cref="LearnStack.SharedKernel.Audit.AuditWriteFailedException">The scope's entry row could not be written; nothing was read.</exception>
    Task<FederationReportResult> ReportAsync(
        PlatformAdmission admission, string? cursor, int limit, CancellationToken cancellationToken = default);

    /// <summary>Removes an orphaned or unreadable owned broker after an intent row, and records the completion.</summary>
    /// <exception cref="LearnStack.SharedKernel.Audit.AuditWriteFailedException">A MUST row could not be written; the effect did not start or its outcome is recorded by the intent row alone.</exception>
    Task<FederationActResult> RemoveAsync(PlatformAdmission admission, string brokerAlias, CancellationToken cancellationToken = default);

    /// <summary>Disables a nonconforming or unreachable owned broker at the provider only, with the same two rows.</summary>
    /// <exception cref="LearnStack.SharedKernel.Audit.AuditWriteFailedException">A MUST row could not be written; the effect did not start or its outcome is recorded by the intent row alone.</exception>
    Task<FederationActResult> DisableAsync(PlatformAdmission admission, string brokerAlias, CancellationToken cancellationToken = default);
}
