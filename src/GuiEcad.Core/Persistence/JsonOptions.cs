using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace GuiEcad.Persistence;

/// <summary>永続化(JSON)の共通シリアライズ設定。.GCAD とパーツライブラリで同一にする（設定の二重保守を防ぐ）。</summary>
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
