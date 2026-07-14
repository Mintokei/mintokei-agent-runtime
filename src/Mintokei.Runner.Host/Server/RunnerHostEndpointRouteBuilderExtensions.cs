using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Mintokei.Runner.Host.Server;

/// <summary>
/// Maps the runner-facing HTTP surface of Mintokei.Runner.Host so a host accepts runner enrollment /
/// token exchange without any of that code living in the host project.
/// </summary>
public static class RunnerHostEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the runner connectivity endpoints and returns a handle for applying host authorization:
    /// <list type="bullet">
    ///   <item><c>POST /machines/enroll</c> — anonymous; the enrollment token is the bearer credential.</item>
    ///   <item><c>POST /auth/runner-token</c> — secret-authenticated; exchanges the machine secret for a JWT.</item>
    ///   <item><c>POST /machines/enrollment-tokens</c> — operator-facing mint; grouped so the host attaches
    ///   its own user authorization via <see cref="RunnerHostEndpoints.EnrollmentTokens"/>.</item>
    /// </list>
    /// The gRPC data plane (<c>RunnerLinkService</c>) is mapped separately by the host via
    /// <c>MapGrpcService&lt;RunnerLinkService&gt;()</c> — it needs the full <c>AddRunnerHostCore()</c> wiring.
    /// </summary>
    public static RunnerHostEndpoints MapRunnerHost(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapEnrollMachineEndpoint();
        endpoints.MapRequestRunnerTokenEndpoint();

        // The mint endpoint carries no auth of its own — group it so the host can require an operator
        // policy without touching the anonymous/secret-authenticated runner-facing routes above.
        var enrollmentTokens = endpoints.MapGroup("");
        enrollmentTokens.MapCreateEnrollmentTokenEndpoint();

        return new RunnerHostEndpoints(enrollmentTokens);
    }
}

/// <summary>Handles for the route groups <see cref="RunnerHostEndpointRouteBuilderExtensions.MapRunnerHost"/>
/// maps, so the host can apply its own conventions (authorization, rate limiting, …).</summary>
public sealed class RunnerHostEndpoints(IEndpointConventionBuilder enrollmentTokens)
{
    /// <summary>The enrollment-token <em>mint</em> endpoint — apply operator authorization here, e.g.
    /// <c>.RequireAuthorization("Admin")</c>. Left open by default; the runner-facing enroll / token
    /// routes are intentionally anonymous / secret-authenticated and need no host policy.</summary>
    public IEndpointConventionBuilder EnrollmentTokens { get; } = enrollmentTokens;
}
