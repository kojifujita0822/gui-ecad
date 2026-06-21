using System.Xml.Linq;
using Xunit;

namespace GuiEcad.Tests;

/// <summary>
/// XAML の VisualState 競合を静的解析で検出する。
///
/// WinUI では別々の VisualStateGroup の状態は「同時に」有効になりうる。
/// そのため同一 VisualStateManager 内で、同じ要素の同じプロパティを
/// 複数のグループがアニメーションすると、後勝ちで上書きし合って不具合になる。
/// （例: Checked と PointerOver を別グループに置くと、選択中ボタンにホバーした瞬間
///  アクセント背景がホバー背景に奪われ「選択が外れたように見える」。）
///
/// 実行時 UI テスト（WinAppDriver 等）に頼らず、この構造的アンチパターンを
/// アプリ起動なしで検出する。
/// </summary>
public class VisualStateConflictTests
{
    private const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static IEnumerable<object[]> AppXamlFiles()
    {
        foreach (var file in Directory.EnumerateFiles(AppDir(), "*.xaml", SearchOption.AllDirectories))
        {
            // ビルド中間生成物（obj/ bin/）の XAML コピーは解析対象外（ソースのみ検査）
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            yield return new object[] { file };
        }
    }

    [Theory]
    [MemberData(nameof(AppXamlFiles))]
    public void VisualStateManager_に_グループ間で同一プロパティを奪い合う競合がない(string xamlPath)
    {
        var doc = XDocument.Load(xamlPath);
        var conflicts = new List<string>();

        // 添付プロパティ VisualStateManager.VisualStateGroups の各インスタンス（=テンプレート1つ分）を走査
        foreach (var vsm in doc.Descendants()
                     .Where(e => e.Name.LocalName == "VisualStateManager.VisualStateGroups"))
        {
            // key = "対象要素|対象プロパティ" -> その値を動かしているグループ名の集合
            var targetToGroups = new Dictionary<string, HashSet<string>>();

            var groups = vsm.Elements().Where(e => e.Name.LocalName == "VisualStateGroup").ToList();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                string groupName = groups[gi].Attribute(XName.Get("Name", X))?.Value ?? $"(group #{gi})";

                foreach (var (target, prop) in AnimatedTargets(groups[gi]))
                {
                    string key = $"{target}|{prop}";
                    if (!targetToGroups.TryGetValue(key, out var set))
                        targetToGroups[key] = set = new HashSet<string>();
                    set.Add(groupName);
                }
            }

            foreach (var (key, groupSet) in targetToGroups)
                if (groupSet.Count > 1)
                {
                    var parts = key.Split('|');
                    conflicts.Add(
                        $"  要素 '{parts[0]}' のプロパティ '{parts[1]}' を複数グループが動かしています: " +
                        string.Join(", ", groupSet.OrderBy(s => s)));
                }
        }

        Assert.True(conflicts.Count == 0,
            $"{Path.GetFileName(xamlPath)} に VisualState の競合があります。\n" +
            "同時に有効になりうる別グループで同じプロパティを動かすと状態が奪い合いになります。\n" +
            "複合状態（CheckedPointerOver など）を1グループにまとめて解消してください。\n" +
            string.Join("\n", conflicts));
    }

    /// <summary>
    /// グループ配下の全 VisualState を走査し、Storyboard が動かす
    /// (TargetName, TargetProperty) の組を列挙する。
    /// </summary>
    private static IEnumerable<(string target, string prop)> AnimatedTargets(XElement group)
    {
        foreach (var anim in group.Descendants())
        {
            string? target = AttrLocal(anim, "Storyboard.TargetName");
            string? prop = AttrLocal(anim, "Storyboard.TargetProperty");
            if (target is not null && prop is not null)
                yield return (target, prop);
        }
    }

    // 添付プロパティ属性（Storyboard.TargetName 等）は名前空間なしで現れる。LocalName で照合する。
    private static string? AttrLocal(XElement e, string localName) =>
        e.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    /// <summary>テスト実行ディレクトリから上方向に src/GuiEcad.App を探す。</summary>
    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "GuiEcad.App");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("src/GuiEcad.App が見つかりません（テスト実行ディレクトリの上位を探索）。");
    }
}
