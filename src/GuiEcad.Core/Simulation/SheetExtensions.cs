using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>シートからシミュレーション補助情報を導出する拡張。複数箇所での同一構築を一本化する。</summary>
internal static class SheetExtensions
{
    /// <summary>行→回路番号の対応表。採番済み Lines から導出する。</summary>
    public static Dictionary<int, int> CircuitByRow(this Sheet sheet)
        => sheet.Lines.ToDictionary(l => l.Row, l => l.CircuitNumber);
}
