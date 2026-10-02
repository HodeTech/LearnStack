namespace LearnStack.Modules.Customization.Infrastructure.Projections;

/// <summary>
/// Sticky for the DI scope, not a transaction object: a rolled-back generation can
/// be reissued and Npgsql can reuse transaction instances. No reset or tenant setter.
/// </summary>
public sealed class CustomizationReadState
{
    public bool IsDirty { get; private set; }
    public void MarkDirty() => IsDirty = true;
}
