using System.Text;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Tooling;

public sealed class SeedOriginSelectionTests
{
    [Theory]
    [InlineData("{\"version\":1,\"tenantIds\":[]}", 0)]
    [InlineData("{\"version\":1,\"tenantIds\":\"all\"}", 2)]
    [InlineData("{\"version\":1,\"tenantIds\":[\"01930000-0000-7000-8000-000000000001\"]}", 1)]
    public async Task The_fixed_selection_accepts_only_declared_tenants(string json, int count)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        (await SeedAdministrationOriginRunner.ReadSelectionAsync(input, CancellationToken.None)).Should().HaveCount(count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"1\",\"tenantIds\":[]}")]
    [InlineData("{\"version\":2,\"tenantIds\":[]}")]
    [InlineData("{\"version\":1,\"version\":1,\"tenantIds\":[]}")]
    [InlineData("{\"version\":1,\"tenantIds\":[],\"secret\":\"private-canary\"}")]
    [InlineData("{\"version\":1,\"tenantIds\":[\"private-canary\"]}")]
    [InlineData("{\"version\":1,\"tenantIds\":[\"01930000-0000-7000-8000-000000000099\"]}")]
    [InlineData("{\"version\":1,\"tenantIds\":[\"01930000-0000-7000-8000-000000000001\",\"01930000-0000-7000-8000-000000000001\"]}")]
    public async Task Malformed_or_foreign_selection_refuses_without_echoing_values(string json)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        await FluentActions.Awaiting(() => SeedAdministrationOriginRunner.ReadSelectionAsync(input, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("Development administration origin adoption was refused.");
    }

    [Fact]
    public async Task Qualified_marker_two_repeats_dispatch_only_the_two_narrow_queries()
    {
        var sender = new OriginSender(2);
        (await Runner(sender).RunAsync([SeedData.English.TenantId])).Should().Be(0);
        sender.Requests.Select(request => request.GetType()).Should().Equal(
            typeof(GetTenantSeedStateQuery), typeof(GetHostMappingSeedStateQuery));
        sender.Requests.OfType<AdoptSeedAdministrationOriginCommand>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Only_a_new_explicit_run_dispatches_marker_zero_or_one_for_full_qualification(int marker)
    {
        var sender = new OriginSender(marker);
        (await Runner(sender).RunAsync([SeedData.English.TenantId])).Should().Be(1);
        sender.Requests.OfType<AdoptSeedAdministrationOriginCommand>().Should().ContainSingle();
        sender.Marker.Should().Be(2);
        var requests = sender.Requests.Select(request => request.GetType()).ToArray();
        if (marker == 1) requests.Should().Equal(typeof(GetTenantSeedStateQuery), typeof(GetHostMappingSeedStateQuery),
            typeof(AdoptSeedAdministrationOriginCommand), typeof(GetTenantSeedStateQuery), typeof(GetHostMappingSeedStateQuery));
        else requests.Should().Equal(typeof(GetTenantSeedStateQuery), typeof(AdoptSeedAdministrationOriginCommand),
            typeof(GetTenantSeedStateQuery), typeof(GetHostMappingSeedStateQuery));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task An_existing_marker_with_a_missing_mapping_refuses_before_command_dispatch(int marker)
    {
        var sender = new OriginSender(marker) { MappingPresent = false };
        await FluentActions.Awaiting(() => Runner(sender).RunAsync([SeedData.English.TenantId]))
            .Should().ThrowAsync<InvalidOperationException>();
        sender.Requests.OfType<AdoptSeedAdministrationOriginCommand>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task Unknown_markers_refuse_without_mapping_or_mutation_dispatch(int marker)
    {
        var sender = new OriginSender(marker);
        await FluentActions.Awaiting(() => Runner(sender).RunAsync([SeedData.English.TenantId]))
            .Should().ThrowAsync<InvalidOperationException>();
        sender.Requests.Should().ContainSingle().Which.Should().BeOfType<GetTenantSeedStateQuery>();
    }

    [Fact]
    public async Task An_uncertain_mutation_does_not_retry_or_observe_inside_the_current_attempt()
    {
        var sender = new OriginSender(0) { FailMutation = true };
        await FluentActions.Awaiting(() => Runner(sender).RunAsync([SeedData.English.TenantId]))
            .Should().ThrowAsync<InvalidOperationException>();
        sender.Requests.Select(request => request.GetType()).Should().Equal(
            typeof(GetTenantSeedStateQuery), typeof(AdoptSeedAdministrationOriginCommand));

        // A subsequent deliberate invocation starts from fresh ordinary observations.
        sender.FailMutation = false;
        sender.Requests.Clear();
        (await Runner(sender).RunAsync([SeedData.English.TenantId])).Should().Be(0);
        sender.Requests.Select(request => request.GetType()).Should().Equal(
            typeof(GetTenantSeedStateQuery), typeof(GetHostMappingSeedStateQuery));
    }

    private static SeedAdministrationOriginRunner Runner(OriginSender sender) =>
        new(_ => new ServiceCollection().AddSingleton<ISender>(sender).BuildServiceProvider());

    private sealed class OriginSender(int marker) : ISender
    {
        public int Marker { get; private set; } = marker;
        public bool MappingPresent { get; init; } = true;
        public bool FailMutation { get; set; }
        public List<object> Requests { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var tenant = SeedData.English;
            object response;
            if (request is GetTenantSeedStateQuery)
                response = Result.Ok(new SeedLookup<TenantSeedDto>(new(tenant.TenantId, tenant.Slug, tenant.DisplayName,
                    "Active", tenant.DefaultOrganization.OrganizationId, 7, [], AdoptedSeedOriginVersion: Marker)));
            else if (request is GetHostMappingSeedStateQuery mapping)
                response = Result.Ok(new SeedLookup<HostMappingSeedDto>(MappingPresent
                    ? new(mapping.Host, tenant.TenantId, null, true, true) : null));
            else if (request is AdoptSeedAdministrationOriginCommand)
            {
                Marker = 2;
                if (FailMutation) throw new InvalidOperationException("Uncertain completion fixture.");
                response = Result.Ok(8L);
            }
            else throw new InvalidOperationException("Unexpected origin runner request.");
            return Task.FromResult((TResponse)response);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
