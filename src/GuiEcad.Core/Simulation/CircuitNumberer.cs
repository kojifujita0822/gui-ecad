using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>
/// 回路番号の自動採番。ドキュメント全シートを PageNumber 順に走査し、
/// 要素が存在する各行へ 1 からの連番を図面全体通しで付与する。
/// </summary>
public static class CircuitNumberer
{
    /// <summary>
    /// doc 内の全シートの <see cref="Sheet.Lines"/> を再採番して上書きする。
    /// 既存の CircuitLine エントリは破棄して新しく生成する。
    /// </summary>
    public static void Number(LadderDocument doc)
    {
        int next = 1;
        foreach (var sheet in doc.Sheets.OrderBy(s => s.PageNumber))
            next = NumberSheet(sheet, next);
    }

    /// <summary>1シートを採番し、次回開始番号を返す。</summary>
    internal static int NumberSheet(Sheet sheet, int startNumber)
    {
        var rows = sheet.Elements
            .Select(e => e.Pos.Row)
            .Distinct()
            .OrderBy(r => r);

        sheet.Lines.Clear();
        int next = startNumber;
        foreach (var row in rows)
            sheet.Lines.Add(new CircuitLine { Row = row, CircuitNumber = next++ });

        return next;
    }
}
