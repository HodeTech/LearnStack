using System.Data.Common;
using System.Text.Json.Nodes;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The confined owner of the operator's global account closure acts (ADR-0107 §§ 7 and 8): Begin here, and Finalize
/// beside it in its own part of this class.
/// </summary>
/// <remarks>
/// <para>
/// <b>The database bounds what it reads and decides what it admits.</b> Every act is one entered platform scope on
/// <c>learnstack_platform</c>, which reads the request, the person's User status and Memberships, and the administrator
/// predicate's columns through closed column grants. The changes it makes are admitted by the <c>users</c> trigger exception
/// and the request guard's closure arrows only with the fences this connection holds in <c>pg_locks</c>, the subject's User
/// status and the request's own state: no flag of this class is trusted by the database.
/// </para>
/// <para>
/// <b>Lifecycle through Tenancy.</b> Which participant groups are served, and which participant tenants the
/// last-administrator rule protects, comes from Tenancy's own port, read on the scope's transaction; no Tenancy table is
/// named here.
/// </para>
/// <para>
/// <b>The fan-out runs after the commit.</b> The per-group erasures run as the application role in each group's own
/// transaction and take the person's fences themselves, so they start only once Begin has committed and released its own.
/// No fan-out registered refuses the act before anything is entered or written, so a deployment without one never moves a
/// person to Closing.
/// </para>
/// <para>
/// <b>Failures.</b> A transient database fault before the commit, a fence not granted within its bound included, is the
/// unavailable owner and safe to repeat; any failure once the commit was sent is the unknown outcome. Anything else, a
/// refused credential or a missing grant included, is the defect it is.
/// </para>
/// </remarks>
public sealed partial class AccountClosureOperatorOperations(
    IPlatformAdminScope scope,
    IIdentityScopeLifecycleReader lifecycle,
    IDataRequestHoldSource holds,
    IEnumerable<IClosureFanOut> fanOuts,
    ILogger<AccountClosureOperatorOperations> logger) : IAccountClosureOperatorOperations
{
    private const string EntityType = "DataSubjectRequest";

    // The request as the act first meets it, before any fence: enough to refuse what is not a closure, to find the person,
    // and to know whether the closure already began. The platform role reads these columns and the User's status by grant.
    private const string ReadRequest = """
        SELECT r.tenant_id, r.organization_id, r.kind, r.subject_user_id, r.status, person.status
        FROM data_subject_requests AS r LEFT JOIN users AS person ON person.id = r.subject_user_id
        WHERE r.id = @id
        """;

    // The request again, locked, once every fence is held; whether its window has passed is the database clock's answer,
    // which the guard's Begin arrow asks again with a later clock.
    private const string LockRequest = """
        SELECT kind, subject_user_id, status, last_refusal, row_version,
            COALESCE(clock_timestamp() >= execute_at, false)
        FROM data_subject_requests WHERE id = @id FOR UPDATE
        """;

    private const string ReadUserStatus = "SELECT status FROM users WHERE id = @subject";

    /// <summary>The one fan-out the act hands the served groups to, or a refusal when none or several are registered.</summary>
    private IClosureFanOut RequireFanOut()
    {
        var registered = fanOuts.Take(2).ToArray();
        return registered.Length == 1
            ? registered[0]
            : throw new InvalidOperationException(
                "No single closure fan-out is registered, so a closure cannot begin; nothing was entered or changed.");
    }

    /// <summary>
    /// The served participant groups and the unserved count, from each group's lifecycle on the scope's transaction, and
    /// the participant tenants that allow ordinary work.
    /// </summary>
    private async Task<ClosureParticipants> ClassifyAsync(
        IPlatformAdminScopeHandle handle, IReadOnlyList<ClosureParticipantGroup> groups, CancellationToken cancellationToken)
    {
        var facts = await lifecycle.ReadOnPlatformScopeAsync(
            handle, [.. groups.Select(group => new IdentityLifecycleScope(group.TenantId, group.OrganizationId))],
            cancellationToken);
        var served = new List<ClosureParticipantGroup>();
        var operable = new HashSet<TenantId>();
        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            var fact = facts[index];
            if (!fact.TenantAllowsOrdinaryWork)
            {
                continue;
            }

            operable.Add(group.TenantId);
            if (group.OrganizationId is null || (fact.OrganizationBelongsToTenant && fact.OrganizationAllowsOrdinaryWork))
            {
                served.Add(group);
            }
        }

        return new ClosureParticipants(served, groups.Count - served.Count, [.. operable]);
    }

    private static string StatusCode(DataRequestStatus status) => DataRequestCodes.Statuses.ToCode(status);

    private static string Account(UserAccount account) => account switch
    {
        UserAccount.Active => "active",
        UserAccount.Closing => "closing",
        _ => "closed",
    };

    private static UserAccount ParseAccount(string? status) => status switch
    {
        "Active" => UserAccount.Active,
        "Closing" => UserAccount.Closing,
        "Closed" => UserAccount.Closed,
        // The foreign key keeps the User of every request, and ck_users_status closes its status.
        _ => throw new InvalidOperationException("A closure's person has no readable User status; the act is abandoned."),
    };

    private static string State(params (string Name, JsonNode? Value)[] members)
    {
        var state = new JsonObject();
        foreach (var (name, value) in members)
        {
            state[name] = value;
        }

        return state.ToJsonString();
    }

    private static void RequireOperation(PlatformAdmission admission, PlatformOperation expected)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (!ReferenceEquals(admission.Operation, expected))
        {
            throw new ArgumentException("The admission is not for this closure operation.", nameof(admission));
        }
    }

    /// <summary>
    /// A fault before the commit that a repeat may not meet: the transient database faults the other owners retry. A fence
    /// or a row lock not granted within the act's bound (<c>55P03</c>) and a deadlock the database broke (<c>40P01</c>) are
    /// among them, because the driver classifies both as transient; a test holds a fence past the bound to pin it.
    /// </summary>
    private static bool IsRetryable(Exception failure, CancellationToken cancellationToken) =>
        OperatorRecoveryFaults.IsTransientDatabaseFault(failure, cancellationToken);

    private static bool IsFatal(Exception failure) => failure is OutOfMemoryException or AccessViolationException or StackOverflowException;

    [LoggerMessage(EventId = 7120, Level = LogLevel.Error,
        Message = "A closure's fan-out threw after its Begin committed; every served group is reported failed and a repeated Begin retries them.")]
    private static partial void LogFanOutFailed(ILogger logger, Exception failure);

    /// <summary>The person's User status, closed.</summary>
    private enum UserAccount
    {
        Active = 1,
        Closing,
        Closed,
    }

    /// <summary>The participant groups split by their lifecycle.</summary>
    /// <param name="Served">The groups the fan-out reaches.</param>
    /// <param name="Unserved">How many groups it does not.</param>
    /// <param name="OperableTenants">The participant tenants that allow ordinary work.</param>
    private sealed record ClosureParticipants(
        IReadOnlyList<ClosureParticipantGroup> Served, int Unserved, IReadOnlyList<TenantId> OperableTenants)
    {
        public override string ToString() => nameof(ClosureParticipants);
    }

    /// <summary>The request as the act first reads it, before any fence; its group and person never change.</summary>
    private sealed record ClosureRequest(
        DataRequestScopeGroup Group, DataRequestKind Kind, UserId Subject, DataRequestStatus Status, UserAccount Account)
    {
        internal TenantId Tenant => Group.TenantId;

        internal static ClosureRequest Read(DbDataReader reader) => new(
            new DataRequestScopeGroup(
                TenantId.From(reader.GetGuid(0)), reader.IsDBNull(1) ? null : OrganizationId.From(reader.GetGuid(1))),
            DataRequestCodes.Kinds.Parse(reader.GetString(2)), UserId.From(reader.GetGuid(3)),
            DataRequestCodes.Statuses.Parse(reader.GetString(4)), ParseAccount(reader.IsDBNull(5) ? null : reader.GetString(5)));

        /// <summary>Never prints the person.</summary>
        public override string ToString() => nameof(ClosureRequest);
    }

    /// <summary>The request as the act reads it locked, under every fence.</summary>
    private sealed record LockedClosure(
        DataRequestKind Kind, UserId Subject, DataRequestStatus Status, DataRequestRefusal? LastRefusal, long Version, bool Due)
    {
        internal static LockedClosure Read(DbDataReader reader) => new(
            DataRequestCodes.Kinds.Parse(reader.GetString(0)), UserId.From(reader.GetGuid(1)),
            DataRequestCodes.Statuses.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DataRequestCodes.Refusals.Parse(reader.GetString(3)), reader.GetInt64(4),
            reader.GetBoolean(5));

        /// <summary>Never prints the person.</summary>
        public override string ToString() => nameof(LockedClosure);
    }
}
