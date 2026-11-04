using System.Net;
using LearnStack.Api.Authentication;
using Npgsql;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_witness_refuses_nested_same_or_copied_request_and_second_physical_owner()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        var observation = new EntryWitnessObservation();
        using var client = EntryClient(proof, configure: services =>
        {
            services.AddSingleton(observation);
            services.AddScoped<CurrentInvitationRecipientReader>();
            services.Replace(ServiceDescriptor.Scoped<ICurrentInvitationRecipientReader, EntryWitnessProbe>());
        }, defaultPoolSize: 2);
        using var response = await EntrySendAsync(client, EntryToken(proof, YogaAdmin), "inspections", offer);
        await JsonAsync(response);
        Assert.Equal(observation.FailedOwnerProcess, observation.ReusedOwnerProcess);
        Assert.Equal(2, observation.NestedRefusals);
        Assert.True(observation.SecondOwnerRefused);
        Assert.True(observation.UnwitnessedSetterRefused);
        Assert.NotNull(observation.Witness);
        Assert.False(observation.Witness.IsActive);
        Assert.Equal(1, proof.Issuer.UserInfoRequests);
        var denied = await proof.AuditAsync(response, Tenant);
        Assert.Equal(2, denied.Count);
        Assert.All(denied, row => { Assert.Equal("identity.authorization.deny", row.Operation); Assert.Equal("denied", row.Outcome); });
    }
    [Theory]
    [InlineData(TransactionMode.ReadOnly)]
    [InlineData(TransactionMode.ReadWrite)]
    public async Task Actual_invitation_entry_refuses_ordinary_join_or_mode_upgrade_and_closes_witness(TransactionMode mode)
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        var observation = new EntryWitnessObservation { AttemptJoin = mode };
        using var client = EntryClient(proof, configure: services =>
        {
            services.AddSingleton(observation);
            services.AddScoped<CurrentInvitationRecipientReader>();
            services.Replace(ServiceDescriptor.Scoped<ICurrentInvitationRecipientReader, EntryWitnessProbe>());
        });
        using var response = await EntrySendAsync(client, EntryToken(proof, YogaAdmin), "inspections", offer);
        await ProblemAsync(response, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        Assert.NotNull(observation.Witness);
        Assert.False(observation.Witness.IsActive);
        Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
        await AssertEntryStateAsync(proof, offer, "Pending", 0);
    }
    private sealed class EntryWitnessObservation
    {
        internal int FailedOwnerProcess { get; set; }
        internal int ReusedOwnerProcess { get; set; }
        internal TransactionMode? AttemptJoin { get; init; }
        internal int NestedRefusals { get; set; }
        internal bool SecondOwnerRefused { get; set; }
        internal bool UnwitnessedSetterRefused { get; set; }
        internal InvitationEntryScopeWitness? Witness { get; set; }
    }
    private sealed class EntryWitnessProbe(CurrentInvitationRecipientReader actual, ISender sender,
        IOperationInvocationContext invocations, IServiceScopeFactory scopes, IUnitOfWork currentUnit, EntryWitnessObservation observation)
        : ICurrentInvitationRecipientReader
    {
        public async Task<Result<CurrentInvitationRecipientEvidence>> ReadAsync(OperationInvocation invocation, CancellationToken cancellationToken = default)
        {
            var original = Assert.IsType<InspectInvitationQuery>(invocation.Operation);
            var witness = Assert.IsType<InvitationEntryScopeWitness>(invocations.CurrentDecision!.InvitationWitness);
            observation.Witness = witness;
            if (observation.AttemptJoin is { } mode)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => currentUnit.BeginTransactionAsync(mode, cancellationToken));
                Assert.True(currentUnit.IsRollbackOnly);
                return await actual.ReadAsync(invocation, cancellationToken);
            }
            foreach (var request in new[] { original, new InspectInvitationQuery(original.InvitationId, original.Code) })
            {
                var refused = await sender.Send(request, cancellationToken);
                Assert.True(refused.IsFailure);
                observation.NestedRefusals++;
                Assert.Same(invocation, invocations.CurrentInvocation);
                Assert.True(witness.IsActive);
            }
            await using (var child = scopes.CreateAsyncScope())
            {
                var unit = child.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var owner = Assert.IsAssignableFrom<IInvitationEntryUnitOfWork>(unit);
                await Assert.ThrowsAsync<InvalidOperationException>(() => owner.BeginInvitationEntryTransactionAsync(witness, invocation.Context, cancellationToken));
                Assert.False(unit.HasActiveTransaction);
                Assert.True(unit.IsRollbackOnly);
                await Assert.ThrowsAsync<InvalidOperationException>(() => unit.BeginTransactionAsync(TransactionMode.ReadOnly, cancellationToken));
                observation.FailedOwnerProcess = ((NpgsqlConnection)unit.Connection).ProcessID;
                observation.SecondOwnerRefused = true;
            }
            await using (var child = scopes.CreateAsyncScope())
            {
                var unit = child.ServiceProvider.GetRequiredService<IUnitOfWork>();
                await using var frame = await unit.BeginTransactionAsync(TransactionMode.ReadOnly, cancellationToken);
                observation.ReusedOwnerProcess = ((NpgsqlConnection)unit.Connection).ProcessID;
                await using var probe = new NpgsqlCommand("SELECT current_setting('transaction_read_only')='on' AND coalesce(current_setting('app.tenant_id',true),'')='' AND coalesce(current_setting('app.organization_id',true),'')=''",
                    (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
                Assert.Equal(true, await probe.ExecuteScalarAsync(cancellationToken));
                Assert.False(((IInvitationEntryUnitOfWork)unit).IsInvitationEntryOwner(witness));
                await Assert.ThrowsAsync<InvalidOperationException>(() => unit.SetTenantContextAsync(invocation.Context, cancellationToken));
                Assert.True(unit.IsRollbackOnly);
                observation.UnwitnessedSetterRefused = true;
            }
            Assert.True(witness.IsActive);
            return await actual.ReadAsync(invocation, cancellationToken);
        }
    }
}
