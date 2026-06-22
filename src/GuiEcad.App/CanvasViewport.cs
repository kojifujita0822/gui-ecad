using Windows.Foundation;

namespace GuiEcad_App;

/// <summary>
/// キャンバスのズーム倍率とパン位置を保持し、画面(DIP)↔ワールド(mm)座標変換を担う。
/// UI 非依存（コントロール参照を持たない）の純粋な状態＋計算クラス。
/// </summary>
internal sealed class CanvasViewport
{
    /// <summary>1mm あたりの DIP 数（96dpi 基準）。</summary>
    public const double DipsPerMm = 96.0 / 25.4;

    public double Zoom { get; set; } = 1.6;
    public double PanX { get; set; } = 20;
    public double PanY { get; set; } = 20;

    /// <summary>mm → DIP の総合スケール（DipsPerMm × Zoom）。</summary>
    public double Scale => DipsPerMm * Zoom;

    /// <summary>画面 DIP 座標をワールド mm 座標へ変換する。</summary>
    public (double X, double Y) ToWorld(Point dip)
    {
        double scale = Scale;
        return ((dip.X - PanX) / scale, (dip.Y - PanY) / scale);
    }

    /// <summary>指定点 at（DIP）を固定したまま factor 倍ズームする。</summary>
    public void ZoomBy(double factor, Point at)
    {
        double oldScale = Scale;
        Zoom = Math.Clamp(Zoom * factor, 0.2, 12.0);
        double newScale = Scale;
        PanX = at.X - (at.X - PanX) * (newScale / oldScale);
        PanY = at.Y - (at.Y - PanY) * (newScale / oldScale);
    }

    /// <summary>既定の表示倍率・位置へ戻す（フィット）。</summary>
    public void Reset()
    {
        Zoom = 1.6;
        PanX = 20;
        PanY = 20;
    }
}
