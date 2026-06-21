namespace GuiEcad.Model;

public enum DeviceClass { Relay, PushButton, SelectSwitch, Lamp, Timer, Counter, Terminal, Other }

/// <summary>電気的な実体（CR11 等）。状態・クロスリファレンスのキー。</summary>
public sealed class Device
{
    public string Name { get; set; } = "";
    public DeviceClass Class { get; set; }
    public string? PartId { get; set; }
    /// <summary>部品表(BOM)用: 型式。</summary>
    public string? Model { get; set; }
    /// <summary>部品表(BOM)用: メーカー。</summary>
    public string? Maker { get; set; }
    /// <summary>部品表(BOM)用: 数量（既定 1）。</summary>
    public int Quantity { get; set; } = 1;
}

public sealed class DeviceTable
{
    public Dictionary<string, Device> ByName { get; set; } = new();
}

/// <summary>部品リストの1項目。</summary>
public sealed class Part
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Maker { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Rating { get; set; }
    public int Quantity { get; set; }
}

public sealed class PartsList
{
    public List<Part> Items { get; set; } = new();
}
