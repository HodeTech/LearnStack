using System.Reflection;
using FluentAssertions;
using LearnStack.SharedKernel.Persistence;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The one sanctioned cross-aggregate write, and the count that keeps it at one.
/// </summary>
/// <remarks>
/// <see href="../../../docs/decisions/0042-tenant-provisioning-cross-aggregate-transaction.md">ADR-0042</see>
/// permits a single operation to write two aggregate roots on one transaction, by
/// enumeration rather than by principle: a tenant whose default organization failed to
/// commit is a tenant no request can serve, and a second transaction is a window in which
/// exactly that state exists. The value of the rule is that it counts the hole. A hole
/// nobody counts becomes a hole everybody uses.
/// </remarks>
public sealed class AggregateWriteTests
{
    [Fact]
    public void Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning()
    {
        // ADR-0042 sanctions ONE operation to write two aggregate roots in one
        // transaction, by enumeration rather than by principle — a tenant whose default
        // organization failed to commit is a tenant no request can serve, and a second
        // transaction is a window in which exactly that state exists.
        //
        // Counted by AGGREGATE TYPE, not by parameter and not by name. The catalogue
        // registered this as a scan for DbSet use in handlers, and under the shipped
        // dependency rules that scan can never fire: Application → Infrastructure is
        // forbidden, so no handler can name a DbSet at all. A rule at Implemented status
        // that cannot fire is worse than one at Registered, because the catalogue then
        // claims coverage it does not have.
        var offenders = ProductionAssemblies()
            .Select(Assembly.Load)
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(IsMessageHandler)
            .Where(type => type.GetConstructors(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(constructor => AggregatesWrittenBy(constructor).Count > 1))
            .Select(type => type.Name)
            .Distinct()
            .ToList();

        offenders.Should().BeEquivalentTo(
            ["ProvisionTenantCommandHandler"],
            "a handler that can write two aggregate roots writes across an aggregate "
            + "boundary in one transaction, which ADR-0042 sanctions for exactly one "
            + "operation — a second name here is a decision that needs its own record");
    }

    /// <summary>
    /// The distinct aggregate roots a constructor's write ports reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct <b>closed constructions</b> of <see cref="IAggregateWriteStore{TRoot,TId}"/>
    /// rather than a count of parameters, and the difference is an escape the first
    /// version of this rule had. Measured: an interface deriving from the generic twice —
    /// <c>IFused : IAggregateWriteStore&lt;Tenant, TenantId&gt;,
    /// IAggregateWriteStore&lt;Organization, OrganizationId&gt;</c> — is ONE constructor
    /// parameter, so a handler taking it wrote both roots and the rule stayed green.
    /// Counting what the ports reach makes the fused shape indistinguishable from the two
    /// it fuses, which is the point: the rule exists to see the write, not the wiring.
    /// </para>
    /// <para>
    /// Two parameters over the SAME root are not a cross-aggregate write and do not
    /// count twice — a handler holding one port for reading and one for writing is
    /// still writing one aggregate.
    /// </para>
    /// </remarks>
    private static HashSet<Type> AggregatesWrittenBy(ConstructorInfo constructor) =>
        [.. constructor.GetParameters()
            .SelectMany(parameter => WriteStoreConstructions(parameter.ParameterType))
            .Select(store => store.GetGenericArguments()[0])];

    private static IEnumerable<Type> WriteStoreConstructions(Type parameterType) =>
        parameterType.GetInterfaces()
            .Append(parameterType)
            .Where(candidate => candidate.IsGenericType
                && candidate.GetGenericTypeDefinition() == typeof(IAggregateWriteStore<,>));

    /// <summary>
    /// Whether the type handles a message on the MediatR pipeline.
    /// </summary>
    /// <remarks>
    /// <c>INotificationHandler</c> is in the set deliberately. An intra-module domain
    /// event is one of ADR-0010's four sanctioned mechanisms and its handler runs
    /// <b>inside the ambient transaction</b>, so a notification handler holding two write
    /// ports is the same cross-aggregate write as a command handler holding them — and
    /// the likelier of the two to be written by someone who did not read ADR-0042,
    /// because a domain-event handler does not look like a write boundary. Measured: with
    /// only <c>IRequestHandler&lt;,&gt;</c> in the set, one dropped into a production
    /// assembly passed all 76 architecture cases.
    /// </remarks>
    private static bool IsMessageHandler(Type type) =>
        type.GetInterfaces().Any(contract =>
            contract.IsGenericType
            && HandlerContracts.Contains(contract.GetGenericTypeDefinition()));

    private static readonly HashSet<Type> HandlerContracts =
    [
        typeof(IRequestHandler<,>),
        typeof(IRequestHandler<>),
        typeof(INotificationHandler<>),
    ];

    /// <summary>Every production assembly, by name, from the project files on disk.</summary>
    /// <remarks>
    /// Enumerated from the filesystem rather than from a literal list, for the reason
    /// <c>RequestSurfaceTests</c> does the same: a list is a thing an author forgets to
    /// grow, and a module added without its entry is a module the rule never scanned.
    /// </remarks>
    private static IEnumerable<string> ProductionAssemblies() =>
        Directory.EnumerateFiles(
                RepositoryPaths.BackendSrc(), "LearnStack.*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!);
}
