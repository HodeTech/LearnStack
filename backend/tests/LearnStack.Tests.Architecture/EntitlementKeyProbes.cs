using LearnStack.SharedKernel.Entitlements;

namespace LearnStack.Tests.Architecture.Probes;

/// <summary>
/// Invents entitlement keys the two ways IL can spell one, for
/// <c>The_Entitlement_Key_Scans_Can_Actually_Fail</c>. Never called.
/// </summary>
internal static class KeyInventorProbe
{
    /// <summary>A key spelled at the call site.</summary>
    public static FeatureKey Invent(string name) => new(name);

    /// <summary>A key copied from a registry member and renamed.</summary>
    public static KillswitchKey Rename(KillswitchKey key) => key with { Value = "killswitch.invented" };
}
