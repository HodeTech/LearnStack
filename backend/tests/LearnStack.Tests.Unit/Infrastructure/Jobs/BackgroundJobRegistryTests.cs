using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobRegistryTests
{
    private static JobModuleFixture Fixture => JobModuleFixture.Shared;

    [Fact]
    public void Empty_business_manifests_keep_nonempty_isolated_infrastructure()
    {
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        definitions.DefinitionKeys.Should().BeEmpty();
        definitions.All.Select(request => request.RequestType).Should().BeEquivalentTo(
            LearnStack.Application.Jobs.BackgroundJobFrameworkRequests.All.Select(request => request.RequestType));
        BackgroundJobRecurrenceRegistry.CreateEmpty().Keys.Should().BeEmpty();
        definitions.Infrastructure.Pools.Values.Select(pool => (pool.Kind, pool.Workers))
            .Should().BeEquivalentTo(new[] { (BackgroundJobWorkerPoolKind.Transactional, 1),
                (BackgroundJobWorkerPoolKind.External, 2), (BackgroundJobWorkerPoolKind.Control, 1) });
    }

    [Fact]
    public void Construction_free_registry_resolves_exact_readiness_and_explicit_tombstones()
    {
        var registry = BackgroundJobDefinitionRegistry.Create(
            [Fixture.Transactional(), Fixture.Transactional("jobsfixture.wait.v2", readiness: BackgroundJobDefinitionReadiness.Pending)],
            [Fixture.Ordinary("Root")], [Fixture.Module], ["jobsfixture.retired.v1"]);
        registry.Resolve("jobsfixture.run.v1").Disposition.Should().Be(BackgroundJobDefinitionDisposition.Active);
        registry.Resolve("jobsfixture.wait.v2").Disposition.Should().Be(BackgroundJobDefinitionDisposition.Pending);
        registry.Resolve("jobsfixture.future.v42").Disposition.Should().Be(BackgroundJobDefinitionDisposition.Unknown);
        registry.Resolve("jobsfixture.retired.v1").Disposition.Should().Be(BackgroundJobDefinitionDisposition.Retired);
        registry.Resolve("jobsfixture.future.v42").Definition.Should().BeNull();
        registry.Resolve(null).Disposition.Should().Be(BackgroundJobDefinitionDisposition.Invalid);
        registry.TryGet("jobsfixture.run.v1", out var definition).Should().BeTrue();
        definition!.OwnerModule.Should().Be("jobsfixture");
        definition.ParameterType.Should().Be(Fixture.Contract("Parameters"));
    }

    [Theory]
    [InlineData("jobsfixture.run.v0")]
    [InlineData("jobsfixture.run.v01")]
    [InlineData("Jobsfixture.run.v1")]
    [InlineData("jobsfixture.run-name.v1")]
    [InlineData("jobsfixture.rún.v1")]
    [InlineData("jobsfixture.run.v1\n")]
    [InlineData("jobsfixture.run.more.v1")]
    [InlineData("another.run.v1")]
    public void Invalid_or_foreign_definition_keys_refuse_registration(string key)
    {
        var action = () => Fixture.Registry(Fixture.Transactional(key));
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Definition_key_length_is_inclusive_and_names_admit_underscores()
    {
        var key = "jobsfixture." + new string('a', 135) + ".v1";
        key.Length.Should().Be(150);
        Fixture.Registry(Fixture.Transactional(key)).DefinitionKeys.Should().Equal(key);
        Fixture.Registry(Fixture.Transactional("jobsfixture.run_task.v1")).DefinitionKeys.Should().ContainSingle();
        var action = () => Fixture.Registry(Fixture.Transactional(key.Replace(".v1", "a.v1", StringComparison.Ordinal)));
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Duplicate_live_and_retired_identities_refuse()
    {
        var duplicate = () => Fixture.Registry(Fixture.Transactional(), Fixture.Transactional());
        duplicate.Should().Throw<ArgumentException>();
        var retired = () => BackgroundJobDefinitionRegistry.Create([Fixture.Transactional()], [Fixture.Ordinary("Root")],
            [Fixture.Module], ["jobsfixture.run.v1"]);
        retired.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("UnresolvedRoot")]
    [InlineData("PublicRoot")]
    [InlineData("ProvisioningRoot")]
    public void Inherent_request_authority_cannot_be_relabelled_ordinary(string root)
    {
        var action = () => Fixture.Registry(Fixture.Transactional(root: root));
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(BackgroundJobRequestClassification.Platform)]
    [InlineData(BackgroundJobRequestClassification.Privileged)]
    [InlineData(BackgroundJobRequestClassification.FrameworkReadOnly)]
    [InlineData(BackgroundJobRequestClassification.FrameworkReadWrite)]
    public void Definition_cannot_use_privileged_or_module_forged_framework_request(BackgroundJobRequestClassification classification)
    {
        var action = () => BackgroundJobDefinitionRegistry.Create([Fixture.Transactional()],
            [new(Fixture.Request("Root"), classification, BackgroundJobScope.TenantWide)], [Fixture.Module]);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Missing_scope_metadata_wrong_request_constructor_and_root_nested_overlap_refuse()
    {
        var missing = () => BackgroundJobDefinitionRegistry.Create([Fixture.Transactional()], [], [Fixture.Module]);
        missing.Should().Throw<ArgumentException>();
        var scope = () => BackgroundJobDefinitionRegistry.Create([Fixture.Transactional()],
            [Fixture.Ordinary("Root", BackgroundJobScope.Organization)], [Fixture.Module]);
        scope.Should().Throw<ArgumentException>();
        var constructor = () => Fixture.Registry(Fixture.Transactional(root: "WrongRoot"));
        constructor.Should().Throw<ArgumentException>();
        var overlap = () => Fixture.Registry(Fixture.Transactional(nested: [Fixture.Request("Root")]));
        overlap.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Declaration_lists_are_copied_before_the_registry_freezes_them()
    {
        var nested = new[] { Fixture.Request("Nested") };
        var registration = Fixture.Transactional(nested: nested);
        var registry = Fixture.Registry(registration);
        nested[0] = Fixture.Request("Finalize");
        registry.TryGet(registration.Key, out var definition).Should().BeTrue();
        definition!.NestedRequestTypes.Should().Equal(Fixture.Request("Nested"));
        var action = () => ((IList<Type>)definition.NestedRequestTypes)[0] = Fixture.Request("Finalize");
        action.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void External_profile_binds_typed_contracts_without_constructing_its_throwing_adapter()
    {
        var registry = Fixture.Registry(Fixture.External());
        registry.TryGet("jobsfixture.external.v1", out var definition).Should().BeTrue();
        definition!.RootRequestTypes.Should().Equal(Fixture.Request("Prepare"), Fixture.Request("Finalize"));
        definition.ProfileRegistration.ExternalPortType.Should().Be(Fixture.Contract("IOperation"));
        var missingCapacity = () => Fixture.Registry(Fixture.External(capacityGroup: null));
        missingCapacity.Should().Throw<ArgumentException>();
        var missingAdapter = () => BackgroundJobDefinitionRegistry.Create([Fixture.External()],
            [Fixture.Ordinary("Prepare"), Fixture.Ordinary("Finalize")], [Fixture.Module]);
        missingAdapter.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("IWrongOperation")]
    [InlineData("IInheritedWrongOperation")]
    public void External_ports_cannot_add_direct_or_inherited_provider_entries(string port)
    {
        var action = () => BackgroundJobInfrastructureRegistry.Create(
            BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values, [],
            [new(Fixture.Contract(port), Fixture.Adapter)], [Fixture.Module]);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Capacity_group_rejects_foreign_keys_soft_limits_and_undeclared_limits()
    {
        foreach (var group in new[]
        {
            new BackgroundJobCapacityGroupRegistration("other.resource", Fixture.Declaration),
            new BackgroundJobCapacityGroupRegistration("jobsfixture.resource", Fixture.Declaration, 0),
            new BackgroundJobCapacityGroupRegistration("jobsfixture.resource", Fixture.Declaration, 1025),
            new BackgroundJobCapacityGroupRegistration("jobsfixture.resource", Fixture.Declaration, planLimit: LimitKeys.ClassroomMinutesPerMonth),
            new BackgroundJobCapacityGroupRegistration("jobsfixture.resource", Fixture.Declaration, planLimit: new LimitKey("limits.unknown")),
        })
        {
            var action = () => BackgroundJobInfrastructureRegistry.Create(
                BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values, [group], [], [Fixture.Module]);
            action.Should().Throw<ArgumentException>();
        }
        var valid = BackgroundJobInfrastructureRegistry.Create(BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values,
            [new("jobsfixture.resource", Fixture.Declaration, 1024, LimitKeys.MaxUsers)], [], [Fixture.Module]);
        valid.CapacityGroups.Should().ContainKey("jobsfixture.resource");
    }

    [Fact]
    public void Pools_are_disjoint_bounded_and_cannot_route_external_work_to_transactional_slots()
    {
        var overlap = () => BackgroundJobInfrastructureRegistry.Create(
            [new("transactional", BackgroundJobWorkerPoolKind.Transactional, 1, ["same"]),
                new("external", BackgroundJobWorkerPoolKind.External, 2, ["same"]),
                new("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"])], [], [], []);
        overlap.Should().Throw<ArgumentException>();
        var tooManyWorkers = () => BackgroundJobInfrastructureRegistry.Create(
            [new("transactional", BackgroundJobWorkerPoolKind.Transactional, 16, ["transactional"]),
                new("external", BackgroundJobWorkerPoolKind.External, 16, ["external"]),
                new("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"])], [], [], []);
        tooManyWorkers.Should().Throw<ArgumentException>();
        var wrongLane = () => Fixture.Registry(Fixture.Transactional(queue: "external"));
        wrongLane.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void One_pool_cannot_exceed_sixteen_workers_even_below_the_total_worker_bound()
    {
        var pools = new[]
        {
            new BackgroundJobWorkerPoolRegistration("transactional", BackgroundJobWorkerPoolKind.Transactional, 16, ["transactional"]),
            new BackgroundJobWorkerPoolRegistration("external", BackgroundJobWorkerPoolKind.External, 1, ["external"]),
            new BackgroundJobWorkerPoolRegistration("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"]),
        };
        BackgroundJobInfrastructureRegistry.Create(pools, [], [], []).Pools["transactional"].Workers.Should().Be(16);
        pools[0] = new("transactional", BackgroundJobWorkerPoolKind.Transactional, 17, ["transactional"]);
        pools.Sum(pool => pool.Workers).Should().BeLessThanOrEqualTo(32);

        var create = () => BackgroundJobInfrastructureRegistry.Create(pools, [], [], []);

        create.Should().Throw<ArgumentException>().WithMessage("*1–16 workers*");
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("media-worker")]
    [InlineData("1worker")]
    [InlineData("pool\n")]
    [InlineData("")]
    public void Pool_and_queue_names_use_the_same_finite_ascii_grammar(string name)
    {
        var pool = () => BackgroundJobInfrastructureRegistry.Create(
            BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values.Append(
                new(name, BackgroundJobWorkerPoolKind.External, 1, ["extra"])), [], [], []);
        pool.Should().Throw<ArgumentException>();
        var queue = () => BackgroundJobInfrastructureRegistry.Create(
            BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values.Append(
                new("extra", BackgroundJobWorkerPoolKind.External, 1, [name])), [], [], []);
        queue.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Topology_accepts_the_exact_pool_queue_worker_and_name_bounds()
    {
        var pools = new List<BackgroundJobWorkerPoolRegistration>();
        for (var index = 0; index < 8; index++)
            pools.Add(new("pool_" + index, (BackgroundJobWorkerPoolKind)(index % 3), 4,
                Enumerable.Range(index * 8, 8).Select(queue => "queue_" + queue)));
        var maximum = BackgroundJobInfrastructureRegistry.Create(pools, [], [], []);
        maximum.Pools.Should().HaveCount(8);
        maximum.QueueNames.Should().HaveCount(64);
        var ninthPool = () => BackgroundJobInfrastructureRegistry.Create(pools.Append(
            new("extra", BackgroundJobWorkerPoolKind.External, 1, ["extra"])), [], [], []);
        ninthPool.Should().Throw<ArgumentException>();
        pools[0] = new("pool_0", BackgroundJobWorkerPoolKind.Transactional, 4,
            Enumerable.Range(0, 8).Select(queue => "queue_" + queue).Append("extra"));
        var extraQueue = () => BackgroundJobInfrastructureRegistry.Create(pools, [], [], []);
        extraQueue.Should().Throw<ArgumentException>();
        var name = new string('a', 50);
        BackgroundJobInfrastructureRegistry.Create(BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values.Append(
            new(name, BackgroundJobWorkerPoolKind.External, 1, [name])), [], [], []).Pools.Should().ContainKey(name);
        var tooLong = () => BackgroundJobInfrastructureRegistry.Create(
            BackgroundJobInfrastructureRegistry.CreateDefault().Pools.Values.Append(
                new(name + "a", BackgroundJobWorkerPoolKind.External, 1, [name])), [], [], []);
        tooLong.Should().Throw<ArgumentException>();
    }
}
