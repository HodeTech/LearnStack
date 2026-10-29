using System.Collections.Immutable;

namespace LearnStack.SharedKernel.Permissions;

public enum ProtectedOperationOutcome { Unavailable = 0, NotCovered = 1, Forbidden = 2, Allowed = 3 }

/// <summary>A private-minted decision for one invocation, with only bounded audit identifiers.</summary>
public sealed class ProtectedOperationDecision
{
    private ProtectedOperationDecision(OperationInvocation invocation, ProtectedOperationOutcome outcome,
        ImmutableArray<Guid> membershipIds, ImmutableArray<Guid> roleIds, OperationPolicyClause? matchedClause, OperationReadWitness? readWitness)
    {
        Invocation = invocation;
        Outcome = outcome;
        MembershipIds = membershipIds;
        RoleIds = roleIds;
        MatchedClause = matchedClause;
        ReadWitness = readWitness;
    }

    public OperationInvocation Invocation { get; }
    public ProtectedOperationOutcome Outcome { get; }
    public ImmutableArray<Guid> MembershipIds { get; }
    public ImmutableArray<Guid> RoleIds { get; }
    public OperationPolicyClause? MatchedClause { get; }
    public OperationReadWitness? ReadWitness { get; }
    public bool IsActive => Invocation.IsActive;
    public override string ToString() => nameof(ProtectedOperationDecision);

#pragma warning disable CA1034 // Only the enumerated Identity evaluator derives the decision source.
    public abstract class MintingSource : OperationReadWitness.MintingSource
#pragma warning restore CA1034
    {
        protected static ProtectedOperationDecision ForDecision(OperationInvocation invocation, AuthorizationCandidate candidate,
            ProtectedOperationOutcome outcome, IEnumerable<Guid>? membershipIds = null, IEnumerable<Guid>? roleIds = null,
            OperationPolicyClause? matchedClause = null)
        {
            ArgumentNullException.ThrowIfNull(invocation);
            ArgumentNullException.ThrowIfNull(candidate);
            if (!Enum.IsDefined(outcome) || !ReferenceEquals(invocation.Candidate, candidate)
                || invocation.Policy.Kind is not (OperationPolicyKind.AccountRead or OperationPolicyKind.Protected
                    or OperationPolicyKind.NativeSessionAdmission))
                throw new ArgumentException("Protected operation decision provenance is invalid.");
            var members = Snapshot(membershipIds, 2);
            var roles = Snapshot(roleIds, 8);
            if (outcome == ProtectedOperationOutcome.Allowed && (!invocation.IsActive || !candidate.IsActive || members.IsEmpty))
                throw new InvalidOperationException("Protected operation admission has no active membership witness.");
            if (outcome == ProtectedOperationOutcome.Allowed && invocation.Policy.Kind == OperationPolicyKind.Protected)
            {
                matchedClause ??= invocation.Policy.Alternatives.Length == 1 ? invocation.Policy.Alternatives[0] : null;
                if (matchedClause is null || !invocation.Policy.Alternatives.Any(clause => ReferenceEquals(clause, matchedClause)))
                    throw new ArgumentException("Protected operation admission has no matching policy clause.");
            }
            else if (matchedClause is not null)
                throw new ArgumentException("Protected operation clause is incompatible with its outcome.");
            var read = matchedClause?.ReadScope == OperationReadScope.Tenant
                ? ForTenantRead(invocation, candidate, matchedClause) : null;
            return new(invocation, outcome, members, roles, matchedClause, read);
        }

        private static ImmutableArray<Guid> Snapshot(IEnumerable<Guid>? values, int maximum)
        {
            if (values is null) return [];
            var snapshot = values.Take(maximum + 1).ToArray();
            if (snapshot.Length > maximum || snapshot.Any(id => id == Guid.Empty || id.Version != 7)
                || snapshot.Distinct().Count() != snapshot.Length)
                throw new ArgumentException("Protected operation evidence identifiers are invalid.");
            return [.. snapshot.Order()];
        }
    }
}
