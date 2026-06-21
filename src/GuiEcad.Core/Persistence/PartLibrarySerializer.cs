using System.Text;
using System.Text.Json;
using GuiEcad.Model;

namespace GuiEcad.Persistence;

/// <summary>外部ファイルとして書き出す自作パーツライブラリのルート（.GCAD とは独立。他ドキュメント間で共有可能）。</summary>
public sealed class PartLibraryFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<PartDefinition> Parts { get; set; } = new();
}

/// <summary>
/// 自作パーツライブラリ（<see cref="PartLibrary"/>）の外部ファイル入出力。
/// .GCAD への埋め込みとは別に、パーツ集合だけを単独 JSON（既定拡張子 .gcadparts）でやり取りする。
/// </summary>
public static class PartLibrarySerializer
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>ライブラリ内の全パーツを外部ファイルへ書き出す。</summary>
    public static void Save(PartLibrary library, string path)
        => File.WriteAllText(path, Serialize(library), Encoding.UTF8);

    public static string Serialize(PartLibrary library)
    {
        var file = new PartLibraryFile
        {
            SchemaVersion = CurrentSchemaVersion,
            Parts = library.ById.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        return JsonSerializer.Serialize(file, JsonOptions.Default);
    }

    /// <summary>外部ファイルからパーツ定義の一覧を読み込む。</summary>
    /// <exception cref="NotSupportedException">未知のスキーマバージョン。</exception>
    public static IReadOnlyList<PartDefinition> Load(string path)
        => Deserialize(File.ReadAllText(path, Encoding.UTF8));

    public static IReadOnlyList<PartDefinition> Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<PartLibraryFile>(json, JsonOptions.Default)
            ?? throw new InvalidDataException("Failed to deserialize part library file.");
        if (file.SchemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Unsupported part library schema version: {file.SchemaVersion} (expected {CurrentSchemaVersion}).");
        return file.Parts;
    }

    // ===== 単体パーツ I/O（1図形=1ファイル・拡張子 .gcadpart）=====

    /// <summary>1個の <see cref="PartDefinition"/> を単体ファイルへ書き出す（既定拡張子 .gcadpart）。</summary>
    public static void SaveOne(PartDefinition part, string path)
        => File.WriteAllText(path, SerializeOne(part), Encoding.UTF8);

    public static string SerializeOne(PartDefinition part)
        => JsonSerializer.Serialize(part, JsonOptions.Default);

    /// <summary>単体ファイル（.gcadpart）からパーツ定義を読み込む。</summary>
    public static PartDefinition LoadOne(string path)
        => DeserializeOne(File.ReadAllText(path, Encoding.UTF8));

    // 単体パーツ(.gcadpart)は PartDefinition を素のままシリアライズするためバージョンフィールドを持たない。
    // よってライブラリ/ドキュメントのような SchemaVersion 検査は行わない（非対称は意図的）。
    public static PartDefinition DeserializeOne(string json)
        => JsonSerializer.Deserialize<PartDefinition>(json, JsonOptions.Default)
            ?? throw new InvalidDataException("Failed to deserialize part definition.");
}
