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
// enough to make the ordering deterministic; the parallelism this gives up is
// worth very little; the suite is 47 tests running in about a second.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
