using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using app.Workflows;

namespace app.Services;

/// <summary>
/// Persistent WebSocket connection from this station to the backend.
/// Replaces HTTP heartbeat (connection alive = online) and WorkflowEventSink
/// (events sent immediately over the socket instead of batched HTTP).
///
/// On first connect: sends a startup/idle workflow_event.
/// On reconnect: resends operator_login if an operator is currently active,
/// so backend in-memory state is restored after backend restart.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StationWsClient
{
    private static ClientWebSocket? _ws;
    private static CancellationTokenSource? _cts;
    private static int _stationId;
    private static string? _currentOperator;
    private static SessionKind _currentOperatorKind;
    private static bool _hasConnected;

    private static readonly SemaphoreSlim _sendLock = new(1, 1);

    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Start(int stationId)
    {
        _stationId = stationId;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    public static void Stop()
    {
        _cts?.Cancel();
        _ws?.Dispose();
    }

    public static void SendWorkflowEvent(WorkflowEventOut evt)
    {
        var msg = new WsWorkflowEventMsg
        {
            WorkflowName = evt.WorkflowName,
            TrackingNumber = evt.TrackingNumber,
            StepId = evt.StepId,
            FromState = evt.FromState,
            ToState = evt.ToState,
            Trigger = evt.Trigger,
            Operator = evt.Operator,
            SequenceInSession = evt.SequenceInSession,
            Payload = evt.Payload,
            OccurredAt = evt.OccurredAt,
        };
        FireAndForget(msg);
    }

    public static void SendOperatorLogin(string operatorCode, SessionKind kind = SessionKind.Packing)
    {
        _currentOperator = operatorCode;
        _currentOperatorKind = kind;
        FireAndForget(new WsOperatorLoginMsg { Operator = operatorCode, StationKind = kind.ToString() });
    }

    public static void SendOperatorLogout()
    {
        _currentOperator = null;
        FireAndForget(new WsOperatorLogoutMsg());
    }

    private static void FireAndForget(object msg)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        _ = Task.Run(() => SendAsync(msg, ct));
    }

    private static async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                var url = BuildWsUrl();
                await _ws.ConnectAsync(new Uri(url), ct);
                backoff = TimeSpan.FromSeconds(1);
                Logger.Log($"StationWsClient: connected to {url}");

                await SendOnConnectAsync(ct);
                await ReceiveLoopAsync(_ws, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Log($"StationWsClient: {ex.Message} — retry in {backoff.TotalSeconds:0}s");
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    private static async Task SendOnConnectAsync(CancellationToken ct)
    {
        if (!_hasConnected)
        {
            _hasConnected = true;
            await SendAsync(new WsWorkflowEventMsg
            {
                WorkflowName = "Station",
                StepId = "startup",
                Trigger = "startup",
                FromState = "online",
                ToState = "idle",
                OccurredAt = DateTime.UtcNow,
            }, ct);
        }

        if (_currentOperator is not null)
            await SendAsync(new WsOperatorLoginMsg { Operator = _currentOperator, StationKind = _currentOperatorKind.ToString() }, ct);
    }

    private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[256];
        while (!ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buf, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }

    private static async Task SendAsync(object msg, CancellationToken ct)
    {
        var ws = _ws;
        if (ws?.State != WebSocketState.Open) return;

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, msg.GetType(), _json));

        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch (Exception ex)
        {
            Logger.Log($"StationWsClient.Send: {ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string BuildWsUrl()
    {
        var api = (AppSettings.ApiUrl?.TrimEnd('/') ?? "http://localhost:8080");
        var wsBase = api.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
                        .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);
        return $"{wsBase}/stations/{_stationId}/ws";
    }
}

internal sealed class WsWorkflowEventMsg
{
    [JsonPropertyName("type")] public string Type { get; } = "workflow_event";
    [JsonPropertyName("workflowName")] public required string WorkflowName { get; init; }
    [JsonPropertyName("trackingNumber")] public string? TrackingNumber { get; init; }
    [JsonPropertyName("stepId")] public required string StepId { get; init; }
    [JsonPropertyName("fromState")] public string? FromState { get; init; }
    [JsonPropertyName("toState")] public string? ToState { get; init; }
    [JsonPropertyName("trigger")] public string? Trigger { get; init; }
    [JsonPropertyName("operator")] public string? Operator { get; init; }
    [JsonPropertyName("sequenceInSession")] public int? SequenceInSession { get; init; }
    [JsonPropertyName("payload")] public Dictionary<string, object?>? Payload { get; init; }
    [JsonPropertyName("occurredAt")] public DateTime OccurredAt { get; init; }
}

internal sealed class WsOperatorLoginMsg
{
    [JsonPropertyName("type")] public string Type { get; } = "operator_login";
    [JsonPropertyName("operator")] public required string Operator { get; init; }
    [JsonPropertyName("stationKind")] public required string StationKind { get; init; }
}

internal sealed class WsOperatorLogoutMsg
{
    [JsonPropertyName("type")] public string Type { get; } = "operator_logout";
}
