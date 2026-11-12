namespace RoslynSentinel.Common;

/// <summary>
/// Bounded, in-memory, thread-safe record of committed <see cref="EditEffect"/>s, looked up by simple name. One instance per
/// PersistentWorkspaceManager (same ownership as <see cref="EditAttributionLedger"/>; not registered in DI). Fed by the write
/// chokepoint when a write stands, seeded from operation blobs at solution load, and marked reverted by an undo. It parses no JSON.
/// </summary>
public class EditEffectIndex
{
    /// <summary>Maximum effects kept; the oldest is dropped first.</summary>
    public const int MaxEffects = 500;

    private readonly object _gate = new();

    // Oldest first. A single list rather than a dictionary per name: the bound is global, and Find scans at most MaxEffects entries.
    private readonly List<EditEffect> _effects = [];

    /// <summary>Number of effects held, reverted ones included.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _effects.Count;
            }
        }
    }

    /// <summary>Adds effects, oldest first, dropping the oldest entries past <see cref="MaxEffects"/>.</summary>
    public void Append(IEnumerable<EditEffect> effects)
    {
        lock (_gate)
        {
            _effects.AddRange(effects);
            Trim();
        }
    }

    /// <summary>
    /// Stamps <paramref name="committed"/> (instances previously passed to <see cref="Append"/>, still without a change id) with
    /// <paramref name="changeId"/>: the index entry is replaced by the stamped copy. Returns the stamped copies, in order, including
    /// any whose index entry was already dropped by the bound, so the caller can persist them.
    /// </summary>
    public IReadOnlyList<EditEffect> AssignChangeId(IReadOnlyList<EditEffect> committed, string changeId)
    {
        var stamped = new List<EditEffect>(committed.Count);
        lock (_gate)
        {
            foreach (var effect in committed)
            {
                var copy = effect with { ChangeId = changeId };
                stamped.Add(copy);
                var index = _effects.FindIndex(e => ReferenceEquals(e, effect));
                if (index >= 0)
                {
                    _effects[index] = copy;
                }
            }
        }

        return stamped;
    }

    /// <summary>Marks every effect of <paramref name="changeId"/> reverted and returns how many were marked. A reverted effect is excluded from <see cref="Find"/>.</summary>
    public int MarkReverted(string changeId)
    {
        if (string.IsNullOrEmpty(changeId))
        {
            return 0;
        }

        var marked = 0;
        lock (_gate)
        {
            for (var i = 0; i < _effects.Count; i++)
            {
                var effect = _effects[i];
                if (!effect.Reverted && string.Equals(effect.ChangeId, changeId, StringComparison.Ordinal))
                {
                    _effects[i] = effect with { Reverted = true };
                    marked++;
                }
            }
        }

        return marked;
    }

    /// <summary>Non-reverted effects whose <see cref="EditEffect.SimpleName"/> equals <paramref name="simpleName"/> (case-sensitive), newest first.</summary>
    public IReadOnlyList<EditEffect> Find(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName))
        {
            return [];
        }

        lock (_gate)
        {
            var found = new List<EditEffect>();
            for (var i = _effects.Count - 1; i >= 0; i--)
            {
                var effect = _effects[i];
                if (!effect.Reverted && string.Equals(effect.SimpleName, simpleName, StringComparison.Ordinal))
                {
                    found.Add(effect);
                }
            }

            return found;
        }
    }

    /// <summary>Non-reverted <see cref="EditEffectKind.Renamed"/> effects whose <see cref="EditEffect.NewName"/> equals <paramref name="newName"/> (case-sensitive), newest first. A rename to a name makes that name declared again, so a note for the name's earlier removal or rename is stale.</summary>
    public IReadOnlyList<EditEffect> FindByNewName(string newName)
    {
        if (string.IsNullOrEmpty(newName))
        {
            return [];
        }

        lock (_gate)
        {
            var found = new List<EditEffect>();
            for (var i = _effects.Count - 1; i >= 0; i--)
            {
                var effect = _effects[i];
                if (!effect.Reverted && effect.Kind == EditEffectKind.Renamed && string.Equals(effect.NewName, newName, StringComparison.Ordinal))
                {
                    found.Add(effect);
                }
            }

            return found;
        }
    }

    /// <summary>
    /// Adds effects read from operation blobs at solution load. An effect already held (same call, change, kind, name and time) is
    /// skipped, and the result stays in time order. The bound applies as for <see cref="Append"/>.
    /// </summary>
    // TODO slice 4: the blob does not record an undo, so a seeded effect of an undone change comes back unreverted; the seeding code must skip changes UndoLastApply already reverted.
    public void Seed(IEnumerable<EditEffect> effects)
    {
        lock (_gate)
        {
            var added = false;
            foreach (var effect in effects)
            {
                if (_effects.Exists(held => SameEffect(held, effect)))
                {
                    continue;
                }

                _effects.Add(effect);
                added = true;
            }

            if (added)
            {
                // OrderBy is stable, so effects with equal times keep their insertion order.
                var ordered = _effects.OrderBy(e => e.UtcTime).ToList();
                _effects.Clear();
                _effects.AddRange(ordered);
                Trim();
            }
        }
    }

    private void Trim()
    {
        var excess = _effects.Count - MaxEffects;
        if (excess > 0)
        {
            _effects.RemoveRange(0, excess);
        }
    }

    private static bool SameEffect(EditEffect a, EditEffect b)
    {
        return a.Kind == b.Kind
            && a.UtcTime == b.UtcTime
            && string.Equals(a.ToolCallId, b.ToolCallId, StringComparison.Ordinal)
            && string.Equals(a.ChangeId, b.ChangeId, StringComparison.Ordinal)
            && string.Equals(a.SimpleName, b.SimpleName, StringComparison.Ordinal)
            && string.Equals(a.SymbolDocId, b.SymbolDocId, StringComparison.Ordinal);
    }
}

/// <summary>Exposes the server's edit effect index.</summary>
public interface IEditEffectIndexProvider
{
    /// <summary>The single live instance, owned by the workspace manager.</summary>
    EditEffectIndex EditEffects { get; }
}
