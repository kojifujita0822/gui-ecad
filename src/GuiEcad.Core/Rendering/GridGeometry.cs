namespace GuiEcad.Rendering;

/// <summary>
/// グリッド座標（行・列境界）↔ mm ワールド座標の相互変換。描画とヒットテストで共有する。
/// 列境界 b の x = Margin + b*Cell、行 r の中心線 y = Margin + (r+0.5)*Cell。
/// </summary>
public readonly struct GridGeometry
{
    public double CellMm { get; init; }
    public double MarginMm { get; init; }

    public GridGeometry(double cellMm = 9.0, double marginMm = 15.0)
    {
        CellMm = cellMm;
        MarginMm = marginMm;
    }

    public double X(int boundary) => MarginMm + boundary * CellMm;
    public double X(double boundary) => MarginMm + boundary * CellMm;   // 0.5 刻み境界（縦コネクタ）用
    public double YRow(int row) => MarginMm + (row + 0.5) * CellMm;

    /// <summary>x(mm) が属するセル列。</summary>
    public int ColAt(double xMm) => (int)Math.Floor((xMm - MarginMm) / CellMm);

    /// <summary>y(mm) が属するセル行。</summary>
    public int RowAt(double yMm) => (int)Math.Floor((yMm - MarginMm) / CellMm);

    /// <summary>x(mm) に最も近い列境界（縦コネクタの配置スナップ等に使う）。</summary>
    public int BoundaryAt(double xMm) => (int)Math.Round((xMm - MarginMm) / CellMm);

    /// <summary>x(mm) に最も近い 0.5 セル刻みの列境界（縦コネクタはセル中央にも置ける）。</summary>
    public double BoundaryAtHalf(double xMm) => Math.Round((xMm - MarginMm) / CellMm * 2) / 2.0;
}
