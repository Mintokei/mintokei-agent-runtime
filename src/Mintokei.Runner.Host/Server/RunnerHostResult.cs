namespace Mintokei.Runner.Host.Server;

/// <summary>
/// Minimal success/failure result for the runner-host server handlers — the library-local stand-in for
/// the Api's <c>Result&lt;T&gt;</c> (which can't cross into the library without an
/// Api→Runner.Host→Api reference cycle). The enrollment/token handlers only ever succeed or
/// fail-with-a-message (every failure maps to HTTP 400), so this carries just the value and an error
/// string; the endpoints translate it to <c>Results.Ok</c> / <c>Results.BadRequest</c>.
/// </summary>
public readonly struct RunnerHostResult<T>
{
    public T? Value { get; }
    public string? Error { get; }
    public bool IsSuccess => Error is null;

    private RunnerHostResult(T value) { Value = value; Error = null; }
    private RunnerHostResult(string error) { Value = default; Error = error; }

    public static RunnerHostResult<T> Ok(T value) => new(value);
    public static RunnerHostResult<T> BadRequest(string error) => new(error);
}
