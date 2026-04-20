using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using app.Services;

namespace app.Workflows;

/// <summary>
/// In-process queue that batches workflow events and POSTs them to
/// <c>/workflow-events</c> every ~2s. Stations can fire events freely without
/// awaiting an HTTP round-trip; a flush failure is logged and the batch is
/// dropped — the station's local log file remains the source of truth when the
/// network is down. A durable SQLite spill could be added later if we find we
/// actually lose meaningful events during outages.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WorkflowEventSink
{
    private static readonly ConcurrentQueue<WorkflowEventOut> _queue = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static HttpClient? _http;
    private static string      _httpBase = "";
    private static Task?       _pump;
    private static readonly object _pumpLock = new();

    public static void Enqueue(WorkflowEventOut evt)
    {
        _queue.Enqueue(evt);
        EnsurePump();
    }

    private static void EnsurePump()
    {
        if (_pump is not null) return;
        lock (_pumpLock)
        {
            _pump ??= Task.Run(PumpLoop);
        }
    }

    private static async Task PumpLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(2000);
                await FlushAsync();
            }
            catch (Exception ex)
            {
                Logger.Log($"WorkflowEventSink.PumpLoop: {ex.Message}");
            }
        }
        // ReSharper disable once FunctionNeverReturns
    }

    private static async Task FlushAsync()
    {
        if (_queue.IsEmpty) return;

        var batch = new List<WorkflowEventOut>(capacity: 64);
        while (batch.Count < 500 && _queue.TryDequeue(out var e))
            batch.Add(e);
        if (batch.Count == 0) return;

        try
        {
            var resp = await Http.PostAsJsonAsync(
                "workflow-events",
                new { events = batch },
                _json);
            if (!resp.IsSuccessStatusCode)
                Logger.Log($"WorkflowEventSink: HTTP {(int)resp.StatusCode} for {batch.Count} events");
        }
        catch (Exception ex)
        {
            Logger.Log($"WorkflowEventSink.Flush ({batch.Count} events): {ex.Message}");
        }
    }

    private static HttpClient Http
    {
        get
        {
            var url = (AppSettings.ApiUrl?.TrimEnd('/') ?? "http://localhost:8080") + "/";
            if (_http is null || _httpBase != url)
            {
                _http?.Dispose();
                _http     = new HttpClient { BaseAddress = new Uri(url) };
                _httpBase = url;
            }
            return _http;
        }
    }
}

/// <summary>
/// DTO matching <c>backend/src/api/workflow_events.rs::WorkflowEventIn</c>.
/// </summary>
public sealed class WorkflowEventOut
{
    [JsonPropertyName("stationId")]          public int?                  StationId { get; init; }
    [JsonPropertyName("stationType")]        public string?               StationType { get; init; }
    [JsonPropertyName("workflowName")]       public required string       WorkflowName { get; init; }
    [JsonPropertyName("trackingNumber")]     public string?               TrackingNumber { get; init; }
    [JsonPropertyName("stepId")]             public required string       StepId { get; init; }
    [JsonPropertyName("fromState")]          public string?               FromState { get; init; }
    [JsonPropertyName("toState")]            public string?               ToState { get; init; }
    [JsonPropertyName("trigger")]            public string?               Trigger { get; init; }
    [JsonPropertyName("operator")]           public string?               Operator { get; init; }
    [JsonPropertyName("sequenceInSession")]  public int?                  SequenceInSession { get; init; }
    [JsonPropertyName("payload")]            public Dictionary<string, object?>? Payload { get; init; }
    [JsonPropertyName("occurredAt")]         public DateTime              OccurredAt { get; init; }
}
