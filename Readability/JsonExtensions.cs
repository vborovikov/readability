namespace Readability;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

static class JsonExtensions
{
    public static bool TryGetString(this JsonElement element, string propertyName, [NotNullWhen(true)] out string? value)
    {
        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty(propertyName, out var property) => property.GetString(),
            _ => default
        };

        return value is not null;
    }
}
