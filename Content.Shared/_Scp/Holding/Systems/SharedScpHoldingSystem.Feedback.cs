using Content.Shared._Scp.Holding.Components;

namespace Content.Shared._Scp.Holding.Systems;

public abstract partial class SharedScpHoldingSystem
{
    protected abstract void ShowBreakoutAttemptFeedback(Entity<ActiveScpHoldableComponent> held);

    protected abstract void Popup(EntityUid target, string key, params (string, object)[] args);
}
