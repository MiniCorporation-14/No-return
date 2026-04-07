using Content.Shared.Actions;
using Content.Shared.Alert;
using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._Scp.Holding;

public sealed partial class ScpHoldActionEvent : EntityTargetActionEvent;

public sealed partial class ScpHoldBreakoutActionEvent : InstantActionEvent;

public sealed partial class ScpHoldBreakoutAlertEvent : BaseAlertEvent;

[Serializable, NetSerializable]
public sealed partial class ScpHoldBreakoutDoAfterEvent : SimpleDoAfterEvent;
