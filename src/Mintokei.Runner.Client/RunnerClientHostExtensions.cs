using Microsoft.Extensions.DependencyInjection;

namespace Mintokei.Runner;

/// <summary>
/// Startup helper for a host that registered a runner via <c>AddMintokeiRunner</c>.
/// </summary>
public static class RunnerClientHostExtensions
{
    /// <summary>
    /// Runs the runner's one-time startup prerequisites — initialise the local outbox database, then
    /// ensure the runner is enrolled (from persisted credentials, or the enrollment token on a first
    /// boot). Call once after <c>Build()</c> and <em>before</em> <c>RunAsync()</c>: enrollment is a hard
    /// prerequisite that must complete before the transports connect, so it is deliberately not a hosted
    /// service (a failed hosted service can't cleanly stop its siblings from starting).
    ///
    /// Throws if enrollment fails (unreachable backend, or a bad / expired / missing token) — the
    /// <c>mintokei-runner</c> executable catches that and exits with a clear message; an embedder can
    /// handle it however it likes.
    /// </summary>
    public static async Task EnsureRunnerReadyAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await services.GetRequiredService<LocalOutbox>().InitializeAsync();
        await services.GetRequiredService<EnrollmentService>().EnsureEnrolledAsync(cancellationToken);
    }
}
