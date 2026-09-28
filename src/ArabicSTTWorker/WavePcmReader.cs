using System.Buffers.Binary;

namespace EgyptianDictation.ArabicSTTWorker;

internal static class WavePcmReader
{
    public static (float[] Samples, double Seconds) Read16KhzMono(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException("ملف الصوت ليس WAV صالحًا.");
        reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException("ملف الصوت ليس WAVE صالحًا.");
        ushort format = 0, channels = 0, bits = 0;
        uint rate = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = new string(reader.ReadChars(4));
            var size = reader.ReadUInt32();
            if (chunk == "fmt ")
            {
                if (size < 16)
                    throw new InvalidDataException("قسم fmt في ملف WAV غير صالح.");
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                stream.Position += size - 16;
            }
            else if (chunk == "data")
                data = reader.ReadBytes(checked((int)size));
            else
                stream.Position += size;
            if ((size & 1) != 0 && stream.Position < stream.Length)
                stream.Position++;
        }
        if (format != 1 || channels < 1 || rate < 8_000 || bits != 16 || data is null)
            throw new InvalidDataException("ملف الصوت يجب أن يكون WAV PCM 16-bit صالحًا.");
        var frames = data.Length / (2 * channels);
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (frame * channels + channel) * 2;
                sum += BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)) / 32768f;
            }
            mono[frame] = sum / channels;
        }
        if (rate == 16_000)
            return (mono, mono.Length / 16000d);
        var outputLength = checked((int)Math.Round(mono.Length * 16000d / rate));
        var output = new float[outputLength];
        var scale = rate / 16000d;
        for (var index = 0; index < output.Length; index++)
        {
            var source = index * scale;
            var left = Math.Min((int)source, mono.Length - 1);
            var right = Math.Min(left + 1, mono.Length - 1);
            var fraction = (float)(source - left);
            output[index] = mono[left] + (mono[right] - mono[left]) * fraction;
        }
        return (output, mono.Length / (double)rate);
    }
}
