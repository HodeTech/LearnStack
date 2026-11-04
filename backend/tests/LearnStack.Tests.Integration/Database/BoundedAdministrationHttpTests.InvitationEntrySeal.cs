using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_validation_seals_both_write_ports_before_central_commit()
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        var observation = new EntrySealObservation();
        using var client = EntryClient(proof, configure: services =>
        {
            services.AddSingleton(observation);
            services.AddScoped<MembershipWriteStore>();
            services.Replace(ServiceDescriptor.Scoped<IMembershipWriteStore, EntrySealProbe>());
        });
        using var response = await EntrySendAsync(client, EntryToken(proof, YogaAdmin), "acceptances", offer, "\"0\"");
        var result = await JsonAsync(response);
        Assert.True(observation.LateWriteRefused);
        Assert.Equal("1", result.GetProperty("version").GetString());
        Assert.Equal(2, (await proof.AuditAsync(response, Tenant)).Count);
        await AssertEntryStateAsync(proof, offer, "Accepted", 1);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await using var tx = await app.BeginTransactionAsync();
        await ScopeAsync(app, tx, Tenant.TenantId.Value);
        Assert.True(await ScalarAsync<bool>(app, tx,
            "SELECT submission_status='Queued' AND row_version=1 FROM invitations WHERE id=@id", ("id", offer.Id)));
    }

    private sealed class EntrySealObservation { internal bool LateWriteRefused { get; set; } }

    // An actual current-invocation late write at the existing graph-validation seam.
    // The probe restores only its unsaved change so the legitimate central commit
    // also proves that neither the attempted write nor a private savepoint escaped.
    private sealed class EntrySealProbe(MembershipWriteStore actual, IInvitationWriteStore invitations,
        IdentityDbContext db, IUnitOfWork unit, IOperationInvocationContext invocations, EntrySealObservation observation)
        : IMembershipWriteStore
    {
        public Task<Membership?> FindAsync(MembershipId id, CancellationToken cancellationToken = default) => actual.FindAsync(id, cancellationToken);
        public Task LockAsync(MembershipId id, CancellationToken cancellationToken = default) => actual.LockAsync(id, cancellationToken);
        public Task<bool> HasMembershipHistoryAsync(CancellationToken cancellationToken = default) => actual.HasMembershipHistoryAsync(cancellationToken);
        public Task AddAsync(Membership aggregate, CancellationToken cancellationToken = default) => actual.AddAsync(aggregate, cancellationToken);
        public Task UpdateAsync(Membership aggregate, CancellationToken cancellationToken = default) => actual.UpdateAsync(aggregate, cancellationToken);
        public async Task ValidateGraphAsync(CancellationToken cancellationToken = default)
        {
            await actual.ValidateGraphAsync(cancellationToken);
            var witness = invocations.CurrentDecision!.InvitationWitness!;
            Assert.True(witness.IsActive);
            Assert.True(((IInvitationEntryUnitOfWork)unit).IsInvitationEntryOwner(witness));
            ((IUnitOfWorkWriteGuard)unit).RequireWriteAllowed();
            var target = Assert.Single(db.ChangeTracker.Entries<Invitation>());
            Assert.Equal(EntityState.Unchanged, target.State);
            Assert.Equal(InvitationStatus.Accepted, target.Entity.Status);
            Assert.True(target.Entity.RecordWorkflowFailure(target.Entity.GenerationId, target.Entity.CurrentDeliveryJobId,
                target.Entity.AcceptedAt!.Value.AddSeconds(1), witness.Offer.Actor).IsSuccess);
            try
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => invitations.UpdateAsync(target.Entity, cancellationToken));
                Assert.Equal("An Identity graph cannot change after validation.", failure.Message);
                observation.LateWriteRefused = true;
            }
            finally
            {
                target.CurrentValues.SetValues(target.OriginalValues);
                target.State = EntityState.Unchanged;
            }
        }
    }
}
