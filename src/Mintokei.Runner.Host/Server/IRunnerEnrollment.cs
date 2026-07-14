namespace Mintokei.Runner.Host.Server;

/// <summary>
/// Host-callable enrollment surface — mint a one-time enrollment token and redeem one — so a host can
/// drive enrollment programmatically (e.g. from its own admin endpoint or provisioning automation)
/// without going through the HTTP routes or knowing the handler types. Backed by the same handlers the
/// <c>MapRunnerHost()</c> endpoints use.
/// </summary>
public interface IRunnerEnrollment
{
    /// <summary>Mints a one-time enrollment token (15-minute expiry); returns the plaintext token once.
    /// <paramref name="createdByUserId"/>/<paramref name="createdByUserName"/> are recorded for audit.</summary>
    Task<CreateEnrollmentTokenResult> CreateEnrollmentTokenAsync(
        string? createdByUserId = null, string? createdByUserName = null);

    /// <summary>Redeems an enrollment token for a machine identity — returns <c>{ MachineId, Secret }</c>
    /// on success, or a failure with a message (invalid / used / expired token).</summary>
    Task<RunnerHostResult<EnrollMachineResult>> EnrollAsync(EnrollMachineCommand command);
}

/// <summary>Default <see cref="IRunnerEnrollment"/> — delegates to the enrollment handlers.</summary>
internal sealed class RunnerEnrollment(
    CreateEnrollmentTokenHandler createToken, EnrollMachineHandler enroll) : IRunnerEnrollment
{
    public async Task<CreateEnrollmentTokenResult> CreateEnrollmentTokenAsync(
        string? createdByUserId = null, string? createdByUserName = null)
        // The create-token handler never fails (it only ever produces a token).
        => (await createToken.ExecuteAsync(new CreateEnrollmentTokenCommand(createdByUserId, createdByUserName))).Value!;

    public Task<RunnerHostResult<EnrollMachineResult>> EnrollAsync(EnrollMachineCommand command)
        => enroll.ExecuteAsync(command);
}
