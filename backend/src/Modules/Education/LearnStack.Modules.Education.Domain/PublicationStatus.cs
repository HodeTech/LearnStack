namespace LearnStack.Modules.Education.Domain;

/// <summary>Independent publication state, separate from content access (ADR-0050).</summary>
public enum PublicationStatus
{
    Draft = 0,
    Published = 1,
}
