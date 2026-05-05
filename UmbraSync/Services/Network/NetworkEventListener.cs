using System.Diagnostics.Tracing;

namespace UmbraSync.Services.Network;

internal sealed class NetworkEventListener : EventListener
{
    private readonly NetworkDiagnosticService _sink;

    private static readonly HashSet<string> InterestingSourceNames = new(StringComparer.Ordinal)
    {
        "Microsoft-System-Net-Sockets",
        "System.Net.Sockets",
        "Private.InternalDiagnostics.System.Net.Sockets",
        "Microsoft-System-Net-Http",
        "System.Net.Http",
        "Private.InternalDiagnostics.System.Net.Http",
        "Microsoft-System-Net-Security",
        "System.Net.Security",
        "Private.InternalDiagnostics.System.Net.Security",
        "Microsoft-System-Net-NameResolution",
        "System.Net.NameResolution",
        "Microsoft-AspNetCore-SignalR-Client",
        "Microsoft.AspNetCore.SignalR.Client",
    };

    public NetworkEventListener(NetworkDiagnosticService sink)
    {
        _sink = sink;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (InterestingSourceNames.Contains(eventSource.Name))
        {
            // Informational+ to keep volume reasonable; bump to Verbose for deeper traces.
            EnableEvents(eventSource, EventLevel.Informational, EventKeywords.All);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        try
        {
            string source = eventData.EventSource.Name;
            string evt = eventData.EventName ?? "?";

            string payload = string.Empty;
            if (eventData.Payload is { Count: > 0 } payloadList)
            {
                var sb = new System.Text.StringBuilder(64);
                for (int i = 0; i < payloadList.Count; i++)
                {
                    if (i > 0) sb.Append(' ');
                    var name = i < (eventData.PayloadNames?.Count ?? 0) ? eventData.PayloadNames![i] : $"arg{i}";
                    var val = payloadList[i];
                    sb.Append(name).Append('=').Append(val);
                }
                payload = sb.ToString();
            }

            _sink.Log("RuntimeEvt", $"[{source}/{evt}] {payload}");
        }
        catch
        {
            // never throw from EventListener callback
        }
    }
}
