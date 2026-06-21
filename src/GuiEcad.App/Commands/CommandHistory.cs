using GuiEcad.Model;

namespace GuiEcad_App.Commands;

internal sealed class CommandHistory
{
    private readonly Stack<IUndoCommand> _undo = new();
    private readonly Stack<IUndoCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoDepth => _undo.Count;

    public void Execute(IUndoCommand cmd)
    {
        cmd.Execute();
        _undo.Push(cmd);
        _redo.Clear();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var cmd = _undo.Pop();
        cmd.Undo();
        _redo.Push(cmd);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var cmd = _redo.Pop();
        cmd.Execute();
        _undo.Push(cmd);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>指定シートを対象とするコマンドを Undo/Redo 履歴から取り除く（シート削除時）。</summary>
    public void RemoveCommandsForSheet(Sheet sheet)
    {
        RemoveFrom(_undo, sheet);
        RemoveFrom(_redo, sheet);
    }

    private static void RemoveFrom(Stack<IUndoCommand> stack, Sheet sheet)
    {
        // Stack 列挙は top→bottom 順。対象外を残し、元の順序で積み直す。
        var kept = stack.Where(c => !ReferenceEquals(c.Target, sheet)).Reverse().ToList();
        stack.Clear();
        foreach (var c in kept) stack.Push(c);
    }
}
