using System.Text.Json;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>Choose the returned prefix before its keyset cursor, within ADR-0079's 256 KiB response bound.</summary>
internal static class OwnTenantNavigationPageBudget
{
    private const int MaximumResponseBytes = 256 * 1024;

    // Page envelope, PageInfo, and its at-most-1024-character ASCII continuation.
    // The default JSON encoder measures escaped labels, not their UTF-16 length.
    private const int EnvelopeBytes = 2048;

    // Transport changes EntryHost to entryOrigin (+2), adds https:// and a
    // five-digit port (+14), canUseCurrentHost:false (+26), and an item comma.
    // 64 covers these additions without making this module depend on API DTOs.
    private const int TransportBytesPerItem = 64;

    internal static OwnTenantNavigationItem[] Select(IReadOnlyList<OwnTenantNavigationItem> items, int limit)
    {
        var remaining = MaximumResponseBytes - EnvelopeBytes;
        var count = 0;
        foreach (var item in items.Take(limit))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(item).Length + TransportBytesPerItem;
            if (bytes > remaining) break;
            remaining -= bytes;
            count++;
        }
        // Valid 200-character labels and canonical hosts always fit individually.
        // Refuse corrupt excess data instead of returning an empty nonprogressing page.
        if (items.Count != 0 && count == 0)
            throw new TenancyAdministrationStorageException(TenancyAdministrationStorageFailure.Provider);
        return items.Take(count).ToArray();
    }
}
