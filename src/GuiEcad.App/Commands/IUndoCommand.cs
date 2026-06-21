using GuiEcad.Model;

namespace GuiEcad_App.Commands;

internal interface IUndoCommand
{
    void Execute();
    void Undo();

    /// <summary>このコマンドが操作対象とするシート（シート削除時の履歴除去に使用）。</summary>
    Sheet Target { get; }
}
