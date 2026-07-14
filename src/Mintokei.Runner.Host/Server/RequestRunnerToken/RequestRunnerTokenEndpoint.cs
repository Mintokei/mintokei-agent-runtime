using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Mintokei.Runner.Host.Server;

public record RequestRunnerTokenRequest(
    Guid MachineId,
    string Secret);

public record RequestRunnerTokenResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt);

public static class RequestRunnerTokenEndpoint
{
    public static IEndpointRouteBuilder MapRequestRunnerTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/runner-token", async (
            RequestRunnerTokenRequest request,
            RequestRunnerTokenHandler handler) =>
        {
            var result = await handler.ExecuteAsync(new(request.MachineId, request.Secret));

            if (result.IsSuccess)
                return Results.Ok(new RequestRunnerTokenResponse(
                    result.Value!.AccessToken, result.Value.ExpiresAt));

            return Results.BadRequest(new { error = result.Error });
        })
        .WithName("RequestRunnerToken")
        .AllowAnonymous();

        return app;
    }
}
