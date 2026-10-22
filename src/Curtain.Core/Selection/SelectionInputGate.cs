namespace Curtain.Core.Selection;

public enum SelectionOutcome { None, Confirm, Cancel }
public readonly record struct SelectionMouseResult(bool Suppress, SelectionOutcome Outcome);

public sealed class SelectionInputGate
{
    private bool _left, _right;
    public bool HasHeldButton => _left || _right;
    public SelectionMouseResult Handle(uint message, bool accepting)
    {
        switch (message)
        {
            case 0x201 when accepting: _left = true; return new(true, SelectionOutcome.None);
            case 0x204 when accepting: _right = true; return new(true, SelectionOutcome.None);
            case 0x202 when _left: _left = false; return new(true, accepting && !_right ? SelectionOutcome.Confirm : SelectionOutcome.None);
            case 0x205 when _right: _right = false; return new(true, accepting ? SelectionOutcome.Cancel : SelectionOutcome.None);
            default: return new(false, SelectionOutcome.None);
        }
    }
}
