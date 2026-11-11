using System.Collections.Immutable;

namespace LearnStack.SharedKernel.Identity;

/// <summary>Bounded retained account history from one composition-qualified identity source.</summary>
public interface IIdentityEventProvider
{
    Task<IdentityEventSourcePolicy> ReadPolicyAsync(CancellationToken cancellationToken = default);
    Task<IdentityEventPage> ReadAsync(IdentityEventRead request, CancellationToken cancellationToken = default);
}

public sealed record IdentityEventSourcePolicy
{
    public IdentityEventSourcePolicy(int retentionSeconds)
    {
        if (retentionSeconds is < 604800 or > 2592000)
            throw new ArgumentOutOfRangeException(nameof(retentionSeconds));
        RetentionSeconds = retentionSeconds;
    }
    public int RetentionSeconds { get; }
}

public sealed record IdentityEventRead
{
    public IdentityEventRead(DateTimeOffset from, DateTimeOffset to, int offset, int maximum = 100)
    {
        if (from.Offset != TimeSpan.Zero || to.Offset != TimeSpan.Zero || from > to
            || offset < 0 || maximum is < 1 or > 100 || offset > int.MaxValue - maximum)
            throw new ArgumentException("Identity event range is invalid.");
        From = from; To = to; Offset = offset; Maximum = maximum;
    }
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    public int Offset { get; }
    public int Maximum { get; }
    public override string ToString() => nameof(IdentityEventRead);
}

public enum IdentityEventPageIssue { Malformed, Oversized }
public enum IdentityEventItemDisposition { Fact, Unselected, Malformed }

/// <summary>One source position; invalid and ignored records still occupy their original offset.</summary>
public sealed record IdentityEventReadItem
{
    private IdentityEventReadItem(IdentityEventItemDisposition disposition, IdentityProviderEvent? fact)
    { Disposition = disposition; Fact = fact; }
    public IdentityEventItemDisposition Disposition { get; }
    public IdentityProviderEvent? Fact { get; }
    public static IdentityEventReadItem Observed(IdentityProviderEvent fact) =>
        new(IdentityEventItemDisposition.Fact, fact ?? throw new ArgumentNullException(nameof(fact)));
    public static IdentityEventReadItem Unselected { get; } = new(IdentityEventItemDisposition.Unselected, null);
    public static IdentityEventReadItem Malformed { get; } = new(IdentityEventItemDisposition.Malformed, null);
    public override string ToString() => nameof(IdentityEventReadItem);
}

/// <summary>No raw response, error text, IP, user name or provider detail dictionary crosses this port.</summary>
public sealed record IdentityEventPage
{
    private IdentityEventPage(ImmutableArray<IdentityEventReadItem> items, IdentityEventPageIssue? issue)
    { Items = items; Issue = issue; }
    public ImmutableArray<IdentityEventReadItem> Items { get; }
    public IdentityEventPageIssue? Issue { get; }
    public static IdentityEventPage Read(IEnumerable<IdentityEventReadItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var bounded = items.Take(101).ToImmutableArray();
        if (bounded.Length > 100 || bounded.Any(item => item is null))
            throw new ArgumentException("Identity event page is invalid.", nameof(items));
        return new(bounded, null);
    }
    public static IdentityEventPage Unreadable(IdentityEventPageIssue issue) => Enum.IsDefined(issue)
        ? new([], issue) : throw new ArgumentOutOfRangeException(nameof(issue));
    public override string ToString() => nameof(IdentityEventPage);
}
