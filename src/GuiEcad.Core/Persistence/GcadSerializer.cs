using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using GuiEcad.Model;

namespace GuiEcad.Persistence;

/// <summary>.GCAD ファイル（JSON）の保存・読込。スキーマバージョン管理を行う。</summary>
public static class GcadSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>LadderDocument を .GCAD ファイルへ保存する。</summary>
    public static void Save(LadderDocument doc, string path)
    {
        doc.SchemaVersion = CurrentSchemaVersion;
        var json = JsonSerializer.Serialize(doc, Options);
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    /// <summary>.GCAD ファイルから LadderDocument を読み込む。</summary>
    /// <exception cref="NotSupportedException">未知のスキーマバージョン。</exception>
    public static LadderDocument Load(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        return Deserialize(json);
    }

    /// <summary>JSON 文字列から LadderDocument を復元する（テスト・インポート向け）。</summary>
    public static LadderDocument Deserialize(string json)
    {
        var doc = JsonSerializer.Deserialize<LadderDocument>(json, Options)
            ?? throw new InvalidDataException("Failed to deserialize .GCAD document.");
        if (doc.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Unsupported .GCAD schema version: {doc.SchemaVersion} (expected {CurrentSchemaVersion}).");
        return doc;
    }

    /// <summary>LadderDocument を JSON 文字列へシリアライズする（テスト・エクスポート向け）。</summary>
    public static string Serialize(LadderDocument doc)
    {
        doc.SchemaVersion = CurrentSchemaVersion;
        return JsonSerializer.Serialize(doc, Options);
    }
}
