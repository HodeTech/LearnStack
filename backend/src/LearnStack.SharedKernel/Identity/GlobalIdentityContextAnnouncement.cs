namespace LearnStack.SharedKernel.Identity;

/// <summary>Read-only evidence for the exact descriptor announced on one physical transaction generation.</summary>
public sealed record GlobalIdentityContextAnnouncement(long Generation, GlobalIdentityOwnerDescriptor Descriptor);

/// <summary>Scoped read-only view used by context-instance EF self filters.</summary>
public interface IGlobalIdentityScopeAccessor
{
    GlobalIdentityContextAnnouncement? Current { get; }
}
