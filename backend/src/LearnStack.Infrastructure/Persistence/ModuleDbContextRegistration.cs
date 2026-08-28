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
/// reads <see cref="RegisteredContexts"/>: a context registered any other way is
/// absent from that set, which is what the rule asserts against.
/// </para>
/// </remarks>
public static class ModuleDbContextRegistration
{
    private static readonly HashSet<Type> Registered = [];

    /// <summary>
    /// Every context type registered through <see cref="AddModuleDbContext{TContext}"/>.
    /// </summary>
    /// <remarks>
    /// Process-wide rather than per-container, because the rule that reads it is
    /// a structural assertion about the composition root, and a test that built
    /// its own container would otherwise assert about that container instead.
    /// </remarks>
    public static IReadOnlyCollection<Type> RegisteredContexts =>
        new ReadOnlyCollection<Type>([.. Registered]);

    /// <summary>
    /// Registers <typeparamref name="TContext"/> scoped, built on the ambient
    /// connection and enlisted in the ambient transaction.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);

        lock (Registered)
        {
            Registered.Add(typeof(TContext));
        }

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
                .Options;

            var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
            context.Database.UseTransaction(unitOfWork.Transaction);

            return context;
        });

        return services;
    }
}
