using System.Reflection;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// Every request handler the backend ships resolves from the API's own container.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the host already checks, and what it cannot.</b> Two composition roots build the
/// handler graph — the API and the seeder — and the publish handlers gained a dependency,
/// <c>IAuditSubject</c>, in the fix for a review finding. A registration missing from the API
/// root is caught when the host builds: the Development environment validates the container,
/// so every host-based case would fail — measured, by deleting the line. What validation cannot
/// do is <em>run</em> anything: it checks the graph statically and never invokes a factory, and
/// this solution registers its audit ports, its data sources and its module contexts through
/// factories that do real work — a module context refuses to exist outside an open unit of
/// work, a data source refuses a missing credential.
/// </para>
/// <para>
/// So every closed <c>IRequestHandler&lt;,&gt;</c> a backend assembly implements is
/// <b>resolved</b>, inside an open unit of work on the shared schema, from the API's own
/// container: a factory that throws on the way to any handler is a red build rather than the
/// first request's <c>500</c>. Discovered, not listed; the transaction is never committed.
/// </para>
/// </remarks>
[Trait(Database.RequiresDocker.Key, Database.RequiresDocker.Value)]
[Collection(Database.SharedSchema.Name)]
public sealed class ApiHandlerCompositionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Database.SchemaFixture _schema;

    public ApiHandlerCompositionTests(WebApplicationFactory<Program> factory, Database.SchemaFixture schema)
    {
        _factory = factory;
        _schema = schema;
    }

    [Fact]
    public async Task Every_Shipped_Request_Handler_Resolves_From_The_Api_Container()
    {
        var contracts = HandlerContracts();

        contracts.Should().Contain(contract => contract.GenericTypeArguments[0].Name == "PublishTenantContentTypeCommand",
            "a discovery that found no handler would pass while proving nothing");

        // The runtime role, against the shared schema: a module context is resolved only
        // inside an open unit of work, so the case opens one and never commits it.
        await using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting(
            "ConnectionStrings:Default", _schema.Postgres.AppConnectionString));
        await using var scope = host.Services.CreateAsyncScope();
        await using var transaction = await scope.ServiceProvider
            .GetRequiredService<LearnStack.SharedKernel.Persistence.IUnitOfWork>()
            .BeginTransactionAsync();
        var unresolved = new List<string>();

        foreach (var contract in contracts)
        {
            try
            {
                if (scope.ServiceProvider.GetService(contract) is null)
                {
                    unresolved.Add($"{contract.GenericTypeArguments[0].Name}: not registered");
                }
            }
            catch (InvalidOperationException missing)
            {
                unresolved.Add($"{contract.GenericTypeArguments[0].Name}: {missing.Message}");
            }
        }

        unresolved.Should().BeEmpty(
            "a handler whose dependencies the API does not register fails its first request "
            + "rather than the build");
    }

    /// <summary>Every closed request-handler contract a backend assembly implements.</summary>
    private static List<Type> HandlerContracts() =>
        [.. Directory
            .EnumerateFiles(BackendSrc(), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)))
            .SelectMany(LoadableTypes)
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                && (contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                    || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<>)))
            .Distinct()];

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.OfType<Type>();
        }
    }

    private static string BackendSrc()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LearnStack.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("LearnStack.slnx was not found above the test binaries."),
            "src");
    }
}
