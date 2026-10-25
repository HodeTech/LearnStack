namespace LearnStack.SharedKernel.DataProtection;

/// <summary>Explicit semantic classification of a mapped property (ADR-0073).</summary>
/// <remarks>Zero is deliberately invalid: nonpersonal data requires an explicit None declaration.</remarks>
public enum PersonalDataCategory
{
    None = 1,
    Identity = 2,
    Behaviour = 3,
    Sensitive = 4,
    Payment = 5,
    Audit = 6,
}
