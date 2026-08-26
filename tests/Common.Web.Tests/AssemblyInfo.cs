using Xunit;

// OpenTelemetry's ASP.NET Core instrumentation subscribes a process-wide
// DiagnosticListener the moment any test builds a host through
// AddObservability — CommonWebDefaultsTests and ObservabilityTests both do.
// While that listener is live, ASP.NET Core's hosting layer starts a server
// Activity for every incoming request in the process, including one an
// unrelated test class sends through its own TestServer. That is exactly the
// ambient state CorrelationIdMiddlewareTests.A_request_with_no_trace_is_assigned_a_new_identifier
// sets Activity.Current to null to rule out — a host built in one test class,
// still alive when a request runs in another, defeats the null it assigned.
// The provider unsubscribes the listener on disposal, so serial execution is
// enough to make the ordering deterministic, and the parallelism it gives up
// is worth very little: this suite needs no container, so what serialising
// costs is seconds of wall clock against a flake that failed about half the
// time.
//
// That last sentence used to carry the suite's size and its duration, and both
// had gone stale — the count was well under half the real one. A number
// nothing recomputes rots beside a suite that grows, which is why
// IntegrationCollection.cs dropped its own pair rather than correcting them.
// The claim here is that serialising is cheap, and cheap is a property of
// needing no infrastructure, not of any particular total.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
