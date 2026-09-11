using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LearnStack.Infrastructure.Audit.Capture;

/// <summary>
/// Takes the before / after snapshot of every entity a <c>SaveChanges</c> is about to
/// write, and puts it in the request's <see cref="IAuditStateCapture"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It captures only.</b> It constructs no row, issues no SQL, and returns the
/// unmodified <see cref="InterceptionResult{TResult}"/>. Several flushes may occur
/// inside one transaction — <c>ProvisionTenantCommand</c> saves three times — which is
/// exactly why the row is composed later, once, by the frame that owns the commit.
/// </para>
/// <para>
/// <b>Every entity in the tracker, minus a named exclusion list.</b> The
/// <c>AuditableEntity&lt;&gt;</c> predicate an earlier draft used is withdrawn: seven
/// shipped entities carry no such base class, five of them MUST in the module matrices
/// (Audit Coverage § Required Behaviours lists them), and <c>PlatformHostMapping</c> — the
/// row that decides whose data an anonymous request sees — is the one the Tenancy matrix
/// singles out as mattering most
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 7</see>).
/// </para>
/// <para>
/// <b>It must be attached through <c>AddInterceptors</c> on the options.</b> Registering
/// an <see cref="ISaveChangesInterceptor"/> in DI alone does not attach it under this
/// repository's hand-built options shape — measured on EF Core 10, and the reason
/// <c>ModuleDbContextRegistration</c> resolves the interceptors from the provider and
/// passes them in explicitly.
/// </para>
/// <para>
/// <b>Two gates run inside the capture, before anything reaches the buffer</b>
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 8</see>):
/// the <see cref="PiiSensitiveAttribute"/> marker together with
/// <see cref="SensitiveTokenCatalog"/>'s name tokens, and the size cap. A sensitive
/// value is <em>replaced</em> rather than dropped, so the diff still records that it
/// changed — which is the fact a reviewer is usually asking about.
/// </para>
/// </remarks>
public sealed class AuditChangeTrackerInterceptor(IAuditStateCapture capture)
    : SaveChangesInterceptor
{
    /// <summary>
    /// Entity type names this interceptor never captures.
    /// </summary>
    /// <remarks>
    /// By name and with a reason each. <c>OutboxMessage</c>: its payload is the audited
    /// event and the row is machinery. <c>IdempotencyKey</c>: request plumbing.
    /// <c>AuditEntry</c> / <c>AuditConfig</c>: the audit tables themselves — which cannot
    /// arise anyway, because the store writes parameterised SQL and never a
    /// <c>DbContext</c>, so there is no re-entrancy path. They are listed regardless,
    /// because "cannot arise" is a property of today's store and the exclusion is a
    /// property of the design.
    /// </remarks>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "OutboxMessage",
        "IdempotencyKey",
        "AuditEntry",
        "AuditConfig",
    };

    /// <summary>
    /// Properties left out of the snapshot and the diff.
    /// </summary>
    /// <remarks>
    /// Bookkeeping the audit row already carries or does not want, named by
    /// <see href="../../../../docs/architecture/31-audit-subsystem.md">Audit Subsystem
    /// § 3</see>. <c>TenantId</c> is the row's own <c>tenant_id</c>, so repeating it in
    /// the snapshot says nothing; <c>CreatedAt</c>, <c>UpdatedAt</c> and <c>Version</c>
    /// move on every write, so a diff carrying them buries the property that actually
    /// changed under three that always do.
    /// <para>
    /// <b>These are EF model property names, not column names</b>, because
    /// <c>Describe</c> matches on <c>property.Metadata.Name</c>. The distinction is not
    /// pedantic: the concurrency token's column is <c>row_version</c> and its property is
    /// <c>Version</c>, and an entry spelled for the column excluded nothing at all.
    /// </para>
    /// <para>
    /// The soft-delete pair is deliberately <b>not</b> here. <c>DeletedAt</c> moving is
    /// the whole content of a soft delete, and the actor columns are who did it — both
    /// are the record rather than the bookkeeping around it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> NotSnapshotted = new(StringComparer.Ordinal)
    {
        "TenantId",
        "CreatedAt",
        "UpdatedAt",

        // `Version`, not `RowVersion`. The COLUMN is row_version — MapAuditColumns renames
        // it — but this set is matched against the EF model's property name, and
        // AuditableEntity<TId> declares `public long Version`. The entry read `RowVersion`
        // and therefore excluded nothing on any shipped aggregate: every diff carried the
        // concurrency token, which moves on every write, exactly the noise this set exists
        // to remove.
        "Version",
    };

    /// <summary>
    /// Whether a property is sensitive, keyed by the declaring type and property name.
    /// </summary>
    /// <remarks>
    /// Reflection per property per save would be paid on every audited write. The answer
    /// cannot change for the life of the process — an attribute is compile-time and the
    /// token list is a static — so it is computed once.
    /// </remarks>
    private static readonly ConcurrentDictionary<(Type, string), bool> SensitivityCache = new();

    /// <summary>How far containment is followed, in either direction.</summary>
    /// <remarks>
    /// A bound rather than a trust: a well-formed model cannot loop, because containment
    /// runs from a non-root to a different type and a root ends it. The shipped depth is
    /// one — a taxonomy and its bands, a tenant and its locales.
    /// </remarks>
    private const int MaxContainmentDepth = 8;

    /// <summary>Which relationship contains each entity type, if any. See <c>ContainmentOf</c>.</summary>
    private static readonly ConcurrentDictionary<IEntityType, IForeignKey?> ContainmentCache = new();

    /// <summary>Whether a CLR type is an <c>IAggregateRoot&lt;&gt;</c>.</summary>
    private static readonly ConcurrentDictionary<Type, bool> AggregateRootCache = new();

    /// <summary>
    /// Every entity this interceptor has seen <c>Added</c>, for the life of its scope — one
    /// request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An owner created in this request contains exactly what was tracked with it, so its
    /// collections are complete — and that is the <b>only</b> case in which this interceptor
    /// can know a collection is complete. It remembers the owner because EF does not:
    /// <c>IsLoaded</c> is <c>false</c> for a new entity and stays <c>false</c> after it is
    /// saved — measured on EF Core 10 — so provisioning's third flush, which modifies the
    /// tenant it created in the first, would otherwise lose the tenant's locales.
    /// </para>
    /// <para>
    /// <c>IsLoaded</c> cannot stand in for it in the other direction either. A filtered
    /// <c>Include</c> sets it to <c>true</c> over a partial collection — measured by the second
    /// review of Packet 9 against real PostgreSQL: a taxonomy loaded with one of its three bands
    /// persisted an <c>after_state</c> listing one band, on a table nothing can correct.
    /// </para>
    /// <para>
    /// An owner leaves this set when a member it was seen with leaves the tracker — see
    /// <see cref="_membersSeen"/>.
    /// </para>
    /// </remarks>
    private readonly HashSet<object> _created = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The members each owner in <see cref="_created"/> held at its last capture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Created in this request is complete only while the tracker still holds what the owner
    /// was created with. A persisted member that is detached — or a tracker cleared and the
    /// owner attached again alone — keeps its row, and the next capture would have recorded
    /// the owner as owning what remained. Measured by the third review of Packet 9 on
    /// PostgreSQL: three bands saved, two detached, the root renamed and saved again — the
    /// database kept three bands and the row's <c>after_state</c> listed one.
    /// </para>
    /// <para>
    /// A deleted member is not a loss, and needs no case of its own: the capture of the save
    /// that deletes it still holds it, and records the owner's members without it — EF
    /// detaches it only after that save.
    /// </para>
    /// </remarks>
    private readonly Dictionary<object, HashSet<object>> _membersSeen = new(ReferenceEqualityComparer.Instance);

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Capture(eventData.Context);

        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Capture(eventData.Context);

        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Snapshots every captured entity this context is about to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public because it is what this class does, not because a test needs it — though a
    /// test is the second caller. EF reaches it through the two <c>SavingChanges</c>
    /// hooks above; anything else that knows a <see cref="DbContext"/> is about to flush
    /// can reach it directly, and driving it that way needs no connection because a
    /// <c>ChangeTracker</c> does not have one. A <c>null</c> context is a no-op rather
    /// than a throw: EF passes one for a few diagnostic events, and an interceptor that
    /// threw there would fail a save for a reason unrelated to the save.
    /// </para>
    /// <para>
    /// <b>One capture per aggregate, not per entity.</b> An entity that is not an aggregate
    /// root and is reached through its root's navigation — a taxonomy's bands, a tenant's
    /// locales — is described inside the root it belongs to: in its diff, under a pointer
    /// through the navigation's name, and in its snapshot when the root was created in this
    /// request and has lost no member to the tracker since — the one case its membership is
    /// known to be complete. Captured on its own it
    /// carried a type name no intent declares, so the composer dropped it and the band
    /// labels a tenant authored never reached the row that recorded the taxonomy
    /// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment 6
    /// § 4</see>). A contained entity whose root is not tracked is still captured, on its
    /// own, rather than dropped here.
    /// </para>
    /// </remarks>
    public void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // BEFORE the save, which is the only moment both states exist: after it, EF has
        // accepted the changes and OriginalValues equals CurrentValues. Every tracked entry
        // and not only the changed ones, because the root a changed band belongs to may be
        // unchanged itself — and the root is what the row describes.
        var tracked = context.ChangeTracker.Entries()
            .Where(entry => !Excluded.Contains(entry.Metadata.ClrType.Name))
            .ToList();

        foreach (var added in tracked.Where(candidate => candidate.State == EntityState.Added))
        {
            _created.Add(added.Entity);
        }

        ForgetOwnersThatLostAMember(tracked);

        var scene = new Scene(tracked, _created);
        var subjects = new List<EntityEntry>();
        var described = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var entry in tracked)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var subject = RootOf(entry, tracked);

            if (described.Add(subject.Entity))
            {
                subjects.Add(subject);
            }
        }

        foreach (var subject in subjects)
        {
            capture.Add(Describe(subject, scene));
        }
    }

    /// <summary>
    /// Takes an owner out of <see cref="_created"/> once a member it was seen with has left
    /// the tracker, and records what every other owner holds now.
    /// </summary>
    /// <remarks>
    /// For the rest of the request: a member the tracker no longer holds can change or go
    /// without this interceptor seeing it, so the membership is unknown from then on — which
    /// is what a loaded owner's already reads as. Left out of both snapshots, never recorded
    /// as what remained; every change to a member is still in the diff
    /// (ADR-0044 Amendment 6 § 4).
    /// </remarks>
    private void ForgetOwnersThatLostAMember(List<EntityEntry> tracked)
    {
        var held = tracked.Select(entry => entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);

        foreach (var owner in tracked.Where(entry => _created.Contains(entry.Entity)))
        {
            var navigations = ContainmentsOf(owner.Metadata).ToList();

            if (navigations.Count == 0)
            {
                continue;
            }

            if (_membersSeen.TryGetValue(owner.Entity, out var seen) && !seen.IsSubsetOf(held))
            {
                _created.Remove(owner.Entity);
                _membersSeen.Remove(owner.Entity);

                continue;
            }

            _membersSeen[owner.Entity] = navigations
                .SelectMany(navigation => MembersThrough(owner, navigation, tracked, original: false))
                .Select(member => member.Entity)
                .ToHashSet(ReferenceEqualityComparer.Instance);
        }
    }

    private static CapturedEntityChange Describe(EntityEntry entry, Scene scene)
    {
        var type = entry.Metadata.ClrType;
        var id = KeyOf(entry);

        // An INSTANCE-qualified RFC 6901 pointer, because `changes` carries every capture of
        // the declared type and a publication captures two instances of it: the successor
        // activated and the incumbent retired. `/TenantContentType/Status` could not say
        // which of them moved (ADR-0044 Amendment 6 § 5).
        var pointer = "/" + Segment(type.Name) + (id is null ? string.Empty : "/" + Segment(id));

        var fields = new List<CapturedFieldChange>();
        CollectFields(entry, pointer, [], scene, fields, depth: 0);

        return new CapturedEntityChange(
            EntityType: type.Name,
            EntityId: id,
            BeforeJson: entry.State == EntityState.Added
                ? null
                : AuditJson.CapObject(Snapshot(entry, original: true, [], scene, depth: 0)),
            AfterJson: entry.State == EntityState.Deleted
                ? null
                : AuditJson.CapObject(Snapshot(entry, original: false, [], scene, depth: 0)),
            Fields: fields);
    }

    /// <summary>
    /// The entity as a JSON object: its snapshotted properties, then each navigation to
    /// the entities it contains.
    /// </summary>
    /// <param name="original">The prior state rather than the new one.</param>
    /// <param name="hidden">
    /// For a contained entity, the foreign-key properties that point at its owner. They
    /// repeat the owner's own key, which is already the path the entity sits under.
    /// </param>
    private static string Snapshot(
        EntityEntry entry,
        bool original,
        IReadOnlyList<IProperty> hidden,
        Scene scene,
        int depth)
    {
        var json = new StringBuilder("{");
        var first = true;

        foreach (var property in Snapshotted(entry, hidden))
        {
            AppendMember(json, ref first, property.Metadata.Name, Render(entry, property, original));
        }

        if (depth >= MaxContainmentDepth)
        {
            return json.Append('}').ToString();
        }

        foreach (var navigation in ContainmentsOf(entry.Metadata))
        {
            // Membership is recorded only where it is KNOWN, and it is known only for an owner
            // created in this request whose members the tracker still holds: every entity it
            // contains was tracked with it. For any other owner the tracker holds whatever the
            // load happened to bring — nothing without an Include, a subset with a filtered
            // one — and EF's IsLoaded says true for the second, so it proves nothing (ADR-0044
            // Amendment 6 § 4). Left out, the collection reads as unknown rather than as
            // "these are all the bands"; its members' own changes still travel in the diff,
            // under their pointers.
            if (!scene.Created.Contains(entry.Entity))
            {
                continue;
            }

            var members = MembersThrough(entry, navigation, scene.Tracked, original);
            var ownerKey = navigation.ForeignKey.Properties;

            if (!navigation.IsCollection)
            {
                AppendMember(json, ref first, navigation.Name, members.Count == 0
                    ? AuditJson.Null
                    : Snapshot(members[0], original, ownerKey, scene, depth + 1));

                continue;
            }

            // An object keyed by each member's own key rather than an array: a pointer into
            // an array is an index, and an index names a different band the moment one is
            // removed ahead of it.
            var set = new StringBuilder("{");
            var firstMember = true;

            foreach (var member in members
                .Select(member => (Key: LocalKeyOf(member, navigation.ForeignKey, original), Member: member))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                AppendMember(set, ref firstMember, member.Key,
                    Snapshot(member.Member, original, ownerKey, scene, depth + 1));
            }

            AppendMember(json, ref first, navigation.Name, set.Append('}').ToString());
        }

        return json.Append('}').ToString();
    }

    /// <summary>
    /// The per-property diff of an entity and of everything it contains, each under its
    /// pointer.
    /// </summary>
    private static void CollectFields(
        EntityEntry entry,
        string pointer,
        IReadOnlyList<IProperty> hidden,
        Scene scene,
        List<CapturedFieldChange> fields,
        int depth)
    {
        foreach (var property in Snapshotted(entry, hidden))
        {
            // Only what CHANGED, on a modify. On an insert and a delete every property
            // is the change, which is what makes the diff and the snapshot agree.
            if (entry.State is EntityState.Added or EntityState.Deleted
                || (entry.State == EntityState.Modified && property.IsModified))
            {
                fields.Add(new CapturedFieldChange(
                    pointer + "/" + Segment(property.Metadata.Name),
                    entry.State == EntityState.Added ? null : Render(entry, property, original: true),
                    entry.State == EntityState.Deleted ? null : Render(entry, property, original: false)));
            }
        }

        if (depth >= MaxContainmentDepth)
        {
            return;
        }

        foreach (var navigation in ContainmentsOf(entry.Metadata))
        {
            // Both memberships: a removed band is only in the prior one and an added band
            // only in the new one, and each is a change the row has to carry.
            var members = MembersThrough(entry, navigation, scene.Tracked, original: false)
                .Union<EntityEntry>(MembersThrough(entry, navigation, scene.Tracked, original: true), ReferenceEqualityComparer.Instance)
                .Select(member => (
                    Key: LocalKeyOf(member, navigation.ForeignKey, original: member.State == EntityState.Deleted),
                    Member: member))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal);

            foreach (var (key, member) in members)
            {
                var at = pointer + "/" + Segment(navigation.Name)
                    + (navigation.IsCollection ? "/" + Segment(key) : string.Empty);

                CollectFields(member, at, navigation.ForeignKey.Properties, scene, fields, depth + 1);
            }
        }
    }

    private static IEnumerable<PropertyEntry> Snapshotted(EntityEntry entry, IReadOnlyList<IProperty> hidden) =>
        entry.Properties
            .Where(property => !NotSnapshotted.Contains(property.Metadata.Name)
                && !hidden.Contains(property.Metadata))
            .OrderBy(property => property.Metadata.Name, StringComparer.Ordinal);

    /// <summary>One property's value as JSON text, through both gates.</summary>
    private static string Render(EntityEntry entry, PropertyEntry property, bool original)
    {
        var metadata = property.Metadata;

        // Whether the COLUMN is jsonb, read from the model — the only place the answer is
        // knowable, and never guessed from the value.
        var storedAsJson = string.Equals(
            metadata.GetColumnType(), "jsonb", StringComparison.OrdinalIgnoreCase);

        // The converter, so the snapshot records WHAT THE COLUMN HOLDS rather than what the
        // CLR object looks like. `Property.CurrentValue` is the model value, and for a
        // converted property the two are different objects with different shapes —
        // measured, and the difference is not cosmetic: a `LocalizedText` display name
        // stores {"en":"Vocabulary Card","tr":"Kelime Kartı"} and serialises from its CLR
        // side as {"Locales":["en","tr"]}, which records which languages exist and none of
        // the text. Every display name in Customization — the tenant-authored values that
        // module exists for — would have been audited as a list of locale codes, and the
        // row is append-only so nothing could recover the words afterwards.
        var value = Stored(
            metadata.GetValueConverter(),
            original ? property.OriginalValue : property.CurrentValue);

        return IsSensitive(entry.Metadata.ClrType, metadata.Name)
            ? AuditJson.Quote(SensitiveTokenCatalog.RedactedValue)
            : AuditJson.Render(value, storedAsJson);
    }

    private static void AppendMember(StringBuilder json, ref bool first, string name, string value)
    {
        if (!first)
        {
            json.Append(',');
        }

        first = false;
        json.Append(AuditJson.Quote(name)).Append(':').Append(value);
    }

    /// <summary>One RFC 6901 reference token: <c>~</c> as <c>~0</c>, then <c>/</c> as <c>~1</c>.</summary>
    private static string Segment(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>
    /// The foreign key through which <paramref name="type"/> is contained, or <c>null</c>
    /// when it is an aggregate in its own right.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from the model rather than declared a second time: the type is <b>not</b> an
    /// <c>IAggregateRoot&lt;&gt;</c>, and exactly one relationship reaches it through a
    /// navigation on its principal — the <c>HasMany(t =&gt; t.Items)</c> each module already
    /// writes to express the aggregate boundary for EF. A root is never contained, however
    /// its relationships run; a type two owners reach is contained by neither, and is
    /// captured on its own.
    /// </para>
    /// <para>
    /// The model is fixed for the life of a <c>DbContext</c> type, so the answer is cached
    /// per entity type.
    /// </para>
    /// </remarks>
    private static IForeignKey? ContainmentOf(IEntityType type) =>
        ContainmentCache.GetOrAdd(type, static entityType =>
        {
            if (IsAggregateRoot(entityType.ClrType))
            {
                return null;
            }

            IForeignKey? containment = null;

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                if (foreignKey.PrincipalToDependent is null
                    || foreignKey.PrincipalEntityType.ClrType == entityType.ClrType)
                {
                    continue;
                }

                if (containment is not null)
                {
                    return null;
                }

                containment = foreignKey;
            }

            return containment;
        });

    private static IEnumerable<INavigation> ContainmentsOf(IEntityType type) =>
        type.GetNavigations()
            .Where(navigation => !navigation.IsOnDependent
                && ReferenceEquals(ContainmentOf(navigation.TargetEntityType), navigation.ForeignKey))
            .OrderBy(navigation => navigation.Name, StringComparer.Ordinal);

    private static bool IsAggregateRoot(Type type) =>
        AggregateRootCache.GetOrAdd(type, static clrType => clrType.GetInterfaces().Any(contract =>
            contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAggregateRoot<>)));

    /// <summary>The root an entity belongs to, or the entity itself when it is one.</summary>
    private static EntityEntry RootOf(EntityEntry entry, List<EntityEntry> tracked)
    {
        var current = entry;

        // Bounded, though a well-formed model cannot loop: containment runs from a non-root
        // to a different type, and a root ends the walk.
        for (var depth = 0; depth < MaxContainmentDepth; depth++)
        {
            var containment = ContainmentOf(current.Metadata);
            var owner = containment is null ? null : OwnerOf(current, containment, tracked);

            if (owner is null)
            {
                return current;
            }

            current = owner;
        }

        return current;
    }

    private static EntityEntry? OwnerOf(
        EntityEntry member, IForeignKey containment, List<EntityEntry> tracked)
    {
        // A deleted member's foreign key is its ORIGINAL one: that is the owner it was
        // removed from.
        var reference = Values(member, containment.Properties, member.State == EntityState.Deleted);

        return tracked.FirstOrDefault(candidate =>
            containment.PrincipalEntityType.ClrType.IsAssignableFrom(candidate.Metadata.ClrType)
            && reference.SequenceEqual(Values(candidate, containment.PrincipalKey.Properties, original: false)));
    }

    /// <summary>
    /// The tracked entities an owner contains through one navigation, in the prior state
    /// or the new one.
    /// </summary>
    private static List<EntityEntry> MembersThrough(
        EntityEntry owner, INavigation navigation, List<EntityEntry> tracked, bool original)
    {
        var key = Values(owner, navigation.ForeignKey.PrincipalKey.Properties, original);

        return [.. tracked.Where(candidate =>
            navigation.TargetEntityType.ClrType.IsAssignableFrom(candidate.Metadata.ClrType)
            && (original
                ? candidate.State != EntityState.Added
                : candidate.State != EntityState.Deleted)
            && key.SequenceEqual(Values(candidate, navigation.ForeignKey.Properties, original)))];
    }

    private static object?[] Values(EntityEntry entry, IReadOnlyList<IProperty> properties, bool original) =>
        [.. properties.Select(property => original
            ? entry.Property(property.Name).OriginalValue
            : entry.Property(property.Name).CurrentValue)];

    /// <summary>
    /// A contained entity's key within its owner: its primary key minus the columns that
    /// point at the owner — a band's <c>b2</c> rather than the four-column key it is stored
    /// under.
    /// </summary>
    /// <remarks>
    /// The whole key when nothing is left, and the same converter-then-text rendering
    /// <see cref="KeyOf"/> uses, so one key is never spelled two ways.
    /// </remarks>
    private static string LocalKeyOf(EntityEntry member, IForeignKey containment, bool original)
    {
        var key = member.Metadata.FindPrimaryKey()?.Properties ?? [];
        var local = key.Where(property => !containment.Properties.Contains(property)).ToList();

        return string.Join('/', (local.Count == 0 ? key : local).Select(property => Stored(
            property.GetValueConverter(),
            original
                ? member.Property(property.Name).OriginalValue
                : member.Property(property.Name).CurrentValue)?.ToString() ?? string.Empty));
    }

    /// <summary>
    /// The value as the column holds it, through the property's converter when it has one.
    /// </summary>
    /// <remarks>
    /// Three shipped shapes go through here and each lands right only because of it: a
    /// Vogen identifier becomes its <c>Guid</c>, an enum mapped by
    /// <c>HasEnumAsText()</c> becomes the member name the column stores, and a
    /// <c>LocalizedText</c> becomes the JSON document its <c>jsonb</c> column holds. An
    /// audit row that recorded the CLR side would disagree with the table it describes,
    /// and a reader has no way to tell which of the two is the record.
    /// </remarks>
    private static object? Stored(ValueConverter? converter, object? value) =>
        converter is null || value is null ? value : converter.ConvertToProvider(value);

    /// <summary>
    /// The entity's primary key rendered as text, or <c>null</c> for a keyless shape.
    /// </summary>
    /// <remarks>
    /// A composite key is joined with <c>/</c> in key order —
    /// <c>tenant_level_taxonomy_items</c> is the shipped four-column case — and escaped as
    /// <c>~1</c> where it becomes one segment of a pointer. A Guid key renders as the
    /// Guid's own <c>ToString()</c>, which is the text <see cref="AuditStateCapture"/>
    /// renders a designated subject as; the two are compared as text.
    /// </remarks>
    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();

        if (key is null)
        {
            return null;
        }

        // Through the SAME converter every other field goes through. entity_id and the
        // row's own key column must not be able to disagree, and they would the moment a
        // key carried a converter whose provider text differs from the model value's
        // ToString() — a non-Guid Vogen id, a normalising converter. Every shipped key
        // renders identically either way today, which is exactly why the inconsistency
        // would have gone unnoticed until it did not.
        var parts = key.Properties
            .Select(property => Stored(
                property.GetValueConverter(),
                entry.Property(property.Name).CurrentValue)?.ToString() ?? string.Empty)
            .ToList();

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static bool IsSensitive(Type declaringType, string propertyName) =>
        SensitivityCache.GetOrAdd(
            (declaringType, propertyName),
            static key =>
            {
                var (type, name) = key;

                // The marker first, because it is the explicit statement and the cheaper
                // lookup. The token list runs beside it rather than instead of it: the
                // list catches what nobody marked, the marker catches what the list's
                // tokens do not name.
                return IsMarked(type, name) || SensitiveTokenCatalog.IsSensitive(name);
            });

    /// <summary>
    /// Whether the property carries <see cref="PiiSensitiveAttribute"/>, wherever in the
    /// hierarchy it was declared.
    /// </summary>
    /// <remarks>
    /// The walk is not decoration. <c>Type.GetProperty</c> with
    /// <c>BindingFlags.NonPublic</c> searches the given type only — a <b>private</b>
    /// property declared on a base class is invisible to it, and this repository's
    /// aggregates put exactly that shape on <c>AuditableEntity&lt;TId&gt;</c>. A marker
    /// the lookup cannot see is a marker that silently does nothing, which is the worst
    /// failure a redaction gate has: it reports success and writes the value.
    /// <c>Inherited = true</c> on the attribute does not help, because the problem is
    /// finding the <c>PropertyInfo</c> rather than reading its attributes.
    /// </remarks>
    private static bool IsMarked(Type? type, string name)
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, Flags);

            if (property?.GetCustomAttribute<PiiSensitiveAttribute>() is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What one <c>Capture</c> call sees: every tracked entry, and the owners created in this request.</summary>
    private sealed record Scene(List<EntityEntry> Tracked, IReadOnlySet<object> Created);
}
