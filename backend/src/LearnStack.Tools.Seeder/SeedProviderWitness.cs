using System.Text.Json;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Tools.Seeder;

/// <summary>Bounded private provider facts supplied by the external development lock owner.</summary>
/// <remarks>Facts are not authority; the fixed seed admission separately checks every declaration.</remarks>
public sealed class SeedProviderWitness
{
    private SeedProviderWitness(IdentityAuthorityRegistration registration, IReadOnlyDictionary<UserId, GlobalIdentitySubject> subjects)
    {
        Registration = registration;
        Subjects = subjects;
    }

    public IdentityAuthorityRegistration Registration { get; }
    public IReadOnlyDictionary<UserId, GlobalIdentitySubject> Subjects { get; }

    public static async Task<SeedProviderWitness> ReadAsync(Stream input,
        IdentityAuthorityRegistration registration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(registration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = new byte[8193];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await input.ReadAsync(bytes.AsMemory(length), deadline.Token).AsTask().WaitAsync(deadline.Token);
            if (count == 0) break;
            length += count;
        }
        if (length is 0 or > 8192) throw Refused();
        try
        {
            using var json = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            ExactMembers(root, "version", "issuer", "users");
            if (!root.GetProperty("version").TryGetInt32(out var version) || version != 1
                || root.GetProperty("issuer").GetString() != registration.Issuer
                || root.GetProperty("users").ValueKind != JsonValueKind.Array
                || root.GetProperty("users").GetArrayLength() != 4) throw Refused();
            var subjects = new Dictionary<UserId, GlobalIdentitySubject>();
            var providerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in root.GetProperty("users").EnumerateArray())
            {
                ExactMembers(entry, "actorId", "opaqueSub");
                if (!entry.GetProperty("actorId").TryGetGuid(out var actorValue)) throw Refused();
                var actor = UserId.From(actorValue);
                var subject = entry.GetProperty("opaqueSub").GetString();
                if (!SeedData.IdentityUsers.Any(person => person.UserId == actor)
                    || string.IsNullOrWhiteSpace(subject) || subject.Length > 255
                    || !providerIds.Add(subject)
                    || !subjects.TryAdd(actor, new GlobalIdentitySubject(registration.AuthorityId, registration.Issuer, subject)))
                    throw Refused();
            }
            if (subjects.Count != SeedData.IdentityUsers.Length) throw Refused();
            return new(registration, subjects.AsReadOnly());
        }
        catch (JsonException) { throw Refused(); }
        catch (InvalidOperationException) { throw Refused(); }
        catch (FormatException) { throw Refused(); }
        catch (ArgumentException) { throw Refused(); }
    }

    private static void ExactMembers(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Refused();
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || !actual.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)))
            throw Refused();
    }

    public override string ToString() => nameof(SeedProviderWitness);
    private static InvalidOperationException Refused() => new("Development identity witness was refused.");
}
