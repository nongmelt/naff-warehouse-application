using System.Runtime.Versioning;
using app.Services;

namespace app.Workflows;

/// <summary>
/// Runs a <see cref="Workflow"/> instance. Holds the current state, fires
/// triggers, matches them against transitions (first-match guard wins),
/// executes each <see cref="WorkflowStep"/>'s side effect, and ships a
/// <see cref="WorkflowEventOut"/> per step via <see cref="WorkflowEventSink"/>.
///
/// Concurrency: trigger firing is serialised with a <see cref="SemaphoreSlim"/>
/// per engine instance. That matches today's single-threaded barcode-input
/// contract and avoids interleaving state transitions during an in-flight step.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkflowEngine
{
    private readonly Workflow      _workflow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private          string        _current;

    public WorkflowEngine(Workflow workflow)
    {
        _workflow = workflow;
        _current  = workflow.InitialState;
    }

    public string CurrentState => _current;
    public Workflow Definition => _workflow;

    /// <summary>
    /// Fires a trigger. The first matching transition runs every step in order,
    /// emitting a workflow_event per step, then advances the state. Unknown
    /// states and unmatched triggers are logged and swallowed — a misfire
    /// should never crash the host page.
    /// </summary>
    public async Task FireAsync(string trigger, WorkflowContext ctx)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_workflow.States.TryGetValue(_current, out var state))
            {
                Logger.Log($"[Workflow:{_workflow.Name}] unknown state '{_current}' when firing '{trigger}'");
                return;
            }

            Transition? match = null;
            foreach (var t in state.Transitions)
            {
                if (t.Trigger != trigger) continue;
                try
                {
                    if (t.Guard(ctx)) { match = t; break; }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Workflow:{_workflow.Name}] guard threw on '{trigger}' in '{_current}': {ex.Message}");
                }
            }

            if (match is null)
            {
                Logger.Log($"[Workflow:{_workflow.Name}] no transition for trigger '{trigger}' in state '{_current}'");
                return;
            }

            var from = _current;
            var to   = match.Next;

            foreach (var step in match.Steps)
            {
                var evt = BuildEvent(ctx, match, step, from, to);
                try
                {
                    await step.Run(ctx);
                }
                catch (Exception ex)
                {
                    // Step failure: still emit the event so analytics sees the attempt,
                    // tagged with the exception, and stop the chain before transitioning.
                    evt.Payload!["error"] = ex.Message;
                    WorkflowEventSink.Enqueue(evt);
                    Logger.Log($"[Workflow:{_workflow.Name}] step '{step.StepId}' threw: {ex.Message}");
                    return;
                }
                WorkflowEventSink.Enqueue(evt);
            }

            _current = to;
        }
        finally
        {
            _gate.Release();
        }
    }

    private WorkflowEventOut BuildEvent(
        WorkflowContext ctx, Transition t, WorkflowStep step, string from, string to)
    {
        var payload = new Dictionary<string, object?>(capacity: 8);
        // Copy whatever the context has set — the engine is payload-agnostic.
        if (ctx.Sku                    is not null) payload["sku"]                    = ctx.Sku;
        if (ctx.PreviousTrackingNumber is not null) payload["previousTrackingNumber"] = ctx.PreviousTrackingNumber;
        if (ctx.QtyBefore           is not null) payload["qtyBefore"]           = ctx.QtyBefore;
        if (ctx.QtyAfter            is not null) payload["qtyAfter"]            = ctx.QtyAfter;
        if (ctx.QtyRemaining        is not null) payload["qtyRemaining"]        = ctx.QtyRemaining;
        if (ctx.QtyEntered          is not null) payload["qtyEntered"]          = ctx.QtyEntered;
        if (ctx.QtyDeducted         is not null) payload["qtyDeducted"]         = ctx.QtyDeducted;
        if (ctx.ItemsPicked         is not null) payload["itemsPicked"]         = ctx.ItemsPicked;
        if (ctx.ItemsRemaining      is not null) payload["itemsRemaining"]      = ctx.ItemsRemaining;
        if (ctx.OrdersFound         is not null) payload["ordersFound"]         = ctx.OrdersFound;
        if (ctx.CheckedBy           is not null) payload["checkedBy"]           = ctx.CheckedBy;
        if (ctx.RejectReason        is not null) payload["reason"]              = ctx.RejectReason;
        if (ctx.PreviousStatus      is not null) payload["previousStatus"]      = ctx.PreviousStatus;
        if (ctx.DurationSeconds     is not null) payload["durationSeconds"]     = ctx.DurationSeconds;
        if (ctx.VideoFileSizeBytes  is not null) payload["videoFileSizeBytes"]  = ctx.VideoFileSizeBytes;
        if (ctx.StationLabel        is not null) payload["stationLabel"]        = ctx.StationLabel;
        if (ctx.PackedBy            is not null) payload["packedBy"]            = ctx.PackedBy;
        if (ctx.VideoId             is not null) payload["videoId"]             = ctx.VideoId;
        if (ctx.FailureReason       is not null) payload["failureReason"]       = ctx.FailureReason;
        if (ctx.UploadDurationMs    is not null) payload["durationMs"]          = ctx.UploadDurationMs;
        if (ctx.UploadResponseStatus is not null) payload["responseStatus"]     = ctx.UploadResponseStatus;
        if (ctx.UploadAttempt > 0)               payload["attempt"]             = ctx.UploadAttempt;
        foreach (var kv in ctx.Extra)            payload[kv.Key]                = kv.Value;

        return new WorkflowEventOut
        {
            StationId         = ctx.StationId,
            StationType       = _workflow.Name,
            WorkflowName      = _workflow.Name,
            TrackingNumber    = ctx.ActiveBarcode ?? ctx.Barcode,
            StepId            = step.StepId,
            FromState         = from,
            ToState           = to,
            Trigger           = t.Trigger,
            Operator          = ctx.Operator,
            SequenceInSession = ctx.SequenceInSession > 0 ? ctx.SequenceInSession : null,
            Payload           = payload.Count > 0 ? payload : null,
            OccurredAt        = DateTime.UtcNow,
        };
    }
}
