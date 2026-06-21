using GuiEcad.Model;

namespace GuiEcad.Simulation;

public enum EvalStatus
{
    /// <summary>不動点収束（正常）。</summary>
    Converged,
    /// <summary>周期的振動（フリッカ・ブザー等、意図的な点滅の可能性）。</summary>
    Cyclic,
    /// <summary>不定発振（収束も周期も検出できなかった）。</summary>
    Diverging,
}

public sealed class EvalResult
{
    public EvalStatus Status { get; init; }
    public SimState State { get; init; } = new();
    /// <summary>両母線から到達した通電ネット集合（UIハイライト用）。</summary>
    public HashSet<int> PoweredNets { get; init; } = new();
    /// <summary>左右母線が負荷を介さず短絡しているネット集合（P1短絡検出）。</summary>
    public HashSet<int> ShortCircuitNets { get; init; } = new();
    public int Iterations { get; init; }
    /// <summary>周期的振動の周期長（Status==Cyclic のときのみ有意）。0 は不明。</summary>
    public int CycleLength { get; init; }
}

/// <summary>
/// 有接点リレーシーケンスを connectivity ＋ 不動点反復で評価する（PLCスキャンではない）。
/// 評価順序に依存せず、実リレーの電気的同時動作に忠実。
/// </summary>
public sealed class Evaluator
{
    private readonly Netlist _net;
    public int MaxIterations { get; init; } = 100;

    public Evaluator(Netlist net) => _net = net;

    public EvalResult Evaluate(SimState input)
    {
        var s = input.Clone();
        // 状態ハッシュ → 反復番号の履歴（周期検出用）
        var history = new Dictionary<string, int>();

        for (int it = 1; it <= MaxIterations; it++)
        {
            var adj = BuildConductionGraph(s);
            var floodL = Flood(_net.LeftRailNet, adj);
            var floodR = Flood(_net.RightRailNet, adj);

            // 負荷の励磁判定（同一デバイスに複数コイルがある場合は OR 集約）
            var coil = new Dictionary<string, bool>();
            foreach (var c in _net.Components)
            {
                if (c.Role != ComponentRole.Load || c.DeviceName is null) continue;
                bool bridged = (floodL.Contains(c.NetA) && floodR.Contains(c.NetB))
                            || (floodL.Contains(c.NetB) && floodR.Contains(c.NetA));
                coil[c.DeviceName] = (coil.TryGetValue(c.DeviceName, out var prev) && prev) || bridged;
            }

            var next = s.Clone();
            foreach (var kv in coil) next.Energized[kv.Key] = kv.Value;

            if (SameEnergized(s.Energized, next.Energized))
            {
                var powered = new HashSet<int>(floodL);
                powered.UnionWith(floodR);
                // 短絡: 負荷を除いた導通グラフで左右母線フラッド集合が重なる（floodL ∩ floodR が非空）。
                var shorts = new HashSet<int>(floodL);
                shorts.IntersectWith(floodR);
                return new EvalResult
                {
                    Status = EvalStatus.Converged,
                    State = next,
                    PoweredNets = powered,
                    ShortCircuitNets = shorts,
                    Iterations = it,
                };
            }

            // 周期検出: 現在の励磁状態が過去の反復と同一なら周期的振動
            var hash = ComputeEnergizedHash(next.Energized);
            if (history.TryGetValue(hash, out int prevIt))
            {
                return new EvalResult
                {
                    Status = EvalStatus.Cyclic,
                    State = next,
                    Iterations = it,
                    CycleLength = it - prevIt,
                };
            }
            history[hash] = it;
            s = next;
        }
        return new EvalResult { Status = EvalStatus.Diverging, State = s, Iterations = MaxIterations };
    }

    /// <summary>励磁状態を決定論的な文字列に変換してハッシュキーにする。</summary>
    private static string ComputeEnergizedHash(Dictionary<string, bool> energized)
    {
        // キー昇順でソートして "key:0/1" を連結→重複なし・順序依存なし
        var sb = new System.Text.StringBuilder();
        foreach (var kv in energized.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key);
            sb.Append(kv.Value ? ":1;" : ":0;");
        }
        return sb.ToString();
    }

    private Dictionary<int, List<int>> BuildConductionGraph(SimState s)
    {
        var adj = new Dictionary<int, List<int>>();
        void AddEdge(int a, int b)
        {
            if (!adj.TryGetValue(a, out var la)) { la = new(); adj[a] = la; }
            la.Add(b);
            if (!adj.TryGetValue(b, out var lb)) { lb = new(); adj[b] = lb; }
            lb.Add(a);
        }
        foreach (var c in _net.Components)
        {
            if (c.Role == ComponentRole.Load) continue;                 // 負荷は給電を通さない
            if (c.Role == ComponentRole.Passthrough || IsConducting(c, s))
                AddEdge(c.NetA, c.NetB);
        }
        return adj;
    }

    private bool IsConducting(Component c, SimState s)
    {
        // セレクトSW: 現在のノッチ位置がこの接点の位置に一致すれば導通
        if (c.Kind == ElementKind.SelectSwitch)
            return c.DeviceName is not null
                && s.Positions.TryGetValue(c.DeviceName, out var pos) && pos == c.SwitchPosition;

        // タイマ接点: タイマコイル励磁 AND 経過時間 >= 設定時間
        if (c.Kind is ElementKind.TimerContactNO or ElementKind.TimerContactNC)
        {
            bool coilOn = c.DeviceName is not null &&
                          s.Energized.TryGetValue(c.DeviceName, out var ce) && ce;
            _net.TimerSetpoints.TryGetValue(c.DeviceName ?? "", out double sp);
            s.TimerElapsed.TryGetValue(c.DeviceName ?? "", out double elapsed);
            bool timedOut = sp > 0 && elapsed >= sp;
            bool on = coilOn && timedOut;
            return c.Kind == ElementKind.TimerContactNO ? on : !on;
        }

        bool state = c.DeviceName is not null && (
            ElementCatalog.IsInputControlled(c.Kind)
                ? s.Inputs.TryGetValue(c.DeviceName, out var v) && v
                // リレー接点はコイル励磁で制御。ただし手動強制ON（Inputs=true）があれば閉路扱いにする。
                : (s.Energized.TryGetValue(c.DeviceName, out var e) && e)
                  || (s.Inputs.TryGetValue(c.DeviceName, out var f) && f));
        return c.Kind switch
        {
            ElementKind.ContactNO or ElementKind.PushButtonNO => state,
            ElementKind.ContactNC or ElementKind.PushButtonNC
                or ElementKind.EmergencyStop or ElementKind.ThermalOverload => !state,
            _ => false,
        };
    }

    private static HashSet<int> Flood(int start, Dictionary<int, List<int>> adj)
    {
        var seen = new HashSet<int> { start };
        var stack = new Stack<int>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            int x = stack.Pop();
            if (!adj.TryGetValue(x, out var ns)) continue;
            foreach (var nx in ns) if (seen.Add(nx)) stack.Push(nx);
        }
        return seen;
    }

    private static bool SameEnergized(Dictionary<string, bool> a, Dictionary<string, bool> b)
    {
        var keys = new HashSet<string>(a.Keys);
        keys.UnionWith(b.Keys);
        foreach (var k in keys)
        {
            a.TryGetValue(k, out var av);
            b.TryGetValue(k, out var bv);
            if (av != bv) return false;
        }
        return true;
    }
}
