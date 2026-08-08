namespace LearnStack.SharedKernel.Hosting;

/// <summary>
/// The deployment shape the composition root branches on per
/// <see href="../../../docs/decisions/0020-triple-deployment-hybrid-license.md">ADR-0020</see>
/// and the
/// <see href="../../../docs/standards/20-infrastructure-stack.md">Standards 20 § Composition
/// Root and Deployment Mode</see> table. <c>SelfHosted</c> is split into two
/// values so phone-home and signed-license-key entitlement providers can be
/// picked at startup without runtime branching.
/// </summary>
/// <remarks>
/// Modules <strong>never</strong> read this enum directly. The composition
/// root selects provider implementations exactly once; the architecture test
/// <c>Modules_Do_Not_Reference_DeploymentMode</c> enforces the rule.
/// </remarks>
public enum DeploymentMode
{
    Development,
    SaaS,
    Dedicated,
    SelfHostedOnline,
    SelfHostedAirGapped,
}
