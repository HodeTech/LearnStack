namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Decides whether the caller may enter the platform-admin scope at all.
/// </summary>
/// <remarks>
/// A separate port, and not a check inlined into <see cref="IPlatformAdminScope"/>,
/// because
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § The platform-admin override is not a resolution source</see> requires the
/// permission to be "checked before the scope opens" — and with no principal anywhere in
/// the process, a collaborator is the only way to make that a real call rather than a
/// comment. Phase 03 replaces the shipped implementation with one that reads the actor's
/// platform-scope permission.
/// </remarks>
public interface IPlatformAdminGate
{
    /// <summary>Whether entry is permitted, for the stated reason.</summary>
    ValueTask<bool> IsPermittedAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>The gate that permits nobody.</summary>
/// <remarks>
/// <para>
/// <b>This is correct, and it will look like a bug</b> — the same shape and the same
/// argument as <see cref="DenyAllTenantMembershipReader"/>. There is no authenticated
/// principal until Phase 02b and no permission to hold until Phase 03, so "nobody holds
/// a platform-scope permission" is the true answer rather than a placeholder, and a gate
/// that were permissive in Development would reproduce exactly the configuration
/// inversion the composition root argues against elsewhere: the demo passes and
/// production refuses.
/// </para>
/// <para>
/// <b>Nothing can reach it in Packet 7</b> — no production caller enters the scope. The
/// consequence worth naming ahead of time is Packet 9's: its GDPR redaction handler is
/// the first real caller, and it inherits a closed gate, so Packet 9 either lands a gate
/// implementation of its own or waits for Phase 03.
/// </para>
/// </remarks>
public sealed class DenyAllPlatformAdminGate : IPlatformAdminGate
{
    /// <inheritdoc />
    public ValueTask<bool> IsPermittedAsync(
        string reason, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}

/// <summary>Thrown when <see cref="IPlatformAdminGate"/> refuses entry.</summary>
/// <remarks>
/// An exception rather than a <c>Result</c>: a caller that asked for a cross-tenant
/// bypass and was refused has no second path to take, and there is no request pipeline
/// here to render a failure into. It carries no tenant, no actor and no connection
/// detail — only that entry was refused and why the caller said it wanted in.
/// </remarks>
public sealed class PlatformAdminScopeDeniedException : Exception
{
    public PlatformAdminScopeDeniedException(string reason)
        : base($"Platform-admin scope entry was refused for reason '{reason}'. "
            + "No principal in this deployment holds a platform-scope permission: "
            + "authentication arrives in Phase 02b and the permission itself in Phase 03.") =>
        Reason = reason;

    public PlatformAdminScopeDeniedException()
        : base("Platform-admin scope entry was refused.")
    {
    }

    public PlatformAdminScopeDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The reason the caller gave. Never a connection detail.</summary>
    public string? Reason { get; }
}
