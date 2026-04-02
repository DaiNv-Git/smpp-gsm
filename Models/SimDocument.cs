using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace GsmAgent.Models;

/// <summary>
/// MongoDB document mapping cho collection "sims".
/// Tương thích 100% với Sim.java entity trong simsmart-gsm (Java).
/// </summary>
[BsonIgnoreExtraElements]
public class SimDocument
{
    [BsonId]
    [BsonSerializer(typeof(StringObjectIdBsonSerializer))]
    public string Id { get; set; } = ObjectId.GenerateNewId().ToString();

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

public class StringObjectIdBsonSerializer : MongoDB.Bson.Serialization.Serializers.SerializerBase<string>
{
    public override string Deserialize(MongoDB.Bson.Serialization.BsonDeserializationContext context, MongoDB.Bson.Serialization.BsonDeserializationArgs args)
    {
        var bsonType = context.Reader.GetCurrentBsonType();
        if (bsonType == BsonType.ObjectId)
        {
            return context.Reader.ReadObjectId().ToString();
        }
        else if (bsonType == BsonType.String)
        {
            return context.Reader.ReadString();
        }
        else
        {
            throw new FormatException($"Cannot deserialize string from BsonType {bsonType}");
        }
    }

    public override void Serialize(MongoDB.Bson.Serialization.BsonSerializationContext context, MongoDB.Bson.Serialization.BsonSerializationArgs args, string value)
    {
        if (ObjectId.TryParse(value, out var objectId))
        {
            context.Writer.WriteObjectId(objectId);
        }
        else
        {
            context.Writer.WriteString(value);
        }
    }
}
