using GuiEcad.Model;

namespace GuiEcad_App;

/// <summary>
/// 機器名の全シート横断検索の状態（一致集合と現在位置）と、検索・ナビゲーションの純ロジックを担う。
/// UI 非依存（コントロール参照を持たない）。ハイライト描画・シート切替・置換コマンド実行は呼び出し側が行う。
/// </summary>
internal sealed class FindController
{
    /// <summary>現在の一致集合（シートと要素の対）。描画ハイライト・結果パネルが読む。</summary>
    public List<(Sheet Sheet, ElementInstance El)> Results { get; } = new();

    /// <summary>選択中の一致インデックス（-1 = 未選択）。</summary>
    public int Index { get; set; } = -1;

    public int Count => Results.Count;

    /// <summary>選択中の一致（範囲外なら null）。</summary>
    public (Sheet Sheet, ElementInstance El)? Current =>
        Index >= 0 && Index < Results.Count ? Results[Index] : null;

    /// <summary>機器名が query に完全一致(大小無視)する要素を全シートから集め、先頭を選択する。</summary>
    public void Search(LadderDocument doc, string query)
    {
        Results.Clear();
        if (!string.IsNullOrEmpty(query))
            Results.AddRange(doc.Sheets
                .SelectMany(sh => sh.Elements
                    .Where(el => string.Equals(el.DeviceName, query, StringComparison.OrdinalIgnoreCase))
                    .Select(el => (Sheet: sh, El: el))));
        Index = Results.Count > 0 ? 0 : -1;
    }

    /// <summary>query に一致する全要素（置換対象の列挙。現在位置には影響しない）。</summary>
    public static List<(Sheet Sheet, ElementInstance El)> Matches(LadderDocument doc, string query)
        => doc.Sheets
            .SelectMany(sh => sh.Elements.Select(el => (Sheet: sh, El: el)))
            .Where(t => string.Equals(t.El.DeviceName, query, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public void Next() { if (Results.Count > 0) Index = (Index + 1) % Results.Count; }
    public void Prev() { if (Results.Count > 0) Index = (Index - 1 + Results.Count) % Results.Count; }

    /// <summary>検索結果をすべて破棄する。</summary>
    public void Clear() { Results.Clear(); Index = -1; }

    /// <summary>指定シートの一致を取り除き、インデックスを範囲内へ収める（シート削除時）。</summary>
    public void RemoveSheet(Sheet sheet)
    {
        Results.RemoveAll(r => ReferenceEquals(r.Sheet, sheet));
        if (Index >= Results.Count) Index = Results.Count - 1;
    }
}
