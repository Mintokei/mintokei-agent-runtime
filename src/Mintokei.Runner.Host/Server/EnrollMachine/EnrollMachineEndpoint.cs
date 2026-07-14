using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Mintokei.Runner.Host.Server;

public record EnrollMachineRequest(
    string EnrollmentToken,
    string Name,
    string? Description = null,
    string? OsInfo = null,
    string? RunnerVersion = null);

public record EnrollMachineResponse(
    Guid MachineId,
    string Secret);

public static class EnrollMachineEndpoint
{
    public static IEndpointRouteBuilder MapEnrollMachineEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/machines/enroll", async (EnrollMachineRequest request, EnrollMachineHandler handler) =>
        {
            var result = await handler.ExecuteAsync(new(
                request.EnrollmentToken, request.Name, request.Description,
                request.OsInfo, request.RunnerVersion));

            if (result.IsSuccess)
                return Results.Ok(new EnrollMachineResponse(result.Value!.MachineId, result.Value.Secret));

            return Results.BadRequest(new { error = result.Error });
        })
        .WithName("EnrollMachine")
        .AllowAnonymous();

        return app;
    }
}
