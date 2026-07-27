using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mintokei.Sandbox.Hosting;

/// <summary>
/// OpenTelemetry instrumentation for sandbox provisioning — a span per phase plus a per-phase duration
/// histogram and an outcome counter, all under the <c>Mintokei.Sandbox</c> source/meter (registered in
/// Program.cs so they flow to the OTLP collector / SigNoz). Answers "which part of bringing a sandbox up costs
/// the time": build the enrollment request → launch (broker + pod/container) → WAIT for the runner to come
/// online (the dominant, variable phase: pod scheduling + image pull + repo clone + enroll).
/// </summary>
public static class SandboxTelemetry
{
    /// <summary>ActivitySource + Meter name — register with <c>AddSource</c>/<c>AddMeter</c> in Program.cs.</summary>
    public const string Name = "Mintokei.Sandbox";

    public static readonly ActivitySource Activity = new(Name);
    private static readonly Meter Meter = new(Name);

    /// <summary>Duration of a sandbox lifecycle phase in ms. Tags: <c>phase</c>, <c>backend</c>. Query in SigNoz
    /// by <c>phase</c> for a p50/p95/p99 breakdown of where the time goes.</summary>
    public static readonly Histogram<double> PhaseMs =
        Meter.CreateHistogram<double>("sandbox.phase.duration", unit: "ms",
            description: "Duration of a sandbox lifecycle phase (build_request / launch / wait_online / …)");

    /// <summary>Provisioning outcomes. Tags: <c>outcome</c> (online | not_online | error), <c>backend</c>.</summary>
    public static readonly Counter<long> Outcome =
        Meter.CreateCounter<long>("sandbox.provision.outcome", unit: "{provision}",
            description: "Sandbox provisioning outcomes (online / did-not-come-online / error)");

    /// <summary>Open a timed phase: starts a span and records the histogram on dispose. Use with <c>using</c>.</summary>
    public static PhaseTimer Phase(string phase, string backend) => new(phase, backend);

    /// <summary>Record a phase duration directly (ms) — for sub-phases whose boundaries are observed
    /// asynchronously (e.g. pod_ready / runner_enroll inside the wait loop) rather than wrapped in a
    /// <c>using</c> scope.</summary>
    public static void RecordPhaseMs(string phase, string backend, double ms) =>
        PhaseMs.Record(ms,
            new KeyValuePair<string, object?>("phase", phase),
            new KeyValuePair<string, object?>("backend", backend));

    /// <summary>Record a provisioning outcome (online | not_online | error) for a backend.</summary>
    public static void RecordOutcome(string outcome, string backend) =>
        Outcome.Add(1,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("backend", backend));

    public readonly struct PhaseTimer : IDisposable
    {
        private readonly string _phase;
        private readonly string _backend;
        private readonly long _start;
        private readonly Activity? _span;

        internal PhaseTimer(string phase, string backend)
        {
            _phase = phase;
            _backend = backend;
            _start = Stopwatch.GetTimestamp();
            _span = Activity.StartActivity($"sandbox.{phase}", ActivityKind.Internal);
            _span?.SetTag("sandbox.backend", backend);
            _span?.SetTag("sandbox.phase", phase);
        }

        /// <summary>Attach a tag to this phase's span (no-op when tracing is disabled / no listener).</summary>
        public void SetTag(string key, object? value) => _span?.SetTag(key, value);

        public void Dispose()
        {
            PhaseMs.Record(Stopwatch.GetElapsedTime(_start).TotalMilliseconds,
                new KeyValuePair<string, object?>("phase", _phase),
                new KeyValuePair<string, object?>("backend", _backend));
            _span?.Dispose();
        }
    }
}
