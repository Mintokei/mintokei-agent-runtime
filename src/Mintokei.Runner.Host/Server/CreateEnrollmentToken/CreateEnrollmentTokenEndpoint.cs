using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Mintokei.Runner.Host.Server;

public record CreateEnrollmentTokenResponse(
    string Token,
    string DisplayPrefix,
    DateTimeOffset ExpiresAt);

public static class CreateEnrollmentTokenEndpoint
{
    public static IEndpointRouteBuilder MapCreateEnrollmentTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/machines/enrollment-tokens", async (
            HttpContext context,
            CreateEnrollmentTokenHandler handler) =>
        {
            var userId = context.User.FindFirst("sub")?.Value
                         ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var userName = context.User.FindFirst("name")?.Value
                           ?? context.User.FindFirst(ClaimTypes.Name)?.Value;

            var result = await handler.ExecuteAsync(new(userId, userName));

            if (result.IsSuccess)
                return Results.Ok(new CreateEnrollmentTokenResponse(
                    result.Value!.Token, result.Value.DisplayPrefix, result.Value.ExpiresAt));

            return Results.BadRequest(new { error = result.Error });
        })
        .WithName("CreateEnrollmentToken");

        return app;
    }
}
