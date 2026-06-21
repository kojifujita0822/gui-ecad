namespace GuiEcad.Simulation;

/// <summary>配線ネットの接続状態（接続検査モードの青/黒判定の根拠）。</summary>
public enum WireStatus
{
    /// <summary>実データで他の端子・母線と結線されている（青）。</summary>
    Connected,
    /// <summary>どこにもつながらない宙ぶらりポート＝未結線スタブ（黒・警告）。</summary>
    Dangling,
}

/// <summary>接続検査の結果。renderer はこれを参照して線色（青/黒）を決める。</summary>
public sealed class ConnectivityReport
{
    /// <summary>未結線（degree &lt;= 1）の内部ネットID集合。</summary>
    public HashSet<int> DanglingNets { get; init; } = new();

    public WireStatus Of(int net) => DanglingNets.Contains(net) ? WireStatus.Dangling : WireStatus.Connected;
}

/// <summary>
/// ネットリストから接続状態を判定する（仕様: docs/rendering.md「接続検査モード」）。
/// データ側で確定できる「未結線（宙ぶらりポート）」を検出する。
/// 母線ネットは常に接続扱い。「ノード不一致の隣接」「ドット無し交差」の可視化は
/// 描画幾何を要するため DiagramRenderer 実装時に本判定と併用して扱う。
/// </summary>
public static class ConnectivityChecker
{
    public static ConnectivityReport Check(Netlist net)
    {
        // 各ネットに接続する要素端子の数（degree）を数える。
        var degree = new Dictionary<int, int>();
        void Bump(int n) => degree[n] = degree.TryGetValue(n, out var d) ? d + 1 : 1;
        // 通過接続要素は左右が同一ネット(NetA==NetB)になりうる。二重計上で degree を
        // 水増しすると孤立端子の未結線判定が甘くなるため、同一ネットは1回だけ数える。
        foreach (var c in net.Components) { Bump(c.NetA); if (c.NetB != c.NetA) Bump(c.NetB); }

        var dangling = new HashSet<int>();
        foreach (var n in net.Nets)
        {
            if (n.IsRail) continue;                       // 母線は常に接続扱い
            degree.TryGetValue(n.Id, out var d);
            if (d <= 1) dangling.Add(n.Id);               // 端子1個以下＝行き止まりスタブ
        }
        return new ConnectivityReport { DanglingNets = dangling };
    }
}
