using System.Net;
using System.Security.Cryptography;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_rechecks_database_expiry_after_the_recipient_User_wait()
    {
        await using var proof = await CreatePreparedAsync();
        SetEntryRecipient(proof, YogaAdmin);
        using var client = EntryClient(proof);
        var missingId = Guid.CreateVersion7();
        var missing = new EntryOffer(missingId, missingId.ToString("D") + "." + TenantAuthenticationIssuer.Base64Url(RandomNumberGenerator.GetBytes(32)));
        // Warm the real stock validation/lookup path before the short natural deadline.
        using (var warm = await EntrySendAsync(client, EntryToken(proof, YogaAdmin), "inspections", missing))
            await ProblemAsync(warm, HttpStatusCode.NotFound, "not_found");
        var offer = await SeedEntryOfferAsync(proof, expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(1200));
        using var response = await AcceptAfterEntryUserWaitAsync(proof, client, offer, async (app, tx) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!await ScalarAsync<bool>(app, tx, "SELECT clock_timestamp() >= expires_at FROM invitations WHERE id=@id", ("id", offer.Id)))
                await Task.Delay(10, deadline.Token);
        });
        await ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
        Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
        await AssertEntryStateAsync(proof, offer, "Pending", 0);
    }
}
