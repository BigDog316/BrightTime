using System.Diagnostics.CodeAnalysis;

namespace BrightTime.SharedKernel.Primitives;

/// <summary>
/// The kind of a failure, which maps to an HTTP status and to an RFC 9457
/// problem type at the edge. Modules return these; only the host translates
/// them, so a module never depends on ASP.NET Core to say "denied".
/// </summary>
public enum ErrorKind
{
    /// <summary>The request is malformed or fails validation.</summary>
    Validation,

    /// <summary>The caller is not authenticated.</summary>
    Unauthenticated,

    /// <summary>The caller is authenticated but not permitted. Carries a reason: a youth is told why, not shown an empty page.</summary>
    Forbidden,

    /// <summary>The target does not exist, or does not exist for this caller.</summary>
    NotFound,

    /// <summary>The target exists but the operation is invalid in its current state.</summary>
    Conflict,

    /// <summary>A precondition on the caller's side is unmet: unverified guardian link, missing consent, expired clearance.</summary>
    PreconditionFailed,

    /// <summary>The caller is making too many requests.</summary>
    RateLimited,

    /// <summary>A dependency failed.</summary>
    Unavailable,
}

/// <summary>A failure with a stable code, a human-readable message, and optional per-field detail.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message, IReadOnlyDictionary<string, string[]>? Details = null)
{
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(ErrorKind.Validation, code, message, details);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Precondition(string code, string message) =>
        new(ErrorKind.PreconditionFailed, code, message);
}

/// <summary>A result with no value.</summary>
public readonly record struct Result
{
    private Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>A result carrying a value on success.</summary>
public readonly record struct Result<T>
{
    private readonly T? _value;

    private Result(T value)
    {
        _value = value;
        Error = null;
    }

    private Result(Error error)
    {
        _value = default;
        Error = error;
    }

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    /// <summary>The value. Throws when the result is a failure; check <see cref="IsSuccess"/> first.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure ({Error!.Code}); no value to read.");

    public static Result<T> Success(T value) => new(value);

    public static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public Result<TNext> Map<TNext>(Func<T, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result<TNext>.Success(map(_value!)) : Result<TNext>.Failure(Error!);
    }

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Error, TOut> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(_value!) : onFailure(Error!);
    }
}
