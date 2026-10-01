using System.Collections.Immutable;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Contracts.Seeding;

// Trusted contextual verification only: no public marker, endpoint or tenant input.
public sealed record SeedLookup<T>(T? State) where T : class;

public sealed record ContentTypeSeedDto(Guid Id, TenantId TenantId, string Key, int SchemaVersion,
    string Status, string DisplayNameJson, string JsonSchema, string RendererKey);
public sealed record TaxonomySeedItemDto(string Key, string DisplayNameJson, short Sort, string? Metadata);
public sealed record TaxonomySeedDto(Guid Id, TenantId TenantId, string Key, int SchemaVersion,
    string Status, string DisplayNameJson, ImmutableArray<TaxonomySeedItemDto> Items);

public sealed record GetContentTypeSeedStateQuery(Guid ContentTypeId) : IRequest<Result<SeedLookup<ContentTypeSeedDto>>>;
public sealed record GetTaxonomySeedStateQuery(Guid TaxonomyId) : IRequest<Result<SeedLookup<TaxonomySeedDto>>>;
