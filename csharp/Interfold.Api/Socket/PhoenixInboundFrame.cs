using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Interfold.Contracts;

namespace Interfold.Api.Socket;

/// <summary>
/// A parsed inbound Phoenix frame — either the array format
/// (<c>[join_ref, ref, topic, event, payload]</c>) or the object format
/// (<c>{topic, event, payload, ref, join_ref}</c>). Replaces the previous 7-out-param
/// parser; <see cref="ReplyAsArrayFrame"/> records which format the client spoke so
/// replies mirror it.
///
/// <para>
/// Ref semantics are intentionally asymmetric and preserved from the original parser:
/// the array format keeps JSON null refs as null (so replies mirror null), while the
/// object format normalises present-but-string refs and leaves absent ones null.
/// </para>
/// </summary>
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
                    // Preserve JSON null so replies can mirror it back as null (not "").
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
