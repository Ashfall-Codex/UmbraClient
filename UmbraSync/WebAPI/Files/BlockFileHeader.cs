using System.Globalization;

namespace UmbraSync.WebAPI.Files;

internal static class BlockFileHeader
{
    private const int MaxHashLength = 64;
    private const int MaxLengthDigits = 20;

    public static (string FileHash, long FileLengthBytes) Read(Stream blockStream)
    {
        List<char> hashName = [];
        List<char> fileLength = [];
        var separator = ReadChar(blockStream);
        if (separator != '#') throw new InvalidDataException("Data is invalid, first char is not #");

        bool readHash = false;
        while (true)
        {
            var readChar = ReadChar(blockStream);
            if (readChar == ':')
            {
                readHash = true;
                continue;
            }
            if (readChar == '#') break;
            if (!readHash)
            {
                if (hashName.Count >= MaxHashLength)
                    throw new InvalidDataException($"Block file header hash exceeds {MaxHashLength} chars");
                hashName.Add(readChar);
            }
            else
            {
                if (fileLength.Count >= MaxLengthDigits)
                    throw new InvalidDataException($"Block file header length exceeds {MaxLengthDigits} chars");
                fileLength.Add(readChar);
            }
        }
        if (fileLength.Count == 0)
            fileLength.Add('0');
        return (string.Join("", hashName), long.Parse(string.Join("", fileLength), CultureInfo.InvariantCulture));
    }

    private static char ReadChar(Stream stream)
    {
        int value = stream.ReadByte();
        if (value == -1) throw new EndOfStreamException();
        return (char)(byte)value;
    }
}
