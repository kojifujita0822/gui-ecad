namespace GuiEcad.Model;

/// <summary>PartDefinition のプリミティブ最適化ユーティリティ。</summary>
public static class PartOptimizer
{
    /// <summary>
    /// 端点が接続し同一直線上にある <see cref="PartLine"/> を1本にマージする。
    /// 元のプリミティブの順序を維持する（描画順への影響を最小化）。
    /// 保存時・読み込み時の最適化として使用する。
    /// </summary>
    public static List<PartPrimitive> MergeCollinearLines(IEnumerable<PartPrimitive> prims)
    {
        const double Eps = 1e-5;
        var list = prims.ToList();

        bool anyMerged = true;
        while (anyMerged)
        {
            anyMerged = false;
            // PartLine が存在するインデックスのみを対象に操作（順序維持）
            for (int ii = 0; ii < list.Count && !anyMerged; ii++)
            {
                if (list[ii] is not PartLine a) continue;
                for (int jj = ii + 1; jj < list.Count; jj++)
                {
                    if (list[jj] is not PartLine b) continue;
                    if (TryMerge(a, b, Eps, out var merged))
                    {
                        list[ii] = merged;
                        list.RemoveAt(jj);
                        anyMerged = true;
                        break;
                    }
                }
            }
        }

        return list;
    }

    private static bool TryMerge(PartLine a, PartLine b, double eps, out PartLine result)
    {
        result = default!;
        double adx = a.X2 - a.X1, ady = a.Y2 - a.Y1;
        double bdx = b.X2 - b.X1, bdy = b.Y2 - b.Y1;
        if (Math.Abs(adx * bdy - ady * bdx) > eps) return false;

        (double ax, double ay, double bx, double by) conn = default;
        bool found = false;
        if      (Near(a.X2, a.Y2, b.X1, b.Y1, eps)) { conn = (a.X1, a.Y1, b.X2, b.Y2); found = true; }
        else if (Near(a.X2, a.Y2, b.X2, b.Y2, eps)) { conn = (a.X1, a.Y1, b.X1, b.Y1); found = true; }
        else if (Near(a.X1, a.Y1, b.X1, b.Y1, eps)) { conn = (a.X2, a.Y2, b.X2, b.Y2); found = true; }
        else if (Near(a.X1, a.Y1, b.X2, b.Y2, eps)) { conn = (a.X2, a.Y2, b.X1, b.Y1); found = true; }
        if (!found) return false;

        result = new PartLine(conn.ax, conn.ay, conn.bx, conn.by);
        return true;
    }

    private static bool Near(double x1, double y1, double x2, double y2, double eps)
        => Math.Abs(x1 - x2) < eps && Math.Abs(y1 - y2) < eps;
}
