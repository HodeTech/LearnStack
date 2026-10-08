using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;

/// <summary>The transport supplies only whether the query string violates the no-parameter contract.</summary>
[PublicSurface]
public sealed record GetPublicSiteQuery(bool HasUnexpectedQuery = false) : IRequest<Result<PublicSite>>;

public sealed record PublicSite(
    string DisplayName, IReadOnlyList<string> EnabledLocales, string DefaultLocale,
    PublicTheme? Theme, bool ShowPlatformAttribution);

public sealed record PublicTheme(string Primary, string Background, string Foreground, string Muted);
