using MessagePack;

namespace UmbraSync.Modules.Housing.Services;

public static class StageSharePayloadCodec
{
    public const byte Version = 1;

    public static byte[] Encode(StageSharePlaintextV1 plaintext)
    {
        byte[] body = MessagePackSerializer.Serialize(plaintext);
        byte[] data = new byte[body.Length + 1];
        data[0] = Version;
        Buffer.BlockCopy(body, 0, data, 1, body.Length);
        return data;
    }

    public static StageSharePlaintextV1? TryDecode(ReadOnlyMemory<byte> data)
    {
        if (data.Length < 2 || data.Span[0] != Version) return null;
        try
        {
            var plaintext = MessagePackSerializer.Deserialize<StageSharePlaintextV1>(data[1..]);
            return plaintext == null || string.IsNullOrEmpty(plaintext.DefinitionJson) ? null : plaintext;
        }
        catch (MessagePackSerializationException)
        {
            return null;
        }
    }
}
