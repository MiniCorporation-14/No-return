using Content.Shared.Whitelist;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Scp.Holding;

/// <summary>
/// Marks an entity as a valid target for the SCP holding mechanic and stores per-target hold tuning.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ScpHoldableComponent : Component
{
    /// <summary>
    /// Optional whitelist of entities that may hold this target.
    /// </summary>
    [DataField]
    public EntityWhitelist? HolderWhitelist;

    /// <summary>
    /// Optional blacklist of entities that may not hold this target.
    /// </summary>
    [DataField]
    public EntityWhitelist? HolderBlacklist;

    /// <summary>
    /// Minimum delay between successful soft breakout attempts while the hold is active.
    /// </summary>
    [DataField]
    public TimeSpan SoftEscapeCooldown = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minimum uninterrupted full hold duration before a breakout do-after may start.
    /// </summary>
    [DataField]
    public TimeSpan FullHoldDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Duration of the visible breakout do-after for a full hold.
    /// </summary>
    [DataField]
    public TimeSpan FullBreakoutDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Duration of immunity after a successful full breakout.
    /// </summary>
    [DataField]
    public TimeSpan PostBreakoutImmunity = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Optional effect prototype spawned on each holder when a full-hold breakout attempt starts.
    /// </summary>
    [DataField]
    public EntProtoId? BreakoutAttemptEffect = "WhistleExclamation";

    /// <summary>
    /// Optional sound played from the held target when a full-hold breakout attempt starts.
    /// </summary>
    [DataField]
    public SoundSpecifier? BreakoutAttemptSound = new SoundCollectionSpecifier("storageRustle",
        AudioParams.Default.WithVolume(-2f).WithMaxDistance(4f).WithVariation(0.15f));

    /// <summary>
    /// Maximum unobstructed range allowed between holder and target.
    /// </summary>
    [DataField]
    public float HoldRange = 1f;

    /// <summary>
    /// Walk speed modifier applied to holders while they move this target.
    /// Lower values make the target heavier to move.
    /// </summary>
    [DataField]
    public float HolderWalkModifier = 0.5f;

    /// <summary>
    /// Sprint speed modifier applied to holders while they move this target.
    /// Lower values make the target heavier to move.
    /// </summary>
    [DataField]
    public float HolderSprintModifier = 0.5f;
}
