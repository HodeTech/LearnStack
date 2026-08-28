using System.Collections.ObjectModel;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// The one sanctioned way to register a module <c>DbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddDbContext&lt;T&gt;(o =&gt; o.UseNpgsql(connectionString))</c> — the EF
/// default — gives the context its own connection, and a context on its own
/// connection never saw <c>SET LOCAL app.tenant_id</c>. Under the corrected Row
/// Level Security policy every read through it returns zero rows, silently
/// (<see href="../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>).
/// This helper builds every context on the connection <see cref="IUnitOfWork"/>
/// owns and enlists it in the ambient transaction.
/// </para>
/// <para>
/// <c>Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork</c> is the guard, and it
/// reads <see cref="RegisteredContexts"/> off the <see cref="IServiceCollection"/>
/// it just built: a context registered any other way is absent from that set,
/// which is what the rule asserts against.
/// </para>
/// <para>
/// <b>EF issues its own savepoints, and that is left on.</b> A context enlisted in
/// an externally supplied transaction wraps every <c>SaveChangesAsync</c> in a
/// real <c>SAVEPOINT</c> / <c>RELEASE SAVEPOINT</c>, so a failed save rolls back
/// to its own savepoint and leaves the ambient transaction usable. ADR-0040's
/// "frames, not savepoints" describes the unit of work's in-process depth counter,
/// not the connection — the two mechanisms are independent and both are wanted.
/// </para>
/// </remarks>
public static class ModuleDbContextRegistration
{
    /// <summary>
    /// The context types registered through
    /// <see cref="AddModuleDbContext{TContext}"/> <b>on this collection</b>.
    /// </summary>
    /// <remarks>
    /// Per-collection, not process-wide. A static set is an assertion about the
    /// whole process rather than about the container the caller just built, so
    /// once anything anywhere had registered a context correctly, the rule
    /// vouched for the same type registered any other way in any later container
    /// — and the shape it vouched for is exactly the ADR-0040 failure: a scoped
    /// <c>ImplementationFactory</c> building the context on its own connection
    /// string, which the rule's independent lifetime leg cannot tell apart from
    /// this helper's.
    /// </remarks>
    public static IReadOnlyCollection<Type> RegisteredContexts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return new ReadOnlyCollection<Type>([.. Marker(services).Contexts]);
    }

    /// <summary>
    /// The per-collection record of what this helper registered, carried in the
    /// collection itself so it travels with the container rather than the process.
    /// </summary>
    private sealed class RegistrationMarker
    {
        public HashSet<Type> Contexts { get; } = [];
    }

    private static RegistrationMarker Marker(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(
            descriptor => descriptor.ServiceType == typeof(RegistrationMarker));

        if (existing?.ImplementationInstance is RegistrationMarker marker)
        {
            return marker;
        }

        marker = new RegistrationMarker();
        services.AddSingleton(marker);

        return marker;
    }

    /// <summary>
    /// Registers <typeparamref name="TContext"/> scoped, built on the ambient
    /// connection and enlisted in the ambient transaction.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        // Both or neither. TryAddScoped is a no-op when something already
        // registered TContext, and recording it anyway would make
        // Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork report a context this
        // helper did not build — which is precisely the case the rule exists to
        // catch: an AddDbContext registration that got there first, still holding
        // its own connection, with the marker set vouching for it.
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TContext)))
        {
            return services;
        }

        Marker(services).Contexts.Add(typeof(TContext));

        services.TryAddScoped(provider =>
        {
            var unitOfWork = provider.GetRequiredService<IUnitOfWork>();

            if (unitOfWork.Transaction is null)
            {
                // Fail loud rather than fail silent. Without a transaction the
                // context would still work — it would read through the shared
                // connection in autocommit, with no SET LOCAL, and return zero
                // rows from every tenant-owned table. That is the exact failure
                // ADR-0040 exists to prevent, and it is indistinguishable from
                // "there is no data" at the call site.
                throw new InvalidOperationException(
                    $"{typeof(TContext).Name} was resolved outside the ambient transaction. "
                    + "TransactionBehavior opens it at step 6 of the MediatR pipeline, and the "
                    + "event transport opens it per delivery; a context resolved before either "
                    + "reads zero rows from every tenant-owned table because it never saw "
                    + "SET LOCAL app.tenant_id.");
            }

            var options = new DbContextOptionsBuilder<TContext>()
                // The connection, not a connection string. contextOwnsConnection
                // is false by this overload's contract, so disposing the context
                // does not return the connection to the pool underneath its
                // siblings — IUnitOfWork is the sole owner.
                .UseNpgsql(unitOfWork.Connection)
                // Without this EF has no ILoggerFactory to resolve, and every
                // Microsoft.EntityFrameworkCore log category is silent for every
                // context this helper registers — on a seam whose whole premise is
                // that a misconfigured context fails invisibly. Measured: the
                // model-validation warnings and the command-error lines carrying
                // the failing SQL both disappear. It is also what lets a
                // DI-registered interceptor be found, which Packet 9 needs.
                .UseApplicationServiceProvider(provider)
                .Options;

            var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
            context.Database.UseTransaction(unitOfWork.Transaction);

            return context;
        });

        return services;
    }
}
