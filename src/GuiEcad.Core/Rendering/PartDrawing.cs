using GuiEcad.Model;

namespace GuiEcad.Rendering;

/// <summary>
/// 自作パーツ（<see cref="PartDefinition"/>）の図形プリミティブを描く。
/// パーツローカル座標はセル単位（原点=最左ポート点・行中心線=y0）。mm へは ×cell で変換。
/// </summary>
internal static class PartDrawing
{
    public static void Draw(IRenderer r, DrawingTheme theme, PartDefinition part, double cell, StrokeStyle s)
    {
        foreach (var p in part.Primitives)
        {
            switch (p)
            {
                case PartLine l:
                    r.DrawLine(new(l.X1 * cell, l.Y1 * cell), new(l.X2 * cell, l.Y2 * cell), s); break;
                case PartCircle c:
                    r.DrawCircle(new(c.Cx * cell, c.Cy * cell), c.R * cell, s); break;
                case PartArc a when a.Ry <= 0 && a.Rot == 0:   // 真円弧・無回転はネイティブ描画
                    r.DrawArc(new(a.Cx * cell, a.Cy * cell), a.R * cell, a.StartDeg, a.SweepDeg, s); break;
                case PartArc a when a.Ry <= 0:                 // 真円弧・回転は開始角へ加算
                    r.DrawArc(new(a.Cx * cell, a.Cy * cell), a.R * cell, a.StartDeg + a.Rot, a.SweepDeg, s); break;
                case PartArc a:                                // 楕円弧はポリライン近似（＋中心まわり回転）
                {
                    int seg = Math.Max(8, (int)Math.Ceiling(Math.Abs(a.SweepDeg) / 6.0));
                    var pts = new Point2D[seg + 1];
                    double a0 = a.StartDeg * Math.PI / 180.0, sw = a.SweepDeg * Math.PI / 180.0;
                    for (int i = 0; i <= seg; i++)
                    {
                        double t = a0 + sw * i / seg;
                        pts[i] = RotPt(a.Cx + a.R * Math.Cos(t), a.Cy + a.EffRy * Math.Sin(t), a.Cx, a.Cy, a.Rot, cell);
                    }
                    r.DrawPolyline(pts, s);
                    break;
                }
                case PartRect rc when rc.Rot == 0:
                    r.DrawRectangle(new(rc.X * cell, rc.Y * cell, rc.W * cell, rc.H * cell), s); break;
                case PartRect rc:                              // 回転した矩形は 4 頂点をポリラインで描く
                {
                    double cx = rc.X + rc.W / 2, cy = rc.Y + rc.H / 2;
                    r.DrawPolyline(new[]
                    {
                        RotPt(rc.X, rc.Y, cx, cy, rc.Rot, cell),
                        RotPt(rc.X + rc.W, rc.Y, cx, cy, rc.Rot, cell),
                        RotPt(rc.X + rc.W, rc.Y + rc.H, cx, cy, rc.Rot, cell),
                        RotPt(rc.X, rc.Y + rc.H, cx, cy, rc.Rot, cell),
                        RotPt(rc.X, rc.Y, cx, cy, rc.Rot, cell),
                    }, s);
                    break;
                }
                case PartPolyline pl when pl.Points.Length >= 4:
                {
                    var pts = new Point2D[pl.Points.Length / 2];
                    for (int i = 0; i < pts.Length; i++) pts[i] = new(pl.Points[2 * i] * cell, pl.Points[2 * i + 1] * cell);
                    r.DrawPolyline(pts, s);
                    break;
                }
                case PartText t:
                    r.DrawText(t.Text, new(t.X * cell, t.Y * cell),
                        theme.Text(TextRole.DeviceName) with { FontSizeMm = t.SizeCells * cell });
                    break;
            }
        }
    }

    /// <summary>点 (x,y) を中心 (cx,cy) まわりに deg 度回転し、×cell で mm 座標へ変換する（すべてセル単位入力）。</summary>
    private static Point2D RotPt(double x, double y, double cx, double cy, double deg, double cell)
    {
        double r = deg * Math.PI / 180.0, cos = Math.Cos(r), sin = Math.Sin(r);
        double dx = x - cx, dy = y - cy;
        return new((cx + dx * cos - dy * sin) * cell, (cy + dx * sin + dy * cos) * cell);
    }
}
