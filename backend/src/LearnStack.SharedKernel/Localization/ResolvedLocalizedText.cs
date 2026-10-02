namespace LearnStack.SharedKernel.Localization;

/// <summary>An immutable display label and the canonical locale actually authored.</summary>
public sealed record ResolvedLocalizedText(string Value, string Locale);
