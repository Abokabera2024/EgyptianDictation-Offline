using System.Text.Json;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.Core.Ipc;

public sealed class PipeMessageCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public string Serialize(MessageEnvelope message)
    {
        Validate(message);
        return JsonSerializer.Serialize(message, Options);
    }

    public MessageEnvelope Deserialize(string line)
    {
        var message = JsonSerializer.Deserialize<MessageEnvelope>(line, Options)
            ?? throw new InvalidDataException("The message is empty.");
        Validate(message);
        return message;
    }

    public static string Payload<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? ReadPayload<T>(MessageEnvelope envelope) =>
        string.IsNullOrWhiteSpace(envelope.PayloadJson)
            ? default
            : JsonSerializer.Deserialize<T>(envelope.PayloadJson, Options);

    private static void Validate(MessageEnvelope message)
    {
        if (message.ProtocolVersion != Protocol.CurrentVersion)
            throw new InvalidDataException($"Unsupported protocol version {message.ProtocolVersion}.");
        if (string.IsNullOrWhiteSpace(message.Type))
            throw new InvalidDataException("Message type is required.");
        if (message.SequenceNumber < 0)
            throw new InvalidDataException("Sequence number cannot be negative.");
    }
}

