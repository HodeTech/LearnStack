using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnStack.Tools.Seeder;

/// <summary>
/// Writes the two demo tenants by sending the same commands a request would.
/// </summary>
/// <remarks>
/// <para>
/// <b>It sends commands; it does not write rows.</b>
/// [ADR-0042](../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md)
/// requires it: a seeder that inserted the tenant and its default organization itself
/// would be a second copy of the one sanctioned cross-aggregate write, and the allow-list
/// that keeps that exception at one entry would no longer describe the system. Sending the
/// command also means the seed exercises the pipeline production uses — validation, the
/// transaction, the announcement — so a seed that succeeds is evidence about the request
/// path and not only about the schema.
/// </para>
/// <para>
/// <b>Each tenant is seeded in two acts, under two different contexts.</b> Provisioning
/// runs <i>unresolved</i>, because the tenant it announces does not exist until it
/// commits. Everything after runs <i>as that tenant</i>: the second organization and the
/// host row are ordinary tenant-owned writes, and their policies check the row against the
/// announcement. Setting the accessor is how a non-request execution says which tenant it
/// is acting for — the same thing a background job does.
/// </para>
/// <para>
/// <b>It never writes <c>ITenantContextAccessor.Current</c>.</b> Writes to that member
/// are a closed, enumerated set of four
/// ([ADR-0036 Amendment 2](../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md)),
/// because a writer of it can make work run under a tenant nothing resolved. A seeder
/// legitimately does that — it is the same shape as the Hangfire job activator — but
/// widening a security enumeration for a development tool is the wrong trade when the
/// alternative costs nothing: each act composes a scope around a
/// <c>StaticTenantContextAccessor</c> holding its context, so the seeder <i>constructs</i>
/// a context per unit of work instead of <i>mutating</i> an ambient one, and cannot move
/// the ambient tenant at all.
/// </para>
/// <para>
/// <b>Idempotent by conflict, not by pre-check.</b> Re-running is expected — <c>make seed</c>
/// is documented as safe to repeat — and a "does it exist already?" query cannot be asked:
/// under the provisioning announcement a <c>SELECT</c> over <c>tenants</c> returns no rows
/// by policy. So the seeder writes and treats a uniqueness refusal as "already seeded",
/// which is the same answer with one fewer round trip and no race.
/// </para>
/// <para>
/// <b>A <i>uniqueness</i> refusal, read from the field-level reason — not from the
/// top-level code.</b> <c>business_rule_violation</c> was a safe proxy while provisioning
/// was the only command: every cause of it really was "this row exists". It stopped being
/// one when <c>MapHostToTenantCommand</c> landed, which returns the same top-level code
/// for a host already taken, an organization that is not this tenant's, and a host the
/// deployment reserved. Only the first is "already seeded". Measured: with the proxy in
/// place, a wrong organization id in <c>SeedData</c> — a plausible copy-paste between two
/// tenants declared side by side — made the seeder log "already present", exit 0, and
/// never write the row that decides whose data an anonymous request sees.
/// </para>
/// </remarks>
public sealed class SeedRunner(
    Func<ITenantContext?, ServiceProvider> compose, ILogger<SeedRunner> logger)
{
    /// <summary>Seeds <paramref name="tenants"/>, defaulting to the two demo tenants.</summary>
    /// <remarks>
    /// The list is a parameter rather than a direct read of <see cref="SeedData.All"/> so
    /// the classification below can be driven with data that fails for a reason other
    /// than uniqueness. Without it the only way to reach that branch was to edit the
    /// shipped seed, and the branch went untested — which is how the masking defect it
    /// now guards against survived a review round.
    /// </remarks>
    public async Task<int> RunAsync(
        CancellationToken cancellationToken, IReadOnlyList<SeedTenant>? tenants = null)
    {
        foreach (var tenant in tenants ?? SeedData.All)
        {
            await SeedTenantAsync(tenant, cancellationToken);
        }

        return 0;
    }

    private async Task SeedTenantAsync(SeedTenant tenant, CancellationToken cancellationToken)
    {
        // Act one, unresolved: the tenant and its default organization, on one
        // transaction, announced with the id being created.
        await SendAsync(
            tenant,
            context: null,
            new ProvisionTenantCommand(
                tenant.TenantId,
                tenant.Slug,
                tenant.DisplayName,
                tenant.DefaultOrganization.OrganizationId,
                tenant.DefaultOrganization.Slug,
                tenant.DefaultOrganization.DisplayName),
            "tenant",
            cancellationToken);

        // Act two, as the tenant: writes the policies check against the announcement.
        var asTenant = new SeedTenantContext(
            tenant.TenantId, tenant.DefaultOrganization.OrganizationId);

        await SendAsync(
            tenant,
            asTenant,
            new CreateOrganizationCommand(
                tenant.SecondOrganization.OrganizationId,
                tenant.SecondOrganization.Slug,
                tenant.SecondOrganization.DisplayName),
            "second organization",
            cancellationToken);

        await SendAsync(
            tenant,
            asTenant,
            new MapHostToTenantCommand(
                tenant.Host,
                tenant.MapHostToDefaultOrganization
                    ? tenant.DefaultOrganization.OrganizationId
                    : null,
                IsActive: true,
                IsPubliclyLive: true),
            "host mapping",
            cancellationToken);
    }

    /// <summary>
    /// Sends one command in a scope composed around <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One scope per command, because a scope is one connection and one transaction under
    /// [ADR-0040](../../../docs/decisions/0040-ambient-unit-of-work.md), and these three
    /// commands are three units of work with different announcements. Sharing a scope
    /// would put the second act on the transaction the first already committed.
    /// </para>
    /// <para>
    /// The context is supplied by composition rather than assignment — see the class
    /// remarks. A null one is an unresolved context, which is what provisioning needs and
    /// what every other act must not get: an earlier version assigned only when non-null,
    /// left the previous act's tenant in place, and the SECOND tenant's provisioning ran
    /// announced as the FIRST. The database refused it 42501, which is the confused-deputy
    /// guard working on the seeder's own bug; composing the value makes the state
    /// unreachable rather than caught.
    /// </para>
    /// </remarks>
    private async Task SendAsync<TResponse>(
        SeedTenant tenant,
        ITenantContext? context,
        IRequest<Result<TResponse>> command,
        string what,
        CancellationToken cancellationToken)
    {
        await using var provider = compose(context);
        await using var scope = provider.CreateAsyncScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISender>()
            .Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            SeedRunnerLog.Seeded(logger, what, tenant.Slug);
            return;
        }

        // A uniqueness refusal is what a second run looks like, and it is the expected
        // outcome of one. Anything else — a validation failure, a policy denial, an
        // organization that is not this tenant's — is a seed that did not do its job, and
        // the process exits non-zero on it.
        if (IsAlreadySeeded(result.Error!))
        {
            SeedRunnerLog.AlreadyPresent(logger, what, tenant.Slug);
            return;
        }

        throw new InvalidOperationException(
            $"Seeding the {what} for '{tenant.Slug}' failed with '{result.Error.Code}'. "
            + "The seed is not idempotent past this point; fix the cause and re-run.");
    }

    /// <summary>
    /// Whether <paramref name="error"/> says the row this act writes already exists.
    /// </summary>
    /// <remarks>
    /// Read from the field-level reasons rather than the top-level code, because the top
    /// level says only <c>business_rule_violation</c> and three different conditions
    /// produce it. These three are the uniqueness ones; a fourth reason under the same
    /// code — <c>lockey_organization_not_in_tenant</c>, <c>lockey_host_reserved</c> —
    /// deliberately falls through to the throw.
    /// </remarks>
    private static bool IsAlreadySeeded(Error error) =>
        error.Details is { } details
        && details.Values.SelectMany(reasons => reasons).Any(reason =>
            AlreadyExists.Contains(reason.Key));

    private static readonly HashSet<string> AlreadyExists = new(StringComparer.Ordinal)
    {
        "lockey_slug_taken",
        "lockey_identifier_taken",
        "lockey_host_taken",
    };
}

/// <summary>Source-generated logging, per the house CA1848 rule.</summary>
public static partial class SeedRunnerLog
{
    [LoggerMessage(EventId = 7002, Level = LogLevel.Information,
        Message = "Seeded {What} for {Slug}.")]
    public static partial void Seeded(ILogger logger, string what, string slug);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Information,
        Message = "{What} for {Slug} already present; leaving it alone.")]
    public static partial void AlreadyPresent(ILogger logger, string what, string slug);
}

/// <summary>
/// The tenant the seeder is currently acting for.
/// </summary>
/// <remarks>
/// <c>UserId</c> is null, so every write is attributed to <c>UserId.SystemActor</c> by the
/// handlers — which is correct and not a shortcut: there is no user in a tenant the seeder
/// just created, and [Audit Coverage](../../../docs/standards/18-audit-coverage.md) puts
/// non-request execution under an actor of type <c>system</c>.
/// </remarks>
public sealed class SeedTenantContext(TenantId tenantId, OrganizationId organizationId)
    : ITenantContext
{
    public bool IsResolved => true;

    /// <summary>
    /// <see cref="TenantContextOrigin.Ambient"/> — the origin for execution with no
    /// request behind it.
    /// </summary>
    /// <remarks>
    /// Not decoration: <c>TenantContextBehavior</c>'s second gate switches over stated
    /// origins and fails closed on <c>null</c>, so a context that omitted this is refused
    /// with the same 404 an unresolvable host gets — measured, as the seeder's first run.
    /// <c>Ambient</c> is the same value <c>EventTenantContext</c> states, and for the same
    /// reason: an integration-event consumer and a seeder are both LearnStack acting for a
    /// tenant with no caller to authenticate.
    /// </remarks>
    public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;

    public TenantId TenantId => tenantId;

    public OrganizationId? OrganizationId => organizationId;

    public UserId? UserId => null;

    public string? CorrelationId => null;

    public string? ModuleName => "tenancy";
}
