using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Api.Common;

/// <summary>
/// Projects an MVC model-binding failure into the same Problem Details shape
/// every other LearnStack error uses.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ApiControllerAttribute"/> installs an automatic 400 for an
/// invalid <see cref="ModelStateDictionary"/>. That response is produced
/// before MediatR runs, so it bypasses <c>ValidationBehavior</c> entirely and
/// — left at its default — emits ASP.NET's own Problem Details body, with
/// English framework text and the binder's parameter names in it. Standards 09
/// § API Surface admits exactly one error shape, keyed by <c>code</c> and
/// <c>messageKey</c>, and Standards 04 § Forbidden rules out echoing internal
/// messages to clients.
/// </para>
/// <para>
/// The binder's own message is deliberately dropped rather than mapped:
/// "The JSON value could not be converted to System.Int32" names an internal
/// type. What survives is the field path — which the client needs — carried in
/// the same <c>errors</c> map <c>ValidationBehavior</c> produces, so a client
/// that already handles <c>validation_failed</c> handles this with no new code.
/// </para>
/// </remarks>
public static class ModelBindingProblemDetails
{
    /// <summary>
    /// The per-field message key. One key rather than a taxonomy of binder
    /// failures: a client cannot act differently on "wrong type" than on
    /// "malformed", and a richer key set would have to be localised for
    /// failures the API layer should be rejecting anyway.
    /// </summary>
    public const string FieldMessageKey = "lockey_invalid_value";

    /// <summary>
    /// The <c>errors</c> key a body-level failure is reported under.
    /// ModelState uses the empty string for "the body as a whole"; the wire
    /// shape needs a name, and <c>$</c> is the JSONPath root.
    /// </summary>
    public const string BodyKey = "$";

    public static IActionResult For(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Group, do not ToDictionary. ModelStateDictionary guarantees its own
        // keys are unique, but the normalisation below does not preserve that:
        // a body-level error keyed "" and a field literally named "$" both
        // normalise to "$", and a bare ToDictionary throws ArgumentException —
        // turning a client's malformed body into a 500.
        var details = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .GroupBy(
                entry => string.IsNullOrEmpty(entry.Key) ? BodyKey : entry.Key,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LocalizedMessage>)
                    [.. group.Select(_ => new LocalizedMessage(FieldMessageKey))],
                StringComparer.Ordinal);

        return new ProblemDetailsActionResult(
            new Error(new LocalizedMessage("lockey_validation_failed"), details));
    }
}
