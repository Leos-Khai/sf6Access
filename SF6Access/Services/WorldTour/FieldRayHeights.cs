using System;
using System.Globalization;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// The heights the game itself casts its own body rays at, read from
/// <c>GetCastRayPosition</c>.
///
/// <para>Docs § World Tour — spatial navigation APIs §3 is explicit that ray heights
/// must be read from this method and never written down, and it is the source that
/// rescued the radar: the collision capsule's <c>Height</c> came back scaled by a
/// size ratio of 0.278, putting every beam at ankle level where it skimmed the street
/// and struck it 60-90 m away. The authored rungs have no such failure mode.</para>
///
/// <para>Split out of <see cref="FieldRayMetrics"/>, which holds the thresholds
/// derived from the collision capsule instead.</para>
/// </summary>
internal static class FieldRayHeights
{
    private const string CAST_RAY_ENUM = "app.worldtour.avatar.AvatarState_FieldBase.CastRayTypes";

    /// <summary>The game's own foot / waist / bust rungs — the rays the avatar already
    /// casts at itself every frame for movement and animation.</summary>
    private const string FOOT_RAY = "FOOT_FRONT", WAIST_RAY = "WAIST_FRONT", BUST_RAY = "BUST_FRONT";

    private static FieldOutBuffer _rayStart, _rayEnd;
    private static float _foot = float.NaN, _waist = float.NaN, _bust = float.NaN;
    private static bool _tried;

    /// <summary>The waist rung above the feet, or NaN. The radar's ray origin.</summary>
    public static float Waist => _waist;

    /// <summary>The foot rung above the feet, or NaN — the height under which an
    /// obstacle is a step the avatar climbs by itself.</summary>
    public static float Foot => _foot;

    /// <summary>Read the three authored body rungs once. Each is the Y of that ray's
    /// published START point above the feet — the height the game itself casts it at.</summary>
    public static void ReadPublished(ManagedObject avatar, float feetY, TypeDefinition vecType)
    {
        if (_tried) return;
        _tried = true;
        _foot = PublishedHeight(avatar, feetY, FOOT_RAY, vecType);
        _waist = PublishedHeight(avatar, feetY, WAIST_RAY, vecType);
        _bust = PublishedHeight(avatar, feetY, BUST_RAY, vecType);
    }

    /// <summary>The height above the feet at which the game casts one of its own named
    /// rays, or NaN when that route does not answer.</summary>
    private static float PublishedHeight(ManagedObject avatar, float feetY, string ray,
                                         TypeDefinition vecType)
    {
        try
        {
            var state = FieldProbeService.FieldState(avatar);
            var getPos = state == null ? null : FieldProbeService.FindByShape(
                state.GetTypeDefinition(), "GetCastRayPosition", 3, "CastRayTypes");
            int rayId = FieldProbeService.EnumValue(CAST_RAY_ENUM, ray);
            var outType = getPos?.GetParameters()?[1].Type;
            if (rayId < 0 || outType == null || outType.FullName != vecType?.FullName) return float.NaN;

            _rayStart ??= FieldOutBuffer.Acquire(outType);
            _rayEnd ??= FieldOutBuffer.Acquire(outType);
            if (_rayStart == null || _rayEnd == null) return float.NaN;
            _rayStart.Clear();
            _rayEnd.Clear();
            getPos.InvokeBoxed(null, state, new object[] { rayId, _rayStart.View, _rayEnd.View });
            float h = _rayStart.Component("y") - feetY;
            return h > 0f ? h : float.NaN;
        }
        catch { return float.NaN; }
    }


    /// <summary>The three rungs, in order, when the game published all of them and they
    /// really do ascend. False leaves the caller on the capsule.</summary>
    public static bool Ladder(out float foot, out float waist, out float bust)
    {
        foot = _foot; waist = _waist; bust = _bust;
        return _foot > 0f && _waist > 0f && _bust > 0f && _foot < _waist && _waist < _bust;
    }

    public static void Reset()
    {
        _tried = false;
        _foot = _waist = _bust = float.NaN;
    }

    public static string Describe() =>
        "published rays foot=" + M(_foot) + " waist=" + M(_waist) + " bust=" + M(_bust);

    private static string M(float v) =>
        float.IsFinite(v) ? v.ToString("F3", CultureInfo.InvariantCulture) + " m" : "unread";
}
