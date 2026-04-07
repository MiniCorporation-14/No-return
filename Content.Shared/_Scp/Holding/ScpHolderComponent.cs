using Robust.Shared.GameStates;

namespace Content.Shared._Scp.Holding;

/// <summary>
/// Runtime contribution state stored on each active holder.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(SharedScpHoldingSystem))]
public sealed partial class ScpHolderComponent : Component
{
    /// <summary>
    /// Target currently being contributed to.
    /// </summary>
    [AutoNetworkedField]
    public EntityUid? Target;

    /// <summary>
    /// Whether this holder should currently receive the custom slowdown.
    /// </summary>
    [AutoNetworkedField]
    public bool SlowdownEnabled;

    /// <summary>
    /// Walk speed modifier used when <see cref="SlowdownEnabled"/> is true.
    /// </summary>
    [AutoNetworkedField]
    public float WalkModifier = 1f;

    /// <summary>
    /// Sprint speed modifier used when <see cref="SlowdownEnabled"/> is true.
    /// </summary>
    [AutoNetworkedField]
    public float SprintModifier = 1f;
}
