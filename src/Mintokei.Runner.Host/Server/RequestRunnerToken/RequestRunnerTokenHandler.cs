using Microsoft.EntityFrameworkCore;
using Mintokei.Runner.Host.Domain.Machines;
using Mintokei.Runner.Host.Persistence;

namespace Mintokei.Runner.Host.Server;

public sealed class RequestRunnerTokenHandler(RunnerHostDbContext db, RunnerTokenService tokenService)
{
    public async Task<RunnerHostResult<RequestRunnerTokenResult>> ExecuteAsync(RequestRunnerTokenCommand command)
    {
        var machine = await db.RunnerMachines
            .FirstOrDefaultAsync(m => m.Id == command.MachineId);

        if (machine is null)
            return RunnerHostResult<RequestRunnerTokenResult>.BadRequest("Machine not found.");

        if (machine.SecretHash is null)
            return RunnerHostResult<RequestRunnerTokenResult>.BadRequest("Machine has not been enrolled.");

        var secretHash = SecretHasher.Hash(command.Secret);
        if (secretHash != machine.SecretHash)
            return RunnerHostResult<RequestRunnerTokenResult>.BadRequest("Invalid secret.");

        var (token, expiresAt) = tokenService.GenerateToken(machine.Id, machine.Name);

        return RunnerHostResult<RequestRunnerTokenResult>.Ok(new(token, expiresAt));
    }
}
