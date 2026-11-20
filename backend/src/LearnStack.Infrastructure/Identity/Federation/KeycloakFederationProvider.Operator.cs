using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.Federation;

/// <summary>
/// The operator half of the adapter (ADR-0099 Amendment 10): a paged listing of the realm's identity-provider
/// instances, the inspection of one alias, a remove and a marker-proven disable. It reaches the provider only through
/// <see cref="KeycloakFederationRoutes"/>, adds no realm authority to the admin client and returns closed facts and
/// outcomes: no representation, configuration value, display name, issuer or secret crosses the port.
/// </summary>
internal sealed partial class KeycloakFederationProvider
{
    // One page of full representations: a hundred instances of a few kilobytes each, with room to spare.
    private const int ListLimit = 1024 * 1024;

    /// <inheritdoc />
    public async Task<FederationBrokerPage> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return await RunAsync(FederationBrokerPage.Unavailable, async (bearer, token) =>
        {
            var response = await SendAsync(
                HttpMethod.Get, KeycloakFederationRoutes.InstancePage(_options, offset, limit), null, bearer, ListLimit, token)
                .ConfigureAwait(false);
            if (response.Code != HttpStatusCode.OK || response.Body is null) throw Fault();
            try
            {
                using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw Fault();
                // A provider that ignored the page size answered outside the admitted shape.
                if (document.RootElement.GetArrayLength() > limit) throw Fault();
                var owned = new List<FederationBrokerFacts>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var ignored = 0;
                var scanned = 0;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    scanned++;
                    if (TryClassify(item, out var facts) && seen.Add(facts.Owner.Alias)) owned.Add(facts);
                    else ignored++;
                }

                return new FederationBrokerPage(FederationOperatorStatus.Ok, owned, ignored, scanned);
            }
            catch (JsonException) { throw Fault(); }
            finally { CryptographicOperations.ZeroMemory(response.Body); }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FederationBrokerInspection> InspectAsync(string brokerAlias, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brokerAlias);
        return await RunAsync(FederationBrokerInspection.Unavailable, async (bearer, token) =>
        {
            var probe = await ProbeAsync(brokerAlias, bearer, token).ConfigureAwait(false);
            try
            {
                switch (probe.Kind)
                {
                    case Probe.Absent: return FederationBrokerInspection.Absent;
                    case Probe.NotOwned: return FederationBrokerInspection.NotOwned;
                }

                var mappers = 0;
                if (probe.Facts!.Readable)
                {
                    mappers = await CountMappersCoreAsync(probe.Facts.Owner, bearer, token).ConfigureAwait(false);
                }

                return new FederationBrokerInspection(FederationInspectionStatus.Owned, probe.Facts, mappers);
            }
            finally { probe.Zero(); }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int?> CountMappersAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await RunAsync<int?>(null, async (bearer, token) =>
            await CountMappersCoreAsync(owner, bearer, token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FederationOperatorOutcome> RemoveAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var sent = false;
        var outcome = await RunAsync(FederationOperatorOutcome.Unavailable, async (bearer, token) =>
        {
            var probe = await ProbeAsync(owner.Alias, bearer, token).ConfigureAwait(false);
            try
            {
                if (probe.Kind == Probe.Absent) return FederationOperatorOutcome.AlreadyApplied;
                if (probe.Kind == Probe.NotOwned || !Names(probe.Facts!.Owner, owner)) return FederationOperatorOutcome.NotOwned;
            }
            finally { probe.Zero(); }

            sent = true;
            var deleted = await SendAsync(HttpMethod.Delete, KeycloakFederationRoutes.Instance(_options, owner), null, bearer, 0, token)
                .ConfigureAwait(false);
            switch (deleted.Code)
            {
                case HttpStatusCode.NoContent or HttpStatusCode.NotFound:
                    break;
                case HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized:
                    return FederationOperatorOutcome.Refused;
                default:
                    throw Fault();
            }

            // The delete is confirmed by absence: an instance still there is an outcome nobody can state.
            var after = await ProbeAsync(owner.Alias, bearer, token).ConfigureAwait(false);
            try { return after.Kind == Probe.Absent ? FederationOperatorOutcome.Applied : FederationOperatorOutcome.Uncertain; }
            finally { after.Zero(); }
        }, cancellationToken).ConfigureAwait(false);

        // A fault after the delete was sent leaves its outcome unknown; before it, nothing happened.
        return outcome == FederationOperatorOutcome.Unavailable && sent ? FederationOperatorOutcome.Uncertain : outcome;
    }

    /// <inheritdoc />
    public async Task<FederationOperatorOutcome> DisableAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var sent = false;
        var outcome = await RunAsync(FederationOperatorOutcome.Unavailable, async (bearer, token) =>
        {
            var probe = await ProbeAsync(owner.Alias, bearer, token).ConfigureAwait(false);
            byte[] replacement;
            try
            {
                if (probe.Kind == Probe.Absent) return FederationOperatorOutcome.Refused;
                if (probe.Kind == Probe.NotOwned || !Names(probe.Facts!.Owner, owner)) return FederationOperatorOutcome.NotOwned;
                if (!probe.Facts.Enabled) return FederationOperatorOutcome.AlreadyApplied;
                replacement = DisabledRepresentation(probe.Body!);
            }
            finally { probe.Zero(); }

            try
            {
                using var content = new ByteArrayContent(replacement);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                sent = true;
                var put = await SendAsync(HttpMethod.Put, KeycloakFederationRoutes.Instance(_options, owner), content, bearer, 0, token)
                    .ConfigureAwait(false);
                switch (put.Code)
                {
                    case HttpStatusCode.NoContent:
                        break;
                    case HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                        or HttpStatusCode.Unauthorized:
                        return FederationOperatorOutcome.Refused;
                    default:
                        throw Fault();
                }
            }
            finally { CryptographicOperations.ZeroMemory(replacement); }

            var after = await ProbeAsync(owner.Alias, bearer, token).ConfigureAwait(false);
            try
            {
                return after is { Kind: Probe.Owned, Facts.Enabled: false } ? FederationOperatorOutcome.Applied : FederationOperatorOutcome.Uncertain;
            }
            finally { after.Zero(); }
        }, cancellationToken).ConfigureAwait(false);

        return outcome == FederationOperatorOutcome.Unavailable && sent ? FederationOperatorOutcome.Uncertain : outcome;
    }

    /// <summary>
    /// The representation the provider returned with <c>enabled</c> false and nothing else changed. The two members the
    /// provider computes on every read are dropped, and the secret member keeps the provider's mask literal, which
    /// the provider reads as "keep the stored value" (ADR-0099 Amendment 3). This is the one provider write that sends
    /// back what the provider returned, which the operator disable needs for a broker the adapter cannot read.
    /// </summary>
    private static byte[] DisabledRepresentation(byte[] returned)
    {
        try
        {
            if (JsonNode.Parse(returned) is not JsonObject representation) throw Fault();
            representation.Remove("internalId");
            representation.Remove("types");
            representation["enabled"] = false;
            return JsonSerializer.SerializeToUtf8Bytes(representation);
        }
        // A body that is not JSON, or an object that repeats a member, is not a representation to send back.
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Fault();
        }
    }

    private async Task<int> CountMappersCoreAsync(FederationBrokerOwner owner, string bearer, CancellationToken token)
    {
        var mappers = await SendAsync(HttpMethod.Get, KeycloakFederationRoutes.Mappers(_options, owner), null, bearer, MapperLimit, token)
            .ConfigureAwait(false);
        if (mappers.Code != HttpStatusCode.OK || mappers.Body is null) throw Fault();
        try
        {
            using var document = JsonDocument.Parse(mappers.Body, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw Fault();
            return document.RootElement.GetArrayLength();
        }
        catch (JsonException) { throw Fault(); }
        finally { CryptographicOperations.ZeroMemory(mappers.Body); }
    }

    /// <summary>Reads one alias and classifies it by its marker; the body stays for a caller that must send it back.</summary>
    private async Task<ProbeResult> ProbeAsync(string alias, string bearer, CancellationToken token)
    {
        var response = await SendAsync(HttpMethod.Get, KeycloakFederationRoutes.InstanceOf(_options, alias), null, bearer, InstanceLimit, token)
            .ConfigureAwait(false);
        if (response.Code == HttpStatusCode.NotFound) return new ProbeResult(Probe.Absent, null, null);
        if (response.Code != HttpStatusCode.OK || response.Body is null) throw Fault();
        try
        {
            using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 16 });
            if (!TryClassify(document.RootElement, out var facts) || !string.Equals(facts.Owner.Alias, alias, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(response.Body);
                return new ProbeResult(Probe.NotOwned, null, null);
            }

            return new ProbeResult(Probe.Owned, facts, response.Body);
        }
        catch (JsonException)
        {
            CryptographicOperations.ZeroMemory(response.Body);
            throw Fault();
        }
    }

    /// <summary>
    /// Whether one representation is an instance LearnStack owns: the alias is the derived form, the marker parses
    /// strictly to a tenant and a connection whose derived alias it is, and the owner is then read for shape and profile.
    /// Everything else is ignored: an unmarked, foreign or marker-mismatched instance is never reported or touched.
    /// </summary>
    private static bool TryClassify(JsonElement item, out FederationBrokerFacts facts)
    {
        facts = null!;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("alias", out var aliasElement)
            || aliasElement.ValueKind != JsonValueKind.String || aliasElement.GetString() is not { Length: 36 } alias
            || !alias.StartsWith(FederationBrokerOwner.AliasPrefix, StringComparison.Ordinal)
            || !item.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object
            || !config.TryGetProperty(KeycloakFederationProfile.OwnerKey, out var marker) || marker.ValueKind != JsonValueKind.String
            || !KeycloakFederationProfile.TryOwnerFromMarker(marker.GetString(), alias, out var owner))
            return false;
        if (KeycloakFederationProfile.Read(item, owner) is { } read)
        {
            facts = new FederationBrokerFacts(owner, Readable: true, read.Conforms, read.State.Enabled, read.State.Protocol);
            return true;
        }

        // Unreadable: the marker is the only proof. Anything but an explicit false counts as enabled, because the
        // operator's step is to switch an offered sign-in option off.
        var enabled = !(item.TryGetProperty("enabled", out var flag) && flag.ValueKind == JsonValueKind.False);
        facts = new FederationBrokerFacts(owner, Readable: false, Conforms: false, enabled, Protocol: null);
        return true;
    }

    private static bool Names(FederationBrokerOwner found, FederationBrokerOwner expected) =>
        found.TenantId == expected.TenantId && found.ConnectionId == expected.ConnectionId
        && string.Equals(found.Alias, expected.Alias, StringComparison.Ordinal);

    private enum Probe { Absent, NotOwned, Owned }

    private readonly record struct ProbeResult(Probe Kind, FederationBrokerFacts? Facts, byte[]? Body)
    {
        internal void Zero()
        {
            if (Body is not null) CryptographicOperations.ZeroMemory(Body);
        }
    }
}
