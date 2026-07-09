using System.Buffers.Binary;

namespace Lagrange.OneBot.Utility;

public static class AudioHelper
{
    public enum AudioFormat
    {
        Unknown,
        Wav,
        Mp3,
        SilkV3,
        TenSilkV3,
        Amr,
        Ogg,
    }

    /// <summary>
    /// Detect audio type
    /// </summary>
    /// <param name="data"></param>
    /// <param name="type"></param>
    /// <returns></returns>
    [Obsolete("public static AudioFormat DetectAudio(byte[] data)")]
    public static bool DetectAudio(byte[] data, out AudioFormat type)
    {
        return (type = DetectAudio(data)) != AudioFormat.Unknown;
    }

    private const uint RIFF_HEAD = 0x52494646U;
    private const uint RF64_HEAD = 0x52463634U;
    private const uint WAVE_HEAD = 0x57415645U;
    private const uint FMT_CHUNK = 0x666D7420U;
    private const uint DATA_CHUNK = 0x64617461U;

    // TENSILKV3
    //     #  !  S  I  L  K
    // +--+--+--+--+--+--+--+
    // |02|23|21|53|49|4C|4B|
    // +--+--+--+--+--+--+--+
    // 0           4        7
    private const ulong TENSILKV3_HEAD_LOWER = 0x02232153494C4B00UL;

    // AMR
    //  #  !  A  M  R
    // +--+--+--+--+--+
    // |23|21|41|4D|52|
    // +--+--+--+--+--+
    // 0           4  5
    private const ulong AMR_HEAD_LOWER = 0x2321414D52000000UL;

    // SILKV3
    //  #  !  S  I  L  K
    // +--+--+--+--+--+--+
    // |23|21|53|49|4C|4B|
    // +--+--+--+--+--+--+
    // 0           4     6
    private const ulong SILKV3_HEAD_LOWER = 0x232153494C4B0000UL;

    // Ogg
    //  O  g  g  S
    // +--+--+--+--+
    // |4F|67|67|53|
    // +--+--+--+--+
    // 0           4
    private const ulong OGG_HEAD_LOWER = 0x4F67675300000000UL;

    // MP3 ID3
    //  I  D  3
    // +--+--+--+
    // |49|44|33|
    // +--+--+--+
    // 0        3
    private const ulong MP3ID3_HEAD_LOWER = 0x4944330000000000UL;

    // MP3 no ID3
    //  ÿ  û
    // +--+--+
    // |FF|F2|
    // +--+--+
    // 0     2
    private const ulong MP3FFF2_HEAD_LOWER = 0xFFF2000000000000UL;

    // MP3 no ID3
    //  ÿ  û
    // +--+--+
    // |FF|F3|
    // +--+--+
    // 0     2
    private const ulong MP3FFF3_HEAD_LOWER = 0xFFF3000000000000UL;

    // MP3 no ID3
    //  ÿ  û
    // +--+--+
    // |FF|FB|
    // +--+--+
    // 0     2
    private const ulong MP3FFFB_HEAD_LOWER = 0xFFFB000000000000UL;

    /// <summary>
    /// Detect audio type
    /// </summary>
    /// <param name="data"></param>
    /// <param name="type"></param>
    /// <returns></returns>
    public static AudioFormat DetectAudio(byte[] data)
    {
        if (data.Length < sizeof(ulong)) return AudioFormat.Unknown;

        if (IsWaveFile(data)) return AudioFormat.Wav;

        ulong lower = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(0, sizeof(ulong)));

        if ((lower & TENSILKV3_HEAD_LOWER) == TENSILKV3_HEAD_LOWER) return AudioFormat.TenSilkV3;

        if ((lower & AMR_HEAD_LOWER) == AMR_HEAD_LOWER) return AudioFormat.Amr;

        if ((lower & SILKV3_HEAD_LOWER) == SILKV3_HEAD_LOWER) return AudioFormat.SilkV3;

        if ((lower & OGG_HEAD_LOWER) == OGG_HEAD_LOWER) return AudioFormat.Ogg;

        if ((lower & MP3ID3_HEAD_LOWER) == MP3ID3_HEAD_LOWER) return AudioFormat.Mp3;

        if ((lower & MP3FFF2_HEAD_LOWER) == MP3FFF2_HEAD_LOWER) return AudioFormat.Mp3;

        if ((lower & MP3FFF3_HEAD_LOWER) == MP3FFF3_HEAD_LOWER) return AudioFormat.Mp3;

        if ((lower & MP3FFFB_HEAD_LOWER) == MP3FFFB_HEAD_LOWER) return AudioFormat.Mp3;

        return AudioFormat.Unknown;
    }

    private static bool IsWaveFile(byte[] data)
    {
        if (data.Length < 12) return false;

        uint riff = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, sizeof(uint)));
        uint formType = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, sizeof(uint)));
        if (riff is not (RIFF_HEAD or RF64_HEAD) || formType != WAVE_HEAD) return false;

        for (int offset = 12; offset + 8 <= data.Length;)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, sizeof(uint)));
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, sizeof(uint)));
            if (chunkId == FMT_CHUNK) return true;

            long nextOffset = (long)offset + 8 + chunkSize + (chunkSize & 1);
            if (nextOffset <= offset || nextOffset > data.Length) return false;
            offset = (int)nextOffset;
        }

        return false;
    }

    public static byte[] NormalizeWaveForDecoder(byte[] data)
    {
        if (!IsWaveFile(data)) return data;
        if (!TryFindWaveChunk(data, FMT_CHUNK, out int fmtOffset, out uint fmtSize)) return data;
        if (!TryFindWaveChunk(data, DATA_CHUNK, out int dataOffset, out uint dataSize)) return data;
        if (fmtSize == uint.MaxValue || dataSize == uint.MaxValue) return data;
        if (fmtSize > int.MaxValue - 9 || dataSize > int.MaxValue - 9) return data;
        if (fmtOffset == 12) return data;

        int fmtLength = GetWaveChunkLength(fmtSize);
        int dataLength = GetWaveChunkLength(dataSize);
        if (fmtOffset + fmtLength > data.Length || dataOffset + dataLength > data.Length) return data;
        long normalizedLength = 12L + fmtLength + dataLength;
        if (normalizedLength > int.MaxValue) return data;

        var normalized = new byte[(int)normalizedLength];
        BinaryPrimitives.WriteUInt32BigEndian(normalized.AsSpan(0, sizeof(uint)), RIFF_HEAD);
        BinaryPrimitives.WriteUInt32LittleEndian(normalized.AsSpan(4, sizeof(uint)), (uint)(normalized.Length - 8));
        BinaryPrimitives.WriteUInt32BigEndian(normalized.AsSpan(8, sizeof(uint)), WAVE_HEAD);
        data.AsSpan(fmtOffset, fmtLength).CopyTo(normalized.AsSpan(12));
        data.AsSpan(dataOffset, dataLength).CopyTo(normalized.AsSpan(12 + fmtLength));

        return normalized;
    }

    private static bool TryFindWaveChunk(byte[] data, uint targetChunk, out int chunkOffset, out uint chunkSize)
    {
        chunkOffset = 0;
        chunkSize = 0;

        for (int offset = 12; offset + 8 <= data.Length;)
        {
            uint chunkId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, sizeof(uint)));
            chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, sizeof(uint)));
            if (chunkId == targetChunk)
            {
                chunkOffset = offset;
                return true;
            }

            if (chunkSize > int.MaxValue - 9) return false;
            long nextOffset = (long)offset + GetWaveChunkLength(chunkSize);
            if (nextOffset <= offset || nextOffset > data.Length) return false;
            offset = (int)nextOffset;
        }

        return false;
    }

    private static int GetWaveChunkLength(uint chunkSize) => checked((int)(8 + chunkSize + (chunkSize & 1)));

    private static readonly byte[] _silk_end = [0xFF, 0xFF];

    public static double GetSilkTime(byte[] data, int offset = 0)
    {
        int count = 0;
        for (int i = 9 + offset; i < data.Length && !data.AsSpan(i, 2).SequenceEqual(_silk_end); i += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i, 2)))
        {
            count++;
        }
        // Because the silk encoder encodes each 20ms sample as a block,
        // So that we can calculate the total time easily.
        return count * 0.02;
    }

    /// <summary>
    /// Get silk total time
    /// </summary>
    /// <param name="data"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    public static double GetTenSilkTime(byte[] data)
    {
        int count = 0;
        for (int i = 10; i < data.Length; i += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i, 2)))
        {
            count++;
        }
        // Because the silk encoder encodes each 20ms sample as a block,
        // So that we can calculate the total time easily.
        return count * 0.02;
    }
}
