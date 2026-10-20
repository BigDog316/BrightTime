namespace BrightTime.Api;

/// <summary>
/// RFC 9457 problems with a stable <c>type</c> per class of error (docs/plan/05). The framework
/// adds the <c>traceId</c>. URNs, because they are identifiers, not pages to fetch.
/// </summary>
public static class ApiProblems
{
    public const string ValidationType = "urn:brighttime:problem:validation";
    public const string NotFoundType = "urn:brighttime:problem:not-found";

    /// <summary>A request whose values are wrong, with what is wrong keyed by parameter.</summary>
    public static IResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
            title: "Some values are not valid.",
            type: ValidationType);

    public static IResult NotFound(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found.", detail: detail, type: NotFoundType);

    /// <summary>
    /// A module's refusal, as a problem whose <c>type</c> names the reason
    /// (<c>urn:brighttime:problem:method_insufficient</c>), so a client can act on it without
    /// parsing the text. Arguments travel as extensions.
    /// </summary>
    public static IResult From(SharedKernel.Primitives.Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        int status = error.Kind switch
        {
            SharedKernel.Primitives.ErrorKind.Validation => StatusCodes.Status400BadRequest,
            SharedKernel.Primitives.ErrorKind.Unauthenticated => StatusCodes.Status401Unauthorized,
            SharedKernel.Primitives.ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            SharedKernel.Primitives.ErrorKind.NotFound => StatusCodes.Status404NotFound,
            SharedKernel.Primitives.ErrorKind.Conflict => StatusCodes.Status409Conflict,
            SharedKernel.Primitives.ErrorKind.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
            SharedKernel.Primitives.ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status503ServiceUnavailable,
        };

        return Results.Problem(
            statusCode: status,
            title: error.Message,
            type: "urn:brighttime:problem:" + error.Code,
            extensions: error.Arguments?.ToDictionary(a => a.Key, a => (object?)a.Value, StringComparer.Ordinal));
    }
}
