using System;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// AUTOMATIC diagnostic catalog of what the navigation radar's front ray hits
/// while the game's own verdict says the avatar is blocked. This exists because
/// the player is blind and cannot aim the F10 probe (<see cref="FieldRayProbe"/>)
/// at a specific object: it fires itself, on the same condition that makes
/// <see cref="SF6Access.Hooks.WorldTour.FieldNavRadarHooks"/> speak "wall" /
/// "blocked", and needs no key press at all — the caller only has to walk into
/// things with the radar on and read the log afterwards.
///
/// <para>Read-only and silent: it never speaks and never changes what the radar
/// announces. Its only output is one log line per NEW obstacle identity, so a
/// play session's log becomes the same "what did I just bump into" answer the
/// sighted F10 workflow gives, without needing sight to aim it.</para>
/// </summary>
public static class ContactCatalog
{
    /// <summary>Floor on how often a new log line can appear, even across two
    /// different identities — a doorframe at the edge of range can re-classify
    /// every sample, and this is what keeps that from flooding the log. UX/log
    /// pacing choice, not a game value.</summary>
    private const int LOG_MIN_GAP_MS = 1000;

    private static string _lastIdentity;
    private static long _lastLogMs = long.MinValue;
    private static bool _failed;

    // Cast-result reflection, resolved once per concrete CastRayResult type — the
    // same per-type caching FieldNavRadarService uses for the same reason.
    private static TypeDefinition _resultType;
    private static Method _getContactPoint;
    private static Method _getContactCollidable;

    /// <summary>Cast once along <paramref name="forward"/> and, if the nearest
    /// contact's identity is new (and the log gap has elapsed), log it. Meant to
    /// be called every sample while the game's verdict says the avatar is
    /// blocked — the dedupe and pacing live here so the caller does not have to
    /// reason about either.</summary>
    public static void Note(FieldDirectionService.FlatDir forward)
    {
        if (_failed) return;
        try
        {
            if (!FieldNavRadarService.CastFront(forward, FieldNavRadarService.SWEEP_REACH_M, out var result))
                return;
            int count = FieldProbeService.ContactCount(result);
            if (count <= 0) return;

            ResolveMethods(result);
            if (_getContactPoint == null || _getContactCollidable == null) return;

            float bestDist = 0f;
            object bestCp = null, bestColl = null;
            for (uint i = 0; i < (uint)count; i++)
            {
                object cp = null, coll = null;
                try { cp = _getContactPoint.InvokeBoxed(typeof(object), result, new object[] { i }); } catch { }
                try { coll = _getContactCollidable.InvokeBoxed(typeof(object), result, new object[] { i }); } catch { }
                float d = FieldNavRadarService.ContactDistance(cp);
                if (d > 0f && (bestCp == null || d < bestDist)) { bestDist = d; bestCp = cp; bestColl = coll; }
            }
            if (bestCp == null) return;

            object go = FieldProbeService.Member(bestColl, "GameObject");
            string identity = FieldProbeService.Collidable(bestColl) +
                              $" obj='{FieldProbeService.GameObjectName(go)}'" +
                              FieldProbeService.GameObjectFolderAndTag(go) +
                              FieldProbeService.GameObjectComponents(go);

            long now = Environment.TickCount64;
            if (identity == _lastIdentity || now - _lastLogMs < LOG_MIN_GAP_MS) return;

            _lastIdentity = identity;
            _lastLogMs = now;
            string desc = FormattableString.Invariant($"dist={bestDist:F1}m") + identity;
            API.LogInfo($"[SF6Access] Contact: {desc}");
        }
        catch (Exception ex)
        {
            _failed = true;
            API.LogWarning($"[SF6Access] Contact catalog failed, disabling: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The two contact-index accessors, bound once for the concrete
    /// <c>CastRayResult</c> type — re-bound only if that type ever changes, the
    /// same rule the rest of the radar follows for engine-object handles.</summary>
    private static void ResolveMethods(ManagedObject result)
    {
        var td = result.GetTypeDefinition();
        if (td == null || ReferenceEquals(td, _resultType)) return;
        _resultType = td;
        _getContactPoint = td.GetMethod("getContactPoint(System.UInt32)");
        _getContactCollidable = td.GetMethod("getContactCollidable(System.UInt32)");
    }
}
