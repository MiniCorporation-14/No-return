using System.Diagnostics.CodeAnalysis;
using Content.Client.Examine;
using Content.Shared._Scp.Knowledge;
using Content.Shared.Examine;
using Content.Shared.Verbs;
using Robust.Client.Player;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Scp.Knowledge;

public sealed class ScpKnowledgeSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystem _examine = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IPrototypeManager _prototype = default!;

    private readonly Dictionary<EntProtoId, List<string>> _knowledgeByEntityPrototype = new();
    private readonly Dictionary<string, ScpKnowledgeExposureFlags> _exposureFlags = new();
    private readonly HashSet<string> _knownKnowledge = [];

    private NetEntity _syncedEntity;
    private bool _stateInitialized;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ScpKnowledgeStateSyncEvent>(OnKnowledgeStateSync);
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnLocalPlayerAttached);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        RebuildCaches();
    }

    public bool TryGetPredictedExamineData(
        EntityUid target,
        out bool knowTarget,
        out string? nameOverride,
        out FormattedMessage? message)
    {
        knowTarget = true;
        nameOverride = null;
        message = null;

        if (!_stateInitialized ||
            _player.LocalEntity is not { } localEntity ||
            GetNetEntity(localEntity) != _syncedEntity ||
            !TryGetKnowledgeIdsForEntity(target, out var knowledgeIds))
        {
            return false;
        }

        var requiresKnowledge = false;
        for (var i = 0; i < knowledgeIds.Count; i++)
        {
            var knowledgeId = knowledgeIds[i];
            var knowledge = _prototype.Index<ScpKnowledgePrototype>(knowledgeId);
            if (!knowledge.HideIdentityUntilKnown)
                continue;

            requiresKnowledge = true;
            if (!IsPredictedKnownAfterExamine(knowledgeId, knowledge))
                continue;

            nameOverride = Loc.GetString(knowledge.DisplayName);
            return true;
        }

        if (!requiresKnowledge)
            return false;

        knowTarget = false;
        message = new FormattedMessage();
        message.AddText(Loc.GetString("scp-knowledge-unknown-examine"));
        return true;
    }

    public void AddPredictedExamineVerbs(EntityUid user, EntityUid target, List<Verb> verbs)
    {
        if (!_stateInitialized ||
            GetNetEntity(user) != _syncedEntity ||
            !TryGetKnowledgeIdsForEntity(target, out var knowledgeIds))
        {
            return;
        }

        for (var i = 0; i < knowledgeIds.Count; i++)
        {
            var knowledgeId = knowledgeIds[i];
            var knowledge = _prototype.Index<ScpKnowledgePrototype>(knowledgeId);
            if (!IsPredictedKnownAfterExamine(knowledgeId, knowledge) ||
                knowledge.KnownExamineVerbText == null ||
                knowledge.KnownExamineText == null)
            {
                continue;
            }

            var examineMessage = new FormattedMessage();
            examineMessage.AddText(Loc.GetString(knowledge.KnownExamineText.Value));

            var examineVerb = new ExamineVerb
            {
                Act = () => _examine.SendExamineTooltip(user, target, examineMessage, false, false),
                Text = Loc.GetString(knowledge.KnownExamineVerbText.Value),
                Category = VerbCategory.Examine,
                Icon = new SpriteSpecifier.Texture(new(ExamineSystemShared.DefaultIconTexture)),
                ClientExclusive = true,
            };

            verbs.Add(examineVerb);
        }
    }

    private void OnKnowledgeStateSync(ScpKnowledgeStateSyncEvent args)
    {
        _syncedEntity = args.Entity;
        _stateInitialized = true;
        _knownKnowledge.Clear();
        _exposureFlags.Clear();

        for (var i = 0; i < args.Entries.Length; i++)
        {
            var entry = args.Entries[i];
            if (entry.Known)
                _knownKnowledge.Add(entry.KnowledgeId);

            if (entry.ExposureFlags == ScpKnowledgeExposureFlags.None)
                continue;

            _exposureFlags[entry.KnowledgeId] = entry.ExposureFlags;
        }
    }

    private void OnLocalPlayerAttached(LocalPlayerAttachedEvent args)
    {
        if (_stateInitialized && _syncedEntity == GetNetEntity(args.Entity))
            return;

        _stateInitialized = false;
        _syncedEntity = default;
        _knownKnowledge.Clear();
        _exposureFlags.Clear();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<ScpKnowledgePrototype>())
            return;

        RebuildCaches();
    }

    private bool IsPredictedKnownAfterExamine(string knowledgeId, ScpKnowledgePrototype knowledge)
    {
        if (_knownKnowledge.Contains(knowledgeId))
            return true;

        if (!ScpKnowledgeLogic.RequiresTextAndExamine(knowledge) ||
            !knowledge.AllowExamine ||
            knowledge.ExamineProgress <= 0)
        {
            return false;
        }

        var exposureFlags = _exposureFlags.GetValueOrDefault(knowledgeId, ScpKnowledgeExposureFlags.None);
        return (exposureFlags & ScpKnowledgeExposureFlags.Text) != 0;
    }

    private void RebuildCaches()
    {
        _knowledgeByEntityPrototype.Clear();

        foreach (var knowledge in _prototype.EnumeratePrototypes<ScpKnowledgePrototype>())
        {
            for (var i = 0; i < knowledge.EntityPrototypes.Count; i++)
            {
                var entityPrototype = knowledge.EntityPrototypes[i];
                if (!_knowledgeByEntityPrototype.TryGetValue(entityPrototype, out var knowledgeIds))
                {
                    knowledgeIds = new List<string>();
                    _knowledgeByEntityPrototype[entityPrototype] = knowledgeIds;
                }

                knowledgeIds.Add(knowledge.ID);
            }
        }
    }

    private bool TryGetKnowledgeIdsForEntity(
        EntityUid target,
        [NotNullWhen(true)] out List<string>? knowledgeIds)
    {
        knowledgeIds = null;
        var prototypeId = Prototype(target)?.ID;
        return prototypeId != null && _knowledgeByEntityPrototype.TryGetValue(prototypeId, out knowledgeIds);
    }
}
