namespace SF6Access.Services.WorldTour;

/// <summary>
/// Keep guiding toward the SAME person until somebody else is clearly closer.
///
/// <para>In a crowd the literal nearest avatar changes with almost every step,
/// and a tracker that renames its target every couple of seconds is reading
/// out a census, not guiding anyone anywhere. The switch margin means a
/// passer-by has to actually beat the current target by a couple of metres to
/// steal it.</para>
///
/// <para>The current target is remembered by ADDRESS, never as a cached
/// <c>ManagedObject</c>: the address is just a number, so it is safe to hold
/// across frames, and a stale one simply fails to match.</para>
/// </summary>
public sealed class StickyTarget
{
    // How much closer somebody else must be before the tracker abandons its
    // current target. Without this, a crowd steals the target every step.
    public const float DEFAULT_SWITCH_MARGIN_M = 2f;

    /// <summary>THE nearest person, shared by every hands-free feature of the
    /// field (tracker, aim, homing beacon): one target, so the beacon never
    /// pulses toward one person while the reader names another. Callers pick
    /// at their own cadence; the memory is only an address.</summary>
    public static readonly StickyTarget NearestPerson = new();

    /// <summary>The nearest NOTABLE person — a master or a player, never the
    /// crowd — which is what the spoken tracker follows (2026-09-07). The
    /// homing pulse keeps following <see cref="NearestPerson"/>: a sound toward
    /// a passer-by is fine, a sentence about one is not. Picked from a list
    /// that <see cref="AvatarNameCache.Notable"/> has already filtered.</summary>
    public static readonly StickyTarget NearestNamed = new();

    private readonly float _switchMarginM;
    private ulong _trackedAddress;

    public StickyTarget(float switchMarginM = DEFAULT_SWITCH_MARGIN_M)
    {
        _switchMarginM = switchMarginM;
    }

    /// <summary>Address of the currently tracked avatar, or 0 when nothing has
    /// been picked yet.</summary>
    public ulong TrackedAddress => _trackedAddress;

    /// <summary><paramref name="others"/> is sorted nearest-first
    /// (<see cref="AvatarFieldReader.ReadOthers"/>). Returns the chosen one;
    /// remembers it by address.</summary>
    public AvatarFieldReader.Other Pick(System.Collections.Generic.List<AvatarFieldReader.Other> others)
    {
        var nearest = others[0];
        if (_trackedAddress != 0)
            foreach (var o in others)
            {
                if (o.Avatar == null || o.Avatar.GetAddress() != _trackedAddress) continue;
                if (o.Dist <= nearest.Dist + _switchMarginM) return o;
                break;
            }

        _trackedAddress = nearest.Avatar?.GetAddress() ?? 0;
        return nearest;
    }

    /// <summary>Forget the tracked target, so the next <see cref="Pick"/> picks
    /// the literal nearest avatar with no bias.</summary>
    public void Reset() => _trackedAddress = 0;
}
