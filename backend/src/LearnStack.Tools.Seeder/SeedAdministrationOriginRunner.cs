using System.Collections.Immutable;
using System.Text.Json;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Tools.Seeder;

public sealed class FixedSeedAdministrationOriginAdmission : ISeedAdministrationOriginAdmission
{
    public SeedAdministrationOriginDeclaration? ForTenant(TenantId tenantId) => SeedAdministrationOriginManifest.Find(tenantId);
}

/// <summary>Fixed per-Tenant upgrade; complete repeats stop before declaring another MUST act.</summary>
public sealed class SeedAdministrationOriginRunner(Func<ITenantContext?, ServiceProvider> compose)
{
    public async Task<int> RunAsync(IEnumerable<TenantId> tenantIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        var selected = tenantIds.Take(3).ToArray();
        if (selected.Length > 2 || selected.Distinct().Count() != selected.Length
            || selected.Any(id => SeedAdministrationOriginManifest.Find(id) is null)) throw Refused();
        var mutations = 0;
        foreach (var id in selected)
        {
            var declaration = SeedAdministrationOriginManifest.Find(id)!;
            var context = new SeedTenantContext(id, null);
            var before = (await SendAsync(context, new GetTenantSeedStateQuery(), cancellationToken)).State;
            if (before is null) throw Refused();
            if (before.AdoptedSeedOriginVersion == 2)
            {
                await VerifyMappingAsync(context, declaration, cancellationToken);
                continue;
            }
            if (before.AdoptedSeedOriginVersion is not (0 or 1)) throw Refused();
            if (before.AdoptedSeedOriginVersion == 1)
                await VerifyMappingAsync(context, declaration, cancellationToken);
            await SendAsync(context, new AdoptSeedAdministrationOriginCommand(before.Version), cancellationToken);
            var after = (await SendAsync(context, new GetTenantSeedStateQuery(), cancellationToken)).State;
            if (after?.AdoptedSeedOriginVersion != 2) throw Refused();
            await VerifyMappingAsync(context, declaration, cancellationToken);
            mutations++;
        }
        return mutations;
    }

    private async Task VerifyMappingAsync(ITenantContext context, SeedAdministrationOriginDeclaration declaration,
        CancellationToken cancellationToken)
    {
        var mapping = (await SendAsync(context, new GetHostMappingSeedStateQuery(declaration.Host), cancellationToken)).State;
        if (mapping is null || mapping.Host != declaration.Host || mapping.TenantId != declaration.TenantId
            || mapping.OrganizationId is not null || !mapping.IsActive || !mapping.IsPubliclyLive) throw Refused();
    }

    private async Task<T> SendAsync<T>(ITenantContext context, IRequest<Result<T>> request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await using var provider = compose(context);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, deadline.Token);
        if (result.IsFailure || result.Value is null) throw Refused();
        return result.Value;
    }

    public static async Task<ImmutableArray<TenantId>> ReadSelectionAsync(Stream input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = new byte[513];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
        }
        if (length is 0 or > 512) throw Refused();
        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(["version", "tenantIds"])
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("tenantIds", out var ids)) throw Refused();
            if (ids.ValueKind == JsonValueKind.String && ids.GetString() == "all")
                return [.. SeedAdministrationOriginManifest.All.Select(value => value.TenantId)];
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 2) throw Refused();
            var selected = new List<TenantId>();
            foreach (var item in ids.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text
                    || !Guid.TryParseExact(text, "D", out var id) || id.ToString("D") != text
                    || SeedAdministrationOriginManifest.Find(TenantId.From(id)) is null) throw Refused();
                selected.Add(TenantId.From(id));
            }
            if (selected.Distinct().Count() != selected.Count) throw Refused();
            return [.. selected];
        }
        catch (JsonException) { throw Refused(); }
    }

    private static InvalidOperationException Refused() => new("Development administration origin adoption was refused.");
}
