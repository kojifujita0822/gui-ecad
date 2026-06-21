using System.Text;
using System.Text.Json;
using GuiEcad.Model;

namespace GuiEcad.Persistence;

/// <summary>.GCAD ファイル（JSON）の保存・読込。スキーマバージョン管理を行う。</summary>
public static class GcadSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static JsonSerializerOptions Options => JsonOptions.Default;

    /// <summary>LadderDocument を .GCAD ファイルへ保存する。doc.SchemaVersion を CurrentSchemaVersion に更新する。</summary>
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

    /// <summary>LadderDocument を JSON 文字列へシリアライズする（テスト・エクスポート向け）。doc は変更しない。</summary>
    public static string Serialize(LadderDocument doc)
    {
        var saved = doc.SchemaVersion;
        doc.SchemaVersion = CurrentSchemaVersion;
        try { return JsonSerializer.Serialize(doc, Options); }
        finally { doc.SchemaVersion = saved; }
    }
}
