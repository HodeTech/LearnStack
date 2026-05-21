using FluentValidation;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// MediatR pipeline behavior — step 1 of the canonical 8-step order
/// (ADR-0032 § Sub-decision 2). Aggregates FluentValidation failures into
/// <see cref="Error.Details"/> and returns
/// <c>Result.FailFor&lt;TResponse&gt;(validation_failed, …)</c>; <strong>never
/// throws</strong> <see cref="ValidationException"/>. The
/// <c>ValidationBehavior_DoesNotThrow_ValidationException</c> architecture
/// test enforces this end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// The <typeparamref name="TResponse"/> constraint <see cref="IResultBase"/>
/// lets the behavior construct the concrete <c>Result&lt;T&gt;</c> shape via
/// <see cref="Result.FailFor{TResponse}"/> without referencing the value type
/// (ADR-0032 § Sub-decision 3).
/// </para>
/// <para>
/// Field names are kept in their PascalCase property form here; the API
/// boundary (<c>ProblemDetailsFactory</c>) lower-cases them on the way out
/// per Standards 09 § Validation Errors.
/// </para>
/// </remarks>
public sealed class ValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : IResultBase
{
    private const string ValidationFailedKey = "lockey_validation_failed";

    private readonly IValidator<TRequest>[] _validators = validators.ToArray();

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        if (_validators.Length == 0)
        {
            return await next().ConfigureAwait(false);
        }

        var context = new ValidationContext<TRequest>(request);
        var failures = new List<FluentValidation.Results.ValidationFailure>();

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
            {
                failures.AddRange(result.Errors);
            }
        }

        if (failures.Count == 0)
        {
            return await next().ConfigureAwait(false);
        }

        var details = failures
            .GroupBy(f => f.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LocalizedMessage>)g
                    .Select(f => new LocalizedMessage(NormaliseKey(f.ErrorCode ?? f.ErrorMessage)))
                    .ToArray(),
                StringComparer.Ordinal);

        var error = new Error(
            new LocalizedMessage(ValidationFailedKey),
            details);

        return Result.FailFor<TResponse>(error);
    }

    /// <summary>
    /// FluentValidation error codes are typically rule names ("NotEmpty",
    /// "EmailValidator"); the API contract requires the
    /// <see cref="LocalizedMessage.RequiredPrefix"/>. If the validator's
    /// <c>ErrorCode</c> already starts with <c>lockey_</c> we trust it;
    /// otherwise we coerce by lower-casing and prefixing so the constructor
    /// invariant holds.
    /// </summary>
    private static string NormaliseKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "lockey_validation_failed";
        }

        return raw.StartsWith(LocalizedMessage.RequiredPrefix, StringComparison.Ordinal)
            ? raw
            : LocalizedMessage.RequiredPrefix + raw.ToLowerInvariant();
    }
}
