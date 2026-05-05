using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using System.Buffers;
using UmbraSync.Services.Network;

namespace UmbraSync.WebAPI.SignalR;

internal sealed class DiagnosticHubProtocol : IHubProtocol
{
    private readonly IHubProtocol _inner;
    private readonly NetworkDiagnosticService _diag;

    public DiagnosticHubProtocol(IHubProtocol inner, NetworkDiagnosticService diag)
    {
        _inner = inner;
        _diag = diag;
    }

    public string Name => _inner.Name;
    public int Version => _inner.Version;
    public TransferFormat TransferFormat => _inner.TransferFormat;

    public bool IsVersionSupported(int version) => _inner.IsVersionSupported(version);

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message)
    {
        var bytes = _inner.GetMessageBytes(message);
        LogSend(message, bytes.Length);
        return bytes;
    }

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output)
    {
        // We capture size by tracking writer advance via a counting wrapper.
        var counting = new CountingBufferWriter(output);
        _inner.WriteMessage(message, counting);
        LogSend(message, counting.BytesWritten);
    }

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HubMessage? message)
    {
        long lenBefore = input.Length;
        bool result = _inner.TryParseMessage(ref input, binder, out message);
        if (result && message != null)
        {
            int sizeBytes = (int)(lenBefore - input.Length);
            LogRecv(message, sizeBytes);
        }
        return result;
    }

    private void LogSend(HubMessage message, int sizeBytes)
    {
        var (kind, label) = Describe(message);
        _diag.LogSend(kind, label, sizeBytes);
    }

    private void LogRecv(HubMessage message, int sizeBytes)
    {
        var (kind, label) = Describe(message);
        _diag.LogRecv(kind, label, sizeBytes);
    }

    private static (string Kind, string Label) Describe(HubMessage message) => message switch
    {
        InvocationMessage inv => ("Invocation", inv.Target ?? "?"),
        StreamInvocationMessage si => ("StreamInvoke", si.Target ?? "?"),
        StreamItemMessage => ("StreamItem", "(item)"),
        CompletionMessage comp => ("Completion", comp.InvocationId ?? "?"),
        CancelInvocationMessage => ("CancelInvoke", "(cancel)"),
        PingMessage => ("Ping", "(keepalive)"),
        CloseMessage close => ("Close", close.Error ?? "(graceful)"),
        AckMessage => ("Ack", "(ack)"),
        SequenceMessage => ("Sequence", "(seq)"),
        _ => (message.GetType().Name, "?"),
    };

    private sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private readonly IBufferWriter<byte> _inner;
        public int BytesWritten { get; private set; }

        public CountingBufferWriter(IBufferWriter<byte> inner) { _inner = inner; }
        public void Advance(int count) { _inner.Advance(count); BytesWritten += count; }
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
    }
}
