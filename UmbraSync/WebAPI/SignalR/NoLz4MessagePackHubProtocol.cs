using System.Buffers;
using MessagePack;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;

namespace UmbraSync.WebAPI.SignalR;

public sealed class NoLz4MessagePackHubProtocol : IHubProtocol
{
    private readonly MessagePackHubProtocol _inner;

    public NoLz4MessagePackHubProtocol(IFormatterResolver resolver)
    {
        var hubOptions = new MessagePackHubProtocolOptions
        {
            SerializerOptions = MessagePackSerializerOptions.Standard
                .WithCompression(MessagePackCompression.None)
                .WithResolver(resolver),
        };
        _inner = new MessagePackHubProtocol(Options.Create(hubOptions));
    }

    public string Name => "messagepack-no-lz4";

    public int Version => 1;

    public TransferFormat TransferFormat => TransferFormat.Binary;

    public bool IsVersionSupported(int version) => version == Version;

    public ReadOnlyMemory<byte> GetMessageBytes(HubMessage message) => _inner.GetMessageBytes(message);

    public void WriteMessage(HubMessage message, IBufferWriter<byte> output) => _inner.WriteMessage(message, output);

    public bool TryParseMessage(ref ReadOnlySequence<byte> input, IInvocationBinder binder, out HubMessage message)
        => _inner.TryParseMessage(ref input, binder, out message);
}
