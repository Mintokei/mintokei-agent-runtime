namespace Mintokei.Runner;

public sealed class RunnerOptions
{
    public required string BackendUrl { get; set; }

    /// <summary>
    /// Optional override for the gRPC RunnerLink endpoint. Defaults to
    /// <see cref="BackendUrl"/> when null — appropriate for production
    /// deployments where an ingress path-routes /mintokei.runner.v1.RunnerLink/*
    /// to the dedicated h2c port. Required in local dev: Kestrel cannot
    /// run h2c and HTTP/1.1 on the same plain-HTTP listener, so the API
    /// auto-binds a separate loopback h2c port (5191) and the runner must
    /// point this option at it (e.g. http://localhost:5191).
    /// </summary>
    public string? GrpcBackendUrl { get; set; }

    /// <summary>
    /// One-time enrollment token (paste from UI). Cleared after successful enrollment.
    /// </summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>
    /// Machine ID assigned during enrollment. Persisted after first enrollment.
    /// </summary>
    public Guid? MachineId { get; set; }

    /// <summary>
    /// Long-lived secret received during enrollment. Used to obtain short-lived JWTs.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Directory where credentials and the local outbox are stored. Defaults to the
    /// OS per-user app-data dir. Override with --data-dir to run multiple runners on
    /// one machine, each with its own identity and outbox.
    /// </summary>
    public string? DataDir { get; set; }

    /// <summary>
    /// Display name reported to the backend at enrollment. Defaults to the machine
    /// name; set a distinct value (--name) when running several runners per host.
    /// </summary>
    public string? Name { get; set; }
}
