using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Test-only composition of the existing closed seed declaration and prerequisite
/// machinery. No human permission or production HTTP admission is asserted here.
/// </summary>
internal static class CanonicalSeedTenantFixture
{
    internal static Guid TenantId => SeedData.English.TenantId.Value;
    internal static ITenantContext Context => new SeedTenantContext(SeedData.English.TenantId, null);
    internal static ProvisionTenantCommand ProvisionCommand => new(SeedData.English.TenantId,
        SeedData.English.Slug, SeedData.English.DisplayName,
        SeedData.English.DefaultOrganization.OrganizationId, SeedData.English.DefaultOrganization.Slug,
        SeedData.English.DefaultOrganization.DisplayName);
    internal static CreateOrganizationCommand OrganizationCommand => new(
        SeedData.English.SecondOrganization.OrganizationId, SeedData.English.SecondOrganization.Slug,
        SeedData.English.SecondOrganization.DisplayName);

    internal static async Task ProvisionAsync(NpgsqlDataSource source)
    {
        await using var provider = SeedComposition.Build(source, null, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(ProvisionCommand);
        result.IsSuccess.Should().BeTrue("the real INSERT-first provision establishes the pristine prefix: {0}", result.Error);
    }

    internal static async Task PrepareContentInitializationAsync(NpgsqlDataSource source)
    {
        // The initializer uses ordinary reads and can join an existing owner. Its
        // complete declared Tenancy prefix is established by real, separately owned acts.
        await ProvisionAsync(source);
        await SendPrefixActAsync(source, OrganizationCommand);
        var tenant = SeedData.English;
        await SendPrefixActAsync(source, new MapHostToTenantCommand(tenant.Host,
            tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null, true, true));
        long version = 1;
        foreach (var locale in tenant.Curriculum!.Locales.OrderByDescending(value => value.IsDefault).ThenBy(value => value.Sort))
            await SendPrefixActAsync(source, new AddTenantLocaleCommand(version++, locale.Locale,
                locale.IsEnabled, locale.IsDefault, locale.Sort));
    }

    internal static async Task<ContentTypeSeedDto?> ReadBuiltInAsync(NpgsqlDataSource source)
    {
        await using var provider = SeedComposition.Build(source, Context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var active = await sender.Send(new GetActiveContentTypeSeedRevisionQuery(BuiltInCustomizations.Card.Key));
        active.IsSuccess.Should().BeTrue();
        if (active.Value!.State is not { } revision) return null;
        var row = await sender.Send(new GetContentTypeSeedStateQuery(revision.Id));
        row.IsSuccess.Should().BeTrue();
        return row.Value!.State;
    }

    private static async Task SendPrefixActAsync<T>(NpgsqlDataSource source, IRequest<Result<T>> request)
    {
        await using var provider = SeedComposition.Build(source, Context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
        result.IsSuccess.Should().BeTrue("the exact canonical prefix must pass its real guards: {0}", result.Error);
    }

    internal static void AddToComponentApi(IServiceCollection services, bool continuation = false)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationPolicySource, TenancyOperationPolicySource>());
        // Do not combine this with another source minting the same request. Slot 5
        // deliberately rejects multiple descriptors; its canonical policy is retained.
        var seedSource = SeederComponent("LearnStack.Tools.Seeder.SeedOperationSource",
            typeof(ITrustedOperationSource), typeof(ITenantContext));
        services.AddScoped<ITrustedOperationSource>(provider =>
            (ITrustedOperationSource)ConstructInScope(provider, seedSource));
        services.Replace(ServiceDescriptor.Singleton<ISeedTenantContinuationSource, FixedSeedTenantContinuationSource>());
        if (!continuation) return;
        var prerequisiteSource = SeederComponent("LearnStack.Tools.Seeder.FixedSeedPrerequisiteSource",
            typeof(SeedPrerequisiteReadWitness.MintingSource), typeof(IOperationInvocationContext),
            typeof(IOperationPolicyCatalog), typeof(ITenantContext), typeof(IUnitOfWork),
            typeof(IIdentityMutationLocks), typeof(IEnumerable<ITrustedOperationSource>));
        var verifier = SeederComponent("LearnStack.Tools.Seeder.FixedSeedPrerequisiteVerifier",
            typeof(IFixedSeedPrerequisiteVerifier), prerequisiteSource.Type, typeof(IUnitOfWork), typeof(IServiceProvider));
        var resolver = SeederComponent("LearnStack.Tools.Seeder.FixedSeedPrerequisiteReadOperationResolver",
            typeof(ISeedPrerequisiteReadOperationResolver), typeof(IServiceProvider));
        services.AddScoped(prerequisiteSource.Type, provider => ConstructInScope(provider, prerequisiteSource));
        services.Replace(ServiceDescriptor.Scoped<IFixedSeedPrerequisiteVerifier>(provider =>
            (IFixedSeedPrerequisiteVerifier)ConstructInScope(provider, verifier)));
        services.AddScoped<ISeedPrerequisiteReadOperationResolver>(provider =>
            (ISeedPrerequisiteReadOperationResolver)ConstructInScope(provider, resolver));
        services.AddScoped<IFixedSeedIdentityPrerequisiteReader, FixedSeedIdentityPrerequisiteReader>();
        // The API factory intentionally omits this Seeder-only optional resolver.
        // This component fixture wires it into the same real scoped physical owner.
        services.Replace(ServiceDescriptor.Scoped<IUnitOfWork>(provider => new NpgsqlUnitOfWork(
            provider.GetRequiredService<Lazy<NpgsqlDataSource>>().Value,
            provider.GetRequiredService<ILogger<NpgsqlUnitOfWork>>(),
            provider.GetRequiredService<GlobalIdentityEnrollment>(),
            provider.GetRequiredService<ISeedPrerequisiteReadOperationResolver>())));
    }

    private static (Type Type, ConstructorInfo Constructor) SeederComponent(
        string name, Type contract, params Type[] parameters)
    {
        var assembly = typeof(SeedComposition).Assembly;
        var type = assembly.GetType(name, throwOnError: true)!;
        var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (type.Assembly != assembly || !type.IsClass || !type.IsSealed || !type.IsNotPublic
            || !contract.IsAssignableFrom(type) || constructors.Length != 1
            || !constructors[0].GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameters))
            throw new InvalidOperationException("The exact test-only Seeder component shape changed: " + name);
        return (type, constructors[0]);
    }

    private static object ConstructInScope(IServiceProvider provider, (Type Type, ConstructorInfo Constructor) component)
    {
        // These are the original production implementations, borrowing this API
        // scope's context, source, invocation and physical owner. No Seeder root is captured.
        if (component.Constructor.IsPublic) return ActivatorUtilities.CreateInstance(provider, component.Type);
        return component.Constructor.Invoke(component.Constructor.GetParameters()
            .Select(parameter => provider.GetRequiredService(parameter.ParameterType)).ToArray());
    }
}
