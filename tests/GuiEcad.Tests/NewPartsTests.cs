using GuiEcad.Model;
using GuiEcad.Simulation;
using Xunit;

namespace GuiEcad.Tests;

/// <summary>サンプル凡例の追加2端子パーツ（タイマ接点 a/b・非常停止押釦・サーマルOL）の導通挙動。</summary>
public class NewPartsTests
{
    private static ElementInstance El(ElementKind kind, int row, int col, string? device = null)
        => new() { Kind = kind, Pos = new GridPos(row, col), DeviceName = device };

    private static bool Energized(ElementKind contact, string dev, bool input)
    {
        // L —[contact dev]— (Coil C) — R
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        sheet.Elements.Add(El(contact, 0, 0, dev));
        sheet.Elements.Add(El(ElementKind.Coil, 0, 2, "C"));
        var nl = NetlistBuilder.Build(sheet);
        var res = new Evaluator(nl).Evaluate(new SimState { Inputs = { [dev] = input } });
        return res.State.Energized["C"];
    }

    // タイマ回路: Row0 = L—[PB "PB1"]—(Timer "TLR1" setpoint秒)—R
    //             Row1 = L—[timerContact "TLR1"]—(Coil "C")—R
    // Columns=2: 境界0=左母線/境界2=右母線。各行の要素が直列に並ぶ。
    private static Sheet MakeTimerSheet(ElementKind contact, double setpoint)
    {
        var timerEl = new ElementInstance
        {
            Kind = ElementKind.Timer, Pos = new GridPos(0, 1), DeviceName = "TLR1",
        };
        timerEl.Params["Setpoint"] = setpoint.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var sheet = new Sheet { Grid = new GridSpec { Columns = 2 } };
        sheet.Elements.Add(El(ElementKind.PushButtonNO, 0, 0, "PB1"));
        sheet.Elements.Add(timerEl);
        sheet.Elements.Add(El(contact, 1, 0, "TLR1"));
        sheet.Elements.Add(El(ElementKind.Coil, 1, 1, "C"));
        return sheet;
    }

    [Fact]
    public void TimerContactNO_ConductsOnlyWhenCoilOnAndTimedOut()
    {
        var session = new TestSession(MakeTimerSheet(ElementKind.TimerContactNO, setpoint: 5.0));
        session.Evaluate();

        // PB未押下 → TLR1消磁 → 接点開放
        Assert.False(session.IsEnergized("C"));

        // PB押下 → TLR1励磁 → 時間未達 → 接点開放
        session.SetInput("PB1", true);
        Assert.False(session.IsEnergized("C"));

        // Tick 3s → まだ未達 → 開放
        session.Tick(3.0);
        Assert.False(session.IsEnergized("C"));

        // Tick 2s → 累計5s到達 → 閉路
        session.Tick(2.0);
        Assert.True(session.IsEnergized("C"));

        // PB離す → TLR1消磁・タイマリセット → 接点開放
        session.SetInput("PB1", false);
        Assert.False(session.IsEnergized("C"));
    }

    [Fact]
    public void TimerContactNC_OpenOnlyWhenCoilOnAndTimedOut()
    {
        var session = new TestSession(MakeTimerSheet(ElementKind.TimerContactNC, setpoint: 5.0));
        session.Evaluate();

        // PB未押下 → TLR1消磁 → b接点閉路 → C通電
        Assert.True(session.IsEnergized("C"));

        // PB押下 → TLR1励磁 → 時間未達 → b接点閉路
        session.SetInput("PB1", true);
        Assert.True(session.IsEnergized("C"));

        // Tick 5s → 限時到達 → b接点開放 → C消灯
        session.Tick(5.0);
        Assert.False(session.IsEnergized("C"));

        // PB離す → TLR1消磁・リセット → b接点閉路に戻る
        session.SetInput("PB1", false);
        Assert.True(session.IsEnergized("C"));
    }

    [Fact]
    public void EmergencyStop_IsNormallyClosed()
    {
        Assert.True(Energized(ElementKind.EmergencyStop, "ES", input: false));   // 未押下=導通
        Assert.False(Energized(ElementKind.EmergencyStop, "ES", input: true));   // 押下=遮断
    }

    [Fact]
    public void ThermalOverload_IsNormallyClosed()
    {
        Assert.True(Energized(ElementKind.ThermalOverload, "OL", input: false)); // 正常=導通
        Assert.False(Energized(ElementKind.ThermalOverload, "OL", input: true)); // トリップ=遮断
    }

    [Fact]
    public void SelectSwitch_ConductsOnlyAtMatchingPosition()
    {
        // 行0: L —[SS pos1]— (Coil A)   行1: L —[SS pos2]— (Coil B)（同一機器 "SS"・別ノッチ）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 3 } };
        var ss1 = new ElementInstance { Kind = ElementKind.SelectSwitch, Pos = new GridPos(0, 0), DeviceName = "SS" };
        ss1.Params["Position"] = "1";
        var ss2 = new ElementInstance { Kind = ElementKind.SelectSwitch, Pos = new GridPos(1, 0), DeviceName = "SS" };
        ss2.Params["Position"] = "2";
        sheet.Elements.Add(ss1);
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.Coil, Pos = new GridPos(0, 2), DeviceName = "A" });
        sheet.Elements.Add(ss2);
        sheet.Elements.Add(new ElementInstance { Kind = ElementKind.Coil, Pos = new GridPos(1, 2), DeviceName = "B" });

        var nl = NetlistBuilder.Build(sheet);

        var atPos1 = new Evaluator(nl).Evaluate(new SimState { Positions = { ["SS"] = 1 } }).State;
        Assert.True(atPos1.Energized["A"]);
        Assert.False(atPos1.Energized["B"]);

        var atPos2 = new Evaluator(nl).Evaluate(new SimState { Positions = { ["SS"] = 2 } }).State;
        Assert.False(atPos2.Energized["A"]);
        Assert.True(atPos2.Energized["B"]);
    }

    [Fact]
    public void Motor_IsNonSimulated_NoComponent()
    {
        // 三相モータは記号のみ＝Component を生成しない（制御ロジック評価対象外）
        var sheet = new Sheet { Grid = new GridSpec { Columns = 6 } };
        sheet.Elements.Add(new ElementInstance
        { Kind = ElementKind.Motor, Pos = new GridPos(0, 1), CellWidth = 3, DeviceName = "M1" });

        var nl = NetlistBuilder.Build(sheet);
        Assert.DoesNotContain(nl.Components, c => c.DeviceName == "M1");
        Assert.False(ElementCatalog.CreatesComponent(ElementKind.Motor));
        Assert.Equal(3, ElementCatalog.Ports(ElementKind.Motor, 3).Count); // U/V/W の3端子
    }
}
