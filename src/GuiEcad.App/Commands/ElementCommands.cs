using GuiEcad.Model;

namespace GuiEcad_App.Commands;

internal sealed class PlaceElementCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly ElementInstance _element;

    public PlaceElementCommand(Sheet sheet, ElementInstance element)
    {
        _sheet = sheet;
        _element = element;
    }

    public Sheet Target => _sheet;
    public void Execute() => _sheet.Elements.Add(_element);
    public void Undo() => _sheet.Elements.Remove(_element);
}

internal sealed class DeleteElementCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly ElementInstance _element;

    public DeleteElementCommand(Sheet sheet, ElementInstance element)
    {
        _sheet = sheet;
        _element = element;
    }

    public Sheet Target => _sheet;
    public void Execute() => _sheet.Elements.Remove(_element);
    public void Undo() => _sheet.Elements.Add(_element);
}

internal sealed class MoveElementCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly ElementInstance _element;
    private readonly GridPos _from;
    private readonly GridPos _to;

    public MoveElementCommand(Sheet sheet, ElementInstance element, GridPos from, GridPos to)
    {
        _sheet = sheet;
        _element = element;
        _from = from;
        _to = to;
    }

    public Sheet Target => _sheet;
    public void Execute() => _element.Pos = _to;
    public void Undo() => _element.Pos = _from;
}

internal sealed class AddConnectorCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly VerticalConnector _connector;

    public AddConnectorCommand(Sheet sheet, VerticalConnector connector)
    {
        _sheet = sheet;
        _connector = connector;
    }

    public Sheet Target => _sheet;
    public void Execute() => _sheet.Connectors.Add(_connector);
    public void Undo() => _sheet.Connectors.Remove(_connector);
}

internal sealed class DeleteConnectorCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly VerticalConnector _connector;

    public DeleteConnectorCommand(Sheet sheet, VerticalConnector connector)
    {
        _sheet = sheet;
        _connector = connector;
    }

    public Sheet Target => _sheet;
    public void Execute() => _sheet.Connectors.Remove(_connector);
    public void Undo() => _sheet.Connectors.Add(_connector);
}

internal sealed class RenameDeviceCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly ElementInstance _element;
    private readonly string? _from;
    private readonly string? _to;

    public RenameDeviceCommand(Sheet sheet, ElementInstance element, string? from, string? to)
    {
        _sheet = sheet;
        _element = element;
        _from = from;
        _to = to;
    }

    public Sheet Target => _sheet;
    public void Execute() => _element.DeviceName = _to;
    public void Undo() => _element.DeviceName = _from;
}

internal sealed class SetCommentCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly ElementInstance _element;
    private readonly string? _from;
    private readonly string? _to;

    public SetCommentCommand(Sheet sheet, ElementInstance element, string? from, string? to)
    {
        _sheet = sheet;
        _element = element;
        _from = from;
        _to = to;
    }

    public Sheet Target => _sheet;
    public void Execute() => _element.Comment = _to;
    public void Undo() => _element.Comment = _from;
}

internal sealed class AddFrameCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly GroupFrame _frame;
    public AddFrameCommand(Sheet sheet, GroupFrame frame) { _sheet = sheet; _frame = frame; }
    public Sheet Target => _sheet;
    public void Execute() => _sheet.Frames.Add(_frame);
    public void Undo() => _sheet.Frames.Remove(_frame);
}

internal sealed class DeleteFrameCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly GroupFrame _frame;
    public DeleteFrameCommand(Sheet sheet, GroupFrame frame) { _sheet = sheet; _frame = frame; }
    public Sheet Target => _sheet;
    public void Execute() => _sheet.Frames.Remove(_frame);
    public void Undo() => _sheet.Frames.Add(_frame);
}

internal sealed class RenameFrameCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly GroupFrame _frame;
    private readonly string _from;
    private readonly string _to;
    public RenameFrameCommand(Sheet sheet, GroupFrame frame, string from, string to)
    { _sheet = sheet; _frame = frame; _from = from; _to = to; }
    public Sheet Target => _sheet;
    public void Execute() => _frame.Label = _to;
    public void Undo() => _frame.Label = _from;
}

internal sealed class InsertLastRowCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    public InsertLastRowCommand(Sheet sheet) { _sheet = sheet; }
    public Sheet Target => _sheet;
    public void Execute() => _sheet.Grid.Rows++;
    public void Undo() => _sheet.Grid.Rows--;
}

internal sealed class DeleteLastRowCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private List<ElementInstance> _removedElements = new();
    private List<VerticalConnector> _removedConnectors = new();
    private List<(GroupFrame Frame, int OldHeight)> _shrunkFrames = new();
    private List<GroupFrame> _removedFrames = new();
    private List<RungComment> _removedComments = new();

    public DeleteLastRowCommand(Sheet sheet) { _sheet = sheet; }
    public Sheet Target => _sheet;

    public void Execute()
    {
        int lastRow = _sheet.Grid.Rows - 1;
        _removedElements = _sheet.Elements.Where(e => e.Pos.Row == lastRow).ToList();
        foreach (var e in _removedElements) _sheet.Elements.Remove(e);
        _removedConnectors = _sheet.Connectors
            .Where(c => c.TopRow == lastRow || c.BottomRow == lastRow).ToList();
        foreach (var c in _removedConnectors) _sheet.Connectors.Remove(c);
        _removedComments = _sheet.RungComments.Where(rc => rc.Row == lastRow).ToList();
        foreach (var rc in _removedComments) _sheet.RungComments.Remove(rc);
        _shrunkFrames = new();
        _removedFrames = new();
        foreach (var f in _sheet.Frames.ToList())
        {
            int bottom = f.TopLeft.Row + f.Height - 1;
            if (bottom >= lastRow)
            {
                if (f.TopLeft.Row >= lastRow) { _removedFrames.Add(f); _sheet.Frames.Remove(f); }
                else { _shrunkFrames.Add((f, f.Height)); f.Height--; }
            }
        }
        _sheet.Grid.Rows--;
    }

    public void Undo()
    {
        _sheet.Grid.Rows++;
        foreach (var e in _removedElements) _sheet.Elements.Add(e);
        foreach (var c in _removedConnectors) _sheet.Connectors.Add(c);
        foreach (var rc in _removedComments) _sheet.RungComments.Add(rc);
        foreach (var (frame, oldH) in _shrunkFrames) frame.Height = oldH;
        foreach (var f in _removedFrames) _sheet.Frames.Add(f);
        _removedElements = new();
        _removedConnectors = new();
        _removedComments = new();
        _shrunkFrames = new();
        _removedFrames = new();
    }
}

internal sealed class MoveConnectorCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly VerticalConnector _connector;
    private readonly double _from;
    private readonly double _to;
    public MoveConnectorCommand(Sheet sheet, VerticalConnector connector, double from, double to)
    { _sheet = sheet; _connector = connector; _from = from; _to = to; }
    public Sheet Target => _sheet;
    public void Execute() => _connector.Column = _to;
    public void Undo() => _connector.Column = _from;
}

internal sealed class SetRungCommentCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly RungComment _comment;
    private readonly string _from;
    private readonly string _to;
    public SetRungCommentCommand(Sheet sheet, RungComment comment, string from, string to)
    { _sheet = sheet; _comment = comment; _from = from; _to = to; }
    public Sheet Target => _sheet;
    public void Execute() => _comment.Text = _to;
    public void Undo() => _comment.Text = _from;
}

internal sealed class AddRungCommentCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly RungComment _comment;
    public AddRungCommentCommand(Sheet sheet, RungComment comment) { _sheet = sheet; _comment = comment; }
    public Sheet Target => _sheet;
    public void Execute() => _sheet.RungComments.Add(_comment);
    public void Undo() => _sheet.RungComments.Remove(_comment);
}

internal sealed class InsertRowCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly int _targetRow;
    public InsertRowCommand(Sheet sheet, int targetRow) { _sheet = sheet; _targetRow = targetRow; }
    public Sheet Target => _sheet;

    public void Execute()
    {
        _sheet.Grid.Rows++;
        foreach (var e in _sheet.Elements)
            if (e.Pos.Row >= _targetRow) e.Pos = e.Pos with { Row = e.Pos.Row + 1 };
        foreach (var c in _sheet.Connectors)
        {
            if (c.TopRow >= _targetRow) c.TopRow++;
            if (c.BottomRow >= _targetRow) c.BottomRow++;
        }
        foreach (var f in _sheet.Frames)
            if (f.TopLeft.Row >= _targetRow) f.TopLeft = f.TopLeft with { Row = f.TopLeft.Row + 1 };
            else if (f.TopLeft.Row + f.Height > _targetRow) f.Height++;
        foreach (var rc in _sheet.RungComments)
            if (rc.Row >= _targetRow) rc.Row++;
    }

    public void Undo()
    {
        _sheet.Grid.Rows--;
        foreach (var e in _sheet.Elements)
            if (e.Pos.Row > _targetRow) e.Pos = e.Pos with { Row = e.Pos.Row - 1 };
        foreach (var c in _sheet.Connectors)
        {
            if (c.TopRow > _targetRow) c.TopRow--;
            if (c.BottomRow > _targetRow) c.BottomRow--;
        }
        foreach (var f in _sheet.Frames)
            if (f.TopLeft.Row > _targetRow) f.TopLeft = f.TopLeft with { Row = f.TopLeft.Row - 1 };
            else if (f.TopLeft.Row + f.Height > _targetRow) f.Height--;
        foreach (var rc in _sheet.RungComments)
            if (rc.Row > _targetRow) rc.Row--;
    }
}

internal sealed class DeleteRowCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly int _targetRow;
    private List<ElementInstance> _removedElements = new();
    private List<VerticalConnector> _removedConnectors = new();
    private List<(GroupFrame Frame, int OldHeight)> _shrunkFrames = new();
    private List<GroupFrame> _removedFrames = new();
    private List<RungComment> _removedComments = new();

    public DeleteRowCommand(Sheet sheet, int targetRow) { _sheet = sheet; _targetRow = targetRow; }
    public Sheet Target => _sheet;

    public void Execute()
    {
        _removedElements = _sheet.Elements.Where(e => e.Pos.Row == _targetRow).ToList();
        foreach (var e in _removedElements) _sheet.Elements.Remove(e);
        _removedConnectors = _sheet.Connectors
            .Where(c => c.TopRow == _targetRow || c.BottomRow == _targetRow).ToList();
        foreach (var c in _removedConnectors) _sheet.Connectors.Remove(c);
        _removedComments = _sheet.RungComments.Where(rc => rc.Row == _targetRow).ToList();
        foreach (var rc in _removedComments) _sheet.RungComments.Remove(rc);
        _shrunkFrames = new();
        _removedFrames = new();
        foreach (var f in _sheet.Frames.ToList())
        {
            if (f.TopLeft.Row == _targetRow) { _removedFrames.Add(f); _sheet.Frames.Remove(f); }
            else if (f.TopLeft.Row < _targetRow && f.TopLeft.Row + f.Height - 1 >= _targetRow)
                { _shrunkFrames.Add((f, f.Height)); f.Height--; }
        }
        foreach (var e in _sheet.Elements)
            if (e.Pos.Row > _targetRow) e.Pos = e.Pos with { Row = e.Pos.Row - 1 };
        foreach (var c in _sheet.Connectors)
        {
            if (c.TopRow > _targetRow) c.TopRow--;
            if (c.BottomRow > _targetRow) c.BottomRow--;
        }
        foreach (var f in _sheet.Frames)
            if (f.TopLeft.Row > _targetRow) f.TopLeft = f.TopLeft with { Row = f.TopLeft.Row - 1 };
        foreach (var rc in _sheet.RungComments)
            if (rc.Row > _targetRow) rc.Row--;
        _sheet.Grid.Rows--;
    }

    public void Undo()
    {
        _sheet.Grid.Rows++;
        foreach (var e in _sheet.Elements)
            if (e.Pos.Row >= _targetRow) e.Pos = e.Pos with { Row = e.Pos.Row + 1 };
        foreach (var c in _sheet.Connectors)
        {
            if (c.TopRow >= _targetRow) c.TopRow++;
            if (c.BottomRow >= _targetRow) c.BottomRow++;
        }
        foreach (var f in _sheet.Frames)
            if (f.TopLeft.Row >= _targetRow) f.TopLeft = f.TopLeft with { Row = f.TopLeft.Row + 1 };
        foreach (var rc in _sheet.RungComments)
            if (rc.Row >= _targetRow) rc.Row++;
        foreach (var e in _removedElements) _sheet.Elements.Add(e);
        foreach (var c in _removedConnectors) _sheet.Connectors.Add(c);
        foreach (var rc in _removedComments) _sheet.RungComments.Add(rc);
        foreach (var (frame, oldH) in _shrunkFrames) frame.Height = oldH;
        foreach (var f in _removedFrames) _sheet.Frames.Add(f);
        _removedElements = new();
        _removedConnectors = new();
        _removedComments = new();
        _shrunkFrames = new();
        _removedFrames = new();
    }
}

internal sealed class MoveFrameCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly GroupFrame _frame;
    private readonly double _fromX, _fromY, _toX, _toY;
    public MoveFrameCommand(Sheet sheet, GroupFrame frame, double fromX, double fromY, double toX, double toY)
    { _sheet = sheet; _frame = frame; _fromX = fromX; _fromY = fromY; _toX = toX; _toY = toY; }
    public Sheet Target => _sheet;
    public void Execute() { _frame.VisualXMm = _toX; _frame.VisualYMm = _toY; }
    public void Undo() { _frame.VisualXMm = _fromX; _frame.VisualYMm = _fromY; }
}

internal sealed class SetFrameBorderStyleCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly GroupFrame _frame;
    private readonly LineStyle? _from;
    private readonly LineStyle? _to;
    public SetFrameBorderStyleCommand(Sheet sheet, GroupFrame frame, LineStyle? from, LineStyle? to)
    { _sheet = sheet; _frame = frame; _from = from; _to = to; }
    public Sheet Target => _sheet;
    public void Execute() => _frame.BorderStyle = _to;
    public void Undo() => _frame.BorderStyle = _from;
}

internal sealed class SetParamCommand : IUndoCommand
{
    private readonly Sheet _sheet;
    private readonly ElementInstance _elem;
    private readonly string _key;
    private readonly string? _oldValue;
    private readonly string _newValue;

    public SetParamCommand(Sheet sheet, ElementInstance elem, string key, string newValue)
    {
        _sheet = sheet;
        _elem = elem;
        _key = key;
        _oldValue = elem.Params.TryGetValue(key, out var v) ? v : null;
        _newValue = newValue;
    }

    public Sheet Target => _sheet;
    public void Execute() => _elem.Params[_key] = _newValue;
    public void Undo()
    {
        if (_oldValue is null) _elem.Params.Remove(_key);
        else _elem.Params[_key] = _oldValue;
    }
}

internal sealed class BatchCommand : IUndoCommand
{
    private readonly IReadOnlyList<IUndoCommand> _commands;
    private readonly Sheet _sheet;

    public BatchCommand(Sheet sheet, IEnumerable<IUndoCommand> commands)
    {
        _sheet = sheet;
        _commands = commands.ToList();
    }

    public Sheet Target => _sheet;
    public void Execute() { foreach (var c in _commands) c.Execute(); }
    public void Undo() { foreach (var c in Enumerable.Reverse(_commands)) c.Undo(); }
}
