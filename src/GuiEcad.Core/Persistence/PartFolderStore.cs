using GuiEcad.Model;

namespace GuiEcad.Persistence;

/// <summary>走査結果の1件。<paramref name="Category"/> は「図形/」からの相対カテゴリ（直下="" / 自作="自作"）。</summary>
public sealed record PartFolderEntry(string Category, string FilePath, PartDefinition Definition);

/// <summary>
/// 図形を実フォルダで一元管理するストア。
/// ルート「図形/」直下に基本図形、「図形/自作/」に自作図形を 1図形=1ファイル（.gcadpart）で置く。
/// .GCAD への埋め込み（ドキュメント自己完結）とは別に、フォルダを「マスター／呼び出し元」とする。
/// </summary>
public sealed class PartFolderStore
{
    public const string PartExtension = ".gcadpart";
    private const string CustomFolderName = "自作";

    /// <summary>図形ルート（基本図形を置く）。</summary>
    public string RootDir { get; }
    /// <summary>自作図形フォルダ（図形/自作/）。</summary>
    public string CustomDir { get; }

    public PartFolderStore(string rootDir)
    {
        RootDir = rootDir;
        CustomDir = Path.Combine(rootDir, CustomFolderName);
    }

    /// <summary>既定の保存先（マイドキュメント\GuiEcad\図形）でストアを作る。</summary>
    public static PartFolderStore CreateDefault()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return new PartFolderStore(Path.Combine(docs, "GuiEcad", "図形"));
    }

    /// <summary>ルートと自作フォルダを作成（既存なら何もしない）。</summary>
    public void EnsureFolders()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(CustomDir);
    }

    /// <summary>全 .gcadpart を相対カテゴリ付きで列挙する（UI 階層構築の元データ）。読込失敗ファイルは無視。</summary>
    public IReadOnlyList<PartFolderEntry> Enumerate()
    {
        var result = new List<PartFolderEntry>();
        if (!Directory.Exists(RootDir)) return result;

        foreach (var file in Directory.EnumerateFiles(RootDir, "*" + PartExtension, SearchOption.AllDirectories))
        {
            PartDefinition def;
            try { def = PartLibrarySerializer.LoadOne(file); }
            catch { continue; }   // 壊れたファイルはスキップ（起動を止めない）

            var dir = Path.GetDirectoryName(file) ?? RootDir;
            string category = Path.GetRelativePath(RootDir, dir);
            if (category == ".") category = "";
            result.Add(new PartFolderEntry(category.Replace('\\', '/'), file, def));
        }

        return result
            .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>自作図形を「図形/自作/&lt;名前&gt;.gcadpart」へ保存し、書き出したパスを返す。</summary>
    public string SaveCustom(PartDefinition part)
    {
        EnsureFolders();
        string path = Path.Combine(CustomDir, SafeFileName(part.Name) + PartExtension);
        PartLibrarySerializer.SaveOne(part, path);
        return path;
    }

    /// <summary>指定パスの図形ファイルを削除する（存在しなければ何もしない）。</summary>
    public void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>同梱の基本図形を「図形/」直下へ展開する（冪等：既存ファイルは上書きしない）。展開した数を返す。</summary>
    public int SeedBasics()
    {
        EnsureFolders();
        int seeded = 0;
        foreach (var def in BasicPartTemplates.All())
        {
            string path = Path.Combine(RootDir, SafeFileName(def.Name) + PartExtension);
            if (File.Exists(path)) continue;
            PartLibrarySerializer.SaveOne(def, path);
            seeded++;
        }
        return seeded;
    }

    /// <summary>図形名をファイル名に使える形へ正規化する（無効文字を '_' に置換・空名は "part"）。</summary>
    private static string SafeFileName(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return "part";
        foreach (char c in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(c, '_');
        return trimmed;
    }
}
