using System.Runtime.Versioning;

namespace app.Workflows;

/// <summary>
/// Convenience wrappers around <see cref="WorkflowEventSink.Enqueue"/> so the
/// existing inline host pages (StationView, OrderSearchPage) can emit
/// <c>workflow_events</c> without restructuring their state machines. Once the
/// declarative-workflow rollout lands on every station this helper goes away —
/// the engine will emit the same events from transition steps directly.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StationEvents
{
    public static void Emit(
        string workflowName,
        string stepId,
        string trigger,
        string? trackingNumber = null,
        string? fromState      = null,
        string? toState        = null,
        int? stationId         = null,
        string? @operator      = null,
        int? sequenceInSession = null,
        Dictionary<string, object?>? payload = null)
    {
        WorkflowEventSink.Enqueue(new WorkflowEventOut
        {
            WorkflowName      = workflowName,
            StepId            = stepId,
            Trigger           = trigger,
            TrackingNumber    = trackingNumber,
            FromState         = fromState,
            ToState           = toState,
            StationId         = stationId,
            Operator          = @operator,
            SequenceInSession = sequenceInSession,
            Payload           = payload,
            OccurredAt        = DateTime.UtcNow,
        });
    }
}
