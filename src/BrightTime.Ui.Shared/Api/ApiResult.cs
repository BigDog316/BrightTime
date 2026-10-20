namespace BrightTime.Ui.Shared.Api;

/// <summary>An API answer: a value, or the problem that stopped it.</summary>
public sealed class ApiResult<T>
{
    private ApiResult(T? value, ApiProblem? problem)
    {
        Value = value;
        Problem = problem;
    }

    public T? Value { get; }

    public ApiProblem? Problem { get; }

    public bool IsSuccess => Problem is null;

    public static ApiResult<T> Success(T value) => new(value, null);

    public static ApiResult<T> Failure(ApiProblem problem) => new(default, problem ?? throw new ArgumentNullException(nameof(problem)));
}

/// <summary>An API answer with no body: done, or the problem that stopped it.</summary>
public sealed class ApiResult
{
    private ApiResult(ApiProblem? problem) => Problem = problem;

    public ApiProblem? Problem { get; }

    public bool IsSuccess => Problem is null;

    public static ApiResult Success { get; } = new(null);

    public static ApiResult Failure(ApiProblem problem) => new(problem ?? throw new ArgumentNullException(nameof(problem)));
}
