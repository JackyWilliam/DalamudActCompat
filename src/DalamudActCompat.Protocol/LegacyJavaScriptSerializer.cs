using System.Text.Json;

namespace DalamudActCompat.Protocol;

/// <summary>
/// Provides the JavaScriptSerializer surface used by legacy ACT plugins without
/// loading the .NET Framework-only System.Web.Extensions assembly.
/// </summary>
public sealed class LegacyJavaScriptSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize(object? value)
        => value is null
            ? "null"
            : JsonSerializer.Serialize(value, value.GetType(), Options);

    public T? Deserialize<T>(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (typeof(T) == typeof(object))
        {
            return (T?)DeserializeObject(input);
        }

        return JsonSerializer.Deserialize<T>(input, Options);
    }

    public object? DeserializeObject(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var document = JsonDocument.Parse(input);
        return ConvertElement(document.RootElement);
    }

    private static object? ConvertElement(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ConvertElement(property.Value),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertElement)
                .ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number when element.TryGetInt64(out var longInteger) => longInteger,
            JsonValueKind.Number when element.TryGetDecimal(out var decimalNumber) => decimalNumber,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => throw new JsonException($"Unsupported JSON token {element.ValueKind}."),
        };
}
