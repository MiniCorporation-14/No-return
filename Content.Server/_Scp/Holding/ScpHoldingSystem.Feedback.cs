using Content.Server.Popups;

namespace Content.Server._Scp.Holding;

public sealed partial class ScpHoldingSystem
{
    [Dependency] private readonly PopupSystem _popup = default!;

    protected override void PopupHolder(EntityUid holder, string key, params (string, object)[] args)
    {
        base.PopupHolder(holder, key, args);

        _popup.PopupEntity(Loc.GetString(key, args), holder, holder);
    }

    protected override void PopupTarget(EntityUid target, string key, params (string, object)[] args)
    {
        base.PopupTarget(target, key, args);

        _popup.PopupEntity(Loc.GetString(key, args), target, target);
    }
}
