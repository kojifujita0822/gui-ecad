using GuiEcad.Model;

namespace GuiEcad.Simulation;

/// <summary>線番号（ワイヤ番号）= 電気的に連続した配線に1番号。幾何から導出（永続化しない）。</summary>
public sealed class Net
{
    public int Id { get; init; }
    /// <summary>線番。母線非接続の内部ネットのみ読み順で 1..N。母線ネットは 0（番号でなく <see cref="Name"/> を使用）。</summary>
    public int WireNumber { get; set; }
    public bool IsRail { get; init; }
    /// <summary>母線ネットの名称（例 R200/S200）。内部ネットは null。</summary>
    public string? Name { get; set; }
}

public enum ComponentRole { Contact, Load, Passthrough }

/// <summary>2つのネットを結ぶ電気要素（接点=条件付き導通 / 負荷=励磁判定 / 端子台=常時導通）。</summary>
public sealed class Component
{
    public ElementKind Kind { get; init; }
    public string? DeviceName { get; init; }
    public int NetA { get; init; }
    public int NetB { get; init; }
    public ComponentRole Role { get; init; }
    /// <summary>セレクトスイッチのノッチ位置。現在位置が一致するとき導通（その他種別では未使用）。</summary>
    public int SwitchPosition { get; init; }
    /// <summary>発生元の要素 Id（描画で要素↔ネットを対応づける）。</summary>
    public Guid SourceElementId { get; init; }
}

/// <summary>テストモードの実行時状態（機器名キー）。</summary>
public sealed class SimState
{
    public Dictionary<string, bool> Inputs { get; set; } = new();      // PB 等の操作状態
    public Dictionary<string, bool> Energized { get; set; } = new();   // リレーコイル等 ON/OFF
    public Dictionary<string, int> Positions { get; set; } = new();    // セレクトSW 等の選択ノッチ位置
    /// <summary>タイマコイルの経過時間（秒）。<see cref="TestSession.Tick"/> で更新される。</summary>
    public Dictionary<string, double> TimerElapsed { get; set; } = new();

    public SimState Clone() => new()
    {
        Inputs = new(Inputs),
        Energized = new(Energized),
        Positions = new(Positions),
        TimerElapsed = new(TimerElapsed),
    };
}

public sealed class Netlist
{
    public List<Net> Nets { get; init; } = new();
    public List<Component> Components { get; init; } = new();
    public int LeftRailNet { get; init; }
    public int RightRailNet { get; init; }
    /// <summary>タイマ設定時間（機器名→秒）。タイマコイル要素の Params["Setpoint"] から構築。</summary>
    public Dictionary<string, double> TimerSetpoints { get; init; } = new();
    /// <summary>縦コネクタが中間行の横配線と電気的に非接続で交差する箇所（P7: ドット無し交差）。</summary>
    public IReadOnlyList<(int Row, int Col)> VerticalCrossings { get; init; } = Array.Empty<(int, int)>();
}
