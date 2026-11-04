using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_invitation_entry_enforces_8192_byte_limit_with_declared_or_counted_413(bool streamed)
    {
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof);
        SetEntryRecipient(proof, YogaAdmin);
        var lengths = new EntryBodyLengthObservation();
        using var client = EntryClient(proof, configure: services => services.AddSingleton<IStartupFilter>(lengths));
        var token = EntryToken(proof, YogaAdmin);
        var valid = JsonSerializer.Serialize(new { invitationId = offer.Id, code = offer.Code });
        var prefixBytes = Encoding.UTF8.GetByteCount(valid);
        foreach (var size in new[] { 8193, 8192 })
        {
            var body = valid + new string(' ', size - prefixBytes);
            using var response = await EntrySendAsync(client, token, "inspections", offer, mutate: request =>
            {
                if (streamed)
                {
                    request.Content = new UndeclaredEntryContent(Encoding.UTF8.GetBytes(body));
                    request.Headers.TransferEncodingChunked = true;
                }
                else request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            });
            Assert.Equal(streamed ? null : (long?)size, lengths.Lengths[^1]);
            if (size > 8192)
            {
                await ProblemAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
                Assert.Equal(0, proof.Issuer.UserInfoRequests);
            }
            else
            {
                await JsonAsync(response);
                Assert.Equal(1, proof.Issuer.UserInfoRequests);
            }
        }
        Assert.Equal(2, lengths.Lengths.Count);
    }

    private sealed class UndeclaredEntryContent : HttpContent
    {
        private readonly byte[] _bytes;
        public UndeclaredEntryContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class EntryBodyLengthObservation : IStartupFilter
    {
        internal List<long?> Lengths { get; } = [];
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) => { Lengths.Add(context.Request.ContentLength); return following(context); });
            next(app);
        };
    }
}
