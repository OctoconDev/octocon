using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Interfold.Contracts;

namespace Interfold.Api.Socket;

/// <summary>Parsed inbound Phoenix frame — array (<c>[join_ref, ref, topic, event, payload]</c>)
/// or object (<c>{topic, event, payload, ref, join_ref}</c>). <see cref="ReplyAsArrayFrame"/>
/// records which so replies mirror the client's shape. Array-format null refs stay null;
/// object-format absent refs stay null while present ones normalise to string.</summary>
internal sealed record PhoenixInboundFrame(
    string EventName,
    string Topic,
    JsonElement? Payload,
    string? Reference,
    string? JoinReference,
    bool ReplyAsArrayFrame)
{
    public static bool TryParse(string frame, [NotNullWhen(true)] out PhoenixInboundFrame? result)
    {
        result = null;

        var trimmed = frame.TrimStart();

        if (trimmed.StartsWith('['))
        {
            try
            {
                using var arrayDoc = JsonDocument.Parse(frame);
                var root = arrayDoc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 5)
                {
                    return false;
                }

                var joinRefElement = root[0];
                var refElement = root[1];
                var topicElement = root[2];
                var eventElement = root[3];

                if (topicElement.ValueKind != JsonValueKind.String
                    || eventElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                result = new PhoenixInboundFrame(
                    EventName: eventElement.GetString() ?? string.Empty,
                    Topic: topicElement.GetString() ?? PhoenixEventNames.PhoenixTopic,
                    Payload: root[4].Clone(),
                    Reference: refElement.ValueKind == JsonValueKind.String ? refElement.GetString() : null,
                    JoinReference: joinRefElement.ValueKind == JsonValueKind.String ? joinRefElement.GetString() : null,
                    ReplyAsArrayFrame: true);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(frame);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("event", out var eventProp)
                || eventProp.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var topic = PhoenixEventNames.PhoenixTopic;
            if (root.TryGetProperty("topic", out var topicProp)
                && topicProp.ValueKind == JsonValueKind.String)
            {
                topic = topicProp.GetString() ?? topic;
            }

            JsonElement? payload = null;
            if (root.TryGetProperty("payload", out var payloadProp))
            {
                payload = payloadProp.Clone();
            }

            string? reference = null;
            if (root.TryGetProperty("ref", out var refProp)
                && refProp.ValueKind == JsonValueKind.String)
            {
                reference = refProp.GetString() ?? string.Empty;
            }

            string? joinReference = null;
            if (root.TryGetProperty("join_ref", out var joinRefProp)
                && joinRefProp.ValueKind == JsonValueKind.String)
            {
                joinReference = joinRefProp.GetString() ?? string.Empty;
            }

            result = new PhoenixInboundFrame(
                EventName: eventProp.GetString() ?? string.Empty,
                Topic: topic,
                Payload: payload,
                Reference: reference,
                JoinReference: joinReference,
                ReplyAsArrayFrame: false);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
