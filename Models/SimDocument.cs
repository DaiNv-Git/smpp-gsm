using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.Serializers;

namespace GsmAgent.Models;

/// <summary>
/// MongoDB document mapping cho collection "sims".
/// Tương thích 100% với Sim.java entity trong simsmart-gsm (Java).
/// Java dùng UUID.randomUUID().toString() cho @Id → _id là String.
/// </summary>
[BsonIgnoreExtraElements]
public class SimDocument
{
    [BsonId]
    [BsonSerializer(typeof(FlexibleIdSerializer))]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [BsonElement("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [BsonElement("revenue")]
    public double? Revenue { get; set; }

    [BsonElement("status")]
    public string Status { get; set; } = "INACTIVE";

    [BsonElement("countryCode")]
    public string CountryCode { get; set; } = "JP";

    [BsonElement("deviceName")]
    public string? DeviceName { get; set; }

    [BsonElement("comName")]
    public string? ComName { get; set; }

    [BsonElement("simProvider")]
    public string? SimProvider { get; set; }

    [BsonElement("ccid")]
    public string? Ccid { get; set; }

    [BsonElement("imsi")]
    public string? Imsi { get; set; }

    [BsonElement("agentId")]
    public string? AgentId { get; set; }

    [BsonElement("content")]
    public string? Content { get; set; }

    [BsonElement("lastUpdated")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? LastUpdated { get; set; }

    [BsonElement("missCount")]
    public int MissCount { get; set; }

    [BsonElement("activeDate")]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ActiveDate { get; set; }

    [BsonElement("allowSms")]
    public bool AllowSms { get; set; } = true;

    [BsonElement("smsFailedCount")]
    public int SmsFailedCount { get; set; }

    // Java Spring Data adds _class field — preserve it for compatibility
    [BsonElement("_class")]
    public string? JavaClass { get; set; }
}

/// <summary>
/// Custom serializer cho _id: ĐỌC được cả ObjectId lẫn String, GHI ra String.
/// Tương thích cả Java UUID lẫn MongoDB native ObjectId.
/// </summary>
public class FlexibleIdSerializer : SerializerBase<string>
{
    public override string Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonType = context.Reader.GetCurrentBsonType();
        return bsonType switch
        {
            BsonType.String => context.Reader.ReadString(),
            BsonType.ObjectId => context.Reader.ReadObjectId().ToString(),
            _ => throw new FormatException($"Cannot deserialize _id from BsonType {bsonType}")
        };
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, string value)
    {
        // Luôn ghi String — giống Java UUID.randomUUID().toString()
        context.Writer.WriteString(value);
    }
}
