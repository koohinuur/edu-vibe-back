using System.Text.Json;
using System.Text.Json.Serialization;

namespace LMS.WebApi.Serialization;

/// <summary>
/// Reads enums from EITHER a JSON number (4) OR a string — the enum name
/// ("Speaking", case-insensitive) or a numeric string ("4") — and always WRITES
/// them as numbers. The numeric output keeps the existing contract the numeric
/// frontend enum constants rely on, while the tolerant input stops a client that
/// echoes a string enum value back (e.g. after it was received as a string) from
/// failing the whole request body with the opaque "The cmd field is required."
/// (a model-binding null caused by one enum property that couldn't convert).
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert))!;
}

public sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number when reader.TryGetInt64(out var n):
                return (T)Enum.ToObject(typeof(T), n);
            case JsonTokenType.String:
                var s = reader.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    if (Enum.TryParse<T>(s, ignoreCase: true, out var byName)) return byName;
                    if (long.TryParse(s, out var byNum)) return (T)Enum.ToObject(typeof(T), byNum);
                }
                throw new JsonException($"Cannot convert '{s}' to enum {typeof(T).Name}.");
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for enum {typeof(T).Name}.");
        }
    }

    // Numeric output — unchanged contract for the numeric-enum clients.
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(Convert.ToInt64(value));
}
