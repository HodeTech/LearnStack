using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Infrastructure.Jobs;

public sealed class ReadBackgroundJobExternalStateQueryHandler(IUnitOfWork unit,
    BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeOptions options)
    : IRequestHandler<ReadBackgroundJobExternalStateQuery, Result<BackgroundJobExternalSnapshot>>
{
    public async Task<Result<BackgroundJobExternalSnapshot>> Handle(ReadBackgroundJobExternalStateQuery request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.RequireOwner(unit);
        var authority = request.Authority;
        if (authority.IsTerminalNoStart)
        {
            var terminal = await BackgroundJobStoredExternalFacts.ReadTerminalNoStartAsync(unit, authority, options,
                cancellationToken).ConfigureAwait(false);
            request.RequireOwner(unit);
            request.RecordRead(unit, terminal);
            return Result<BackgroundJobExternalSnapshot>.Ok(terminal);
        }
        if (!definitions.TryGet(authority.DefinitionKey, out var definition)
            || definition.Readiness != BackgroundJobDefinitionReadiness.Active
            || definition.Profile != BackgroundJobExecutionProfile.External
            || definition.Queue != authority.Queue || definition.CapacityGroup != authority.CapacityGroup
            || (definition.Scope == BackgroundJobScope.TenantWide) != (authority.OrganizationId is null))
            throw BackgroundJobExternalCheckpointSql.Refused();
        var predicate = BackgroundJobExternalCheckpointSql.Identity(authority)
            + BackgroundJobExternalCheckpointSql.Live(authority) + BackgroundJobExternalCheckpointSql.Phase(authority);
        BackgroundJobEnvelope envelope;
        await using (var readEnvelope = BackgroundJobExternalCheckpointSql.Command(unit, authority, options,
            "SELECT " + BackgroundJobEnvelopeSql.Projection + " FROM background_jobs j WHERE " + predicate))
        await using (var row = await readEnvelope.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var found = await row.ReadAsync(cancellationToken).ConfigureAwait(false);
            request.RequireOwner(unit);
            if (!found) throw BackgroundJobExternalCheckpointSql.Refused();
            envelope = BackgroundJobEnvelopeSql.Read(row);
            if (!authority.MatchesEnvelope(envelope)
                || !ReferenceEquals(BackgroundJobEnvelopeSql.ExternalDefinition(envelope, definitions), definition))
                throw BackgroundJobExternalCheckpointSql.Refused();
        }
        request.RequireOwner(unit);
        await using var command = BackgroundJobExternalCheckpointSql.Command(unit, authority, options,
            "SELECT " + BackgroundJobStoredExternalFacts.Projection + BackgroundJobStoredExternalFacts.From + " " + predicate);
        command.Parameters.AddWithValue("normal_attempt", (authority.SelectedNormalAttemptId ?? authority.AttemptId).Value);
        await using var facts = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var exists = await facts.ReadAsync(cancellationToken).ConfigureAwait(false);
        request.RequireOwner(unit);
        if (!exists) throw BackgroundJobExternalCheckpointSql.Refused();
        var result = BackgroundJobStoredExternalFacts.Read(facts, definition, envelope, authority.NormalAttemptOrdinal,
            authority.SelectedNormalAttemptId ?? authority.AttemptId);
        request.RequireOwner(unit);
        request.RecordRead(unit, result);
        return Result<BackgroundJobExternalSnapshot>.Ok(result);
    }
}
