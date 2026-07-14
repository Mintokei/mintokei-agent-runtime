using Mintokei.Runner.Host.Server;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring for the runner-facing <em>server</em> surface of Mintokei.Runner.Host — the pieces a host
/// needs to accept runner connections (as opposed to <c>AddRunnerHostCore()</c>, which is the data-plane
/// transport). Today it registers runner-token issuance; as the extraction proceeds it will also add the
/// enrollment/token endpoints (<c>MapRunnerHost()</c>), the pluggable runner authenticator, and the JWT
/// bearer scheme.
/// </summary>
public static class RunnerHostServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the runner-facing server services, configured from <see cref="RunnerHostServerOptions"/>:
    /// the <see cref="RunnerTokenService"/> (mints runner access tokens), the enrollment / token
    /// request handlers behind <c>MapRunnerHost()</c>, and the host-callable <see cref="IRunnerEnrollment"/>.
    /// The host supplies the JWT signing key / issuer / audience (and optional token lifetime) via
    /// <paramref name="configure"/>, maps the endpoints with <c>MapRunnerHost()</c>, and provides a
    /// <c>RunnerHostDbContext</c>.
    /// </summary>
    public static IServiceCollection AddRunnerHostServer(
        this IServiceCollection services, Action<RunnerHostServerOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<RunnerTokenService>();

        services.AddScoped<CreateEnrollmentTokenHandler>();
        services.AddScoped<EnrollMachineHandler>();
        services.AddScoped<RequestRunnerTokenHandler>();
        services.AddScoped<IRunnerEnrollment, RunnerEnrollment>();

        return services;
    }
}
