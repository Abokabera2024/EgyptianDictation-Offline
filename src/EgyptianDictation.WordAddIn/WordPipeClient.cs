using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.WordAddIn;

internal sealed class WordPipeClient : IDisposable
{
#if PREVIEW
    private const string PipeName = "EgyptianDictation.Preview.v2";
#else
    private const string PipeName = Protocol.DefaultPipeName;
#endif
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _sequence;
    private readonly object _sync = new();

    public MessageEnvelope Send(string type, string sessionId, string? payloadJson = null)
    {
        var request = new MessageEnvelope
        {
            Type = type,
            SessionId = sessionId,
            SequenceNumber = Interlocked.Increment(ref _sequence),
            CorrelationId = Guid.NewGuid().ToString("N"),
            PayloadJson = payloadJson
        };
        lock (_sync)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    EnsureConnected();
                    _writer!.WriteLine(Serialize(request));
                    var read = _reader!.ReadLineAsync();
                    if (Task.WhenAny(read, Task.Delay(15_000)).GetAwaiter().GetResult() != read)
                    {
                        ResetConnection();
                        throw new TimeoutException("The dictation host did not respond.");
                    }
                    var line = read.GetAwaiter().GetResult() ?? throw new EndOfStreamException("The dictation host disconnected.");
                    var response = Deserialize<MessageEnvelope>(line);
                    // Older hosts omit the correlation ID from error replies. The pipe is
                    // strictly request/response, so surface that error instead of hiding it.
                    if (response.CorrelationId != request.CorrelationId && response.Type != MessageTypes.Error)
                        throw new InvalidDataException("Mismatched dictation response.");
                    return response;
                }
                catch (Exception ex) when (attempt == 0 && ex is IOException or TimeoutException)
                {
                    ResetConnection();
                }
            }
        }
    }

    public static T DeserializePayload<T>(MessageEnvelope envelope) =>
        Deserialize<T>(envelope.PayloadJson ?? throw new InvalidDataException("Missing response payload."));

    public static string DeserializeErrorMessage(MessageEnvelope envelope)
    {
        string payload = envelope.PayloadJson ?? string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
            return "حدث خطأ في محرك الإملاء.";
        try
        {
            return Deserialize<ErrorPayload>(payload).Message ?? payload;
        }
        catch
        {
            return payload;
        }
    }

    public void Dispose()
    {
        lock (_sync) ResetConnection();
    }

    private void ResetConnection()
    {
        try { _writer?.Dispose(); } catch (IOException) { }
        try { _reader?.Dispose(); } catch (IOException) { }
        try { _pipe?.Dispose(); } catch (IOException) { }
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private void EnsureConnected()
    {
        if (_pipe?.IsConnected == true)
            return;
        ResetConnection();
        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            _pipe.Connect(400);
        }
        catch (TimeoutException)
        {
            LaunchHost();
            _pipe.Connect(20_000);
        }
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
    }

    private static void LaunchHost()
    {
        var addInDirectory = Path.GetDirectoryName(typeof(WordPipeClient).Assembly.Location)!;
        var installed = Environment.GetEnvironmentVariable("EGYPTIAN_DICTATION_HOST_PATH") ??
            Path.GetFullPath(Path.Combine(addInDirectory, "..", "app", "EgyptianDictation.Host.exe"));
        if (!File.Exists(installed))
            throw new FileNotFoundException("لم يتم العثور على محرك الإملاء. أعد تشغيل المثبت.", installed);
        var start = new ProcessStartInfo(installed, "--headless") { UseShellExecute = false, CreateNoWindow = true };
#if PREVIEW
        start.EnvironmentVariables["EGYPTIAN_DICTATION_PIPE_NAME"] = PipeName;
#endif
        Process.Start(start);
    }

    private static string Serialize<T>(T value)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static T Deserialize<T>(string json)
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (T)(serializer.ReadObject(stream) ?? throw new InvalidDataException("Invalid JSON response."));
    }


    [System.Runtime.Serialization.DataContract]
    private sealed class ErrorPayload
    {
        [System.Runtime.Serialization.DataMember(Name = "message")]
        public string? Message { get; set; }
    }
}
