using System;
using System.Collections.Generic;
using System.Globalization;
using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Every threshold <see cref="FieldRayCaster"/> needs, each READ FROM THE GAME and
/// each withheld (NaN) rather than guessed when the game does not answer.
///
/// <para>Split out of the caster so the sensing loop stays about casting: this file
/// is only "where does the ray leave from, and what counts as floor or as a step".
/// All three answers are static for a session (authored capsule data and authored
/// ray offsets), so each is resolved once and kept.</para>
/// </summary>
internal static class FieldRayMetrics
{
    /// <summary>The game's own foot-level forward probe. The height of its published
    /// start point above the avatar's feet IS "the height at which the game itself
    /// looks for low obstacles", so it is the height under which an obstacle is a step
    /// the avatar climbs by itself rather than a wall.</summary>


    /// <summary>Second source for the step height: <c>AvatarBase.ConstSystemParams</c>
    /// (<c>app.worldtour.avatar.AvatarConstSystemParams</c>) group <c>RayOffset</c>.
    /// Accepted only inside a band the capsule itself defines, because the decompiled
    /// headers do not say what this offset is measured from.</summary>
    private const string STEP_PARAM = "RAY_STEP_UP";

    /// <summary>A <c>SlopeLimit</c> outside this band is not in degrees (another build
    /// could store radians or a cosine), so the walkable cut is refused rather than
    /// computed from a value whose unit is unknown — the guard the RE7 mod uses. The
    /// bounds are the definition of a slope in degrees, not tuning: at 0 nothing is
    /// walkable and at 90 a vertical wall would be.</summary>
    private const float SLOPE_MIN_DEG = 0f, SLOPE_MAX_DEG = 90f;

    private static readonly Dictionary<string, Method> SizeRatioByAvatar = new();
    private static FieldOutBuffer _wRatio, _hRatio;
    private static float _waist = float.NaN, _slopeCos = float.NaN, _step = float.NaN;
    private static float _heightScaled = float.NaN;
    // Raw readings kept purely so the armed line can prove where a bad height came from.
    private static float _rawHeight = float.NaN, _rawRadius = float.NaN;
    private static float _wrUsed = float.NaN, _hrUsed = float.NaN;
    private static float _midBeforeClamp = float.NaN;
    private static string _waistSource = "unread";

    private static bool _slopeTried, _stepTried;
    private static float _slopeDeg;

    /// <summary>Where the ray leaves the body: the capsule's own mid-height in metres
    /// above the feet. <c>Height</c> and <c>Radius</c> come off the live
    /// <c>via.physics.CharacterController</c> and are scaled by the avatar's current
    /// size ratio; the midpoint is then clamped into the cylindrical part of the
    /// capsule (one radius in from each cap) so a degenerate ratio can never put the
    /// origin outside the body it is supposed to leave from. NaN when unreadable.</summary>
    public static float WaistAboveFeet(ManagedObject avatar, ManagedObject cc,
                                       float feetY, TypeDefinition vecType)
    {
        if (float.IsFinite(_waist)) return _waist;
        FieldRayHeights.ReadPublished(avatar, feetY, vecType);

        // The capsule, read and recorded RAW so a bad value can be seen rather than
        // guessed at.
        _rawHeight = FieldProbeService.ToFloat(FieldProbeService.Member(cc, "Height", typeof(float)));
        _rawRadius = FieldProbeService.ToFloat(FieldProbeService.Member(cc, "Radius", typeof(float)));
        var (wr, hr) = SizeRatio(avatar);
        _wrUsed = wr; _hrUsed = hr;
        float h = _rawHeight * hr;
        float r = _rawRadius * wr;

        // A capsule is at least its own two hemispherical caps, so a height at or under
        // twice the radius is not a capsule at all — it is a misread. This is geometry,
        // not a tuned band: it cannot reject a real body. The avatar is confirmed in game
        // at Radius 0.5 / Height 1.8 (docs §4), and a Height reading of 0.5 put every ray
        // at ankle level, grazing the street for tens of metres.
        bool capsuleSane = r > 0f && h > r + r;
        if (!capsuleSane && (h > 0f || r > 0f))
            API.LogWarning(string.Format(CultureInfo.InvariantCulture,
                "[SF6Access] Capsule reads Height={0:F3} Radius={1:F3} (size ratios w={2:F3} h={3:F3}) " +
                "— a capsule cannot be shorter than its own two caps, so this read is REJECTED and " +
                "the game's own published ray heights are used instead.", h, r, wr, hr));

        // The game's own waist ray wins whenever it answered.
        if (FieldRayHeights.Waist > 0f)
        {
            _waist = FieldRayHeights.Waist;
            _waistSource = "WAIST_FRONT, the game's own published ray";
            if (capsuleSane) _heightScaled = h;
            return _waist;
        }

        if (!capsuleSane) return float.NaN;
        _midBeforeClamp = h * 0.5f;
        _waist = Math.Min(Math.Max(_midBeforeClamp, r), h - r);
        _heightScaled = h;
        _waistSource = "capsule mid-height (no published waist ray)";
        return _waist;
    }

    /// <summary>The three heights above the feet at which the reactive radar sends
    /// each beam. The capsule's cylindrical core runs from one radius above the feet
    /// to one radius below the crown, and its bottom, middle and top are what
    /// describe a whole body column — the ladder the RE7 mod casts
    /// (<c>Re7Access/src/RadarService.cs:BodyHeights</c>, "three rays at that core's
    /// bottom, middle and top cover the whole body column"). Everything here is the
    /// live capsule's own <c>Height</c>/<c>Radius</c> scaled by the avatar's size
    /// ratio; nothing is offset by a chosen amount.
    ///
    /// <para>A capsule with no cylindrical core (height at or under two radii) has
    /// exactly one usable height, so it gets the waist three times rather than an
    /// invented spread. False means the capsule could not be read at all, and then
    /// no height is returned — the caller must not cast rather than guess a body.</para></summary>
    public static bool BodyHeights(ManagedObject avatar, ManagedObject cc, float feetY,
                                   TypeDefinition vecType, out float low, out float mid, out float high)
    {
        low = mid = high = WaistAboveFeet(avatar, cc, feetY, vecType);
        if (!(mid > 0f)) return false;

        // The game's own foot / waist / bust rungs whenever it published all three:
        // real authored heights beat any offset derived from the capsule.
        if (FieldRayHeights.Ladder(out float pf, out float pw, out float pb))
        {
            low = pf; mid = pw; high = pb;
            return true;
        }

        float r = Radius(avatar, cc);
        float h = _heightScaled;
        if (r > 0f && float.IsFinite(h) && h > r + r) { low = r; high = h - r; }
        return true;
    }

    /// <summary>The avatar's live collision radius in metres — the capsule's own
    /// <c>Radius</c> scaled by the current width ratio, the same pair
    /// <see cref="WaistAboveFeet"/> derives the ray origin from. It is how close the
    /// waist origin can physically come to a wall, which is where a proximity cue has
    /// to be at full strength, and doubled it is the width a gap needs for the avatar
    /// to fit. 0 when unreadable, so a caller can refuse to answer rather than guess a
    /// body size.</summary>
    public static float Radius(ManagedObject avatar, ManagedObject cc)
    {
        if (float.IsFinite(_radius)) return _radius;
        float r = FieldProbeService.ToFloat(FieldProbeService.Member(cc, "Radius", typeof(float)));
        var (wr, _) = SizeRatio(avatar);
        r *= wr;
        if (!(r > 0f)) return 0f;
        _radius = r;
        return r;
    }

    private static float _radius = float.NaN;

    /// <summary>Forget every body measurement so the next cast re-reads them.
    ///
    /// <para>All of these describe the AVATAR, and the avatar is not fixed: World
    /// Tour lets the player rebuild it, and the size ratio is a live value. Cached
    /// for the whole process they would keep aiming rays from an old waist height
    /// and judging slopes against an old limit, silently, for the rest of the
    /// session. Called whenever the field gate closes — which is exactly what an
    /// avatar edit goes through.</para></summary>
    public static void Reset()
    {
        _waist = float.NaN;
        _radius = float.NaN;
        _heightScaled = float.NaN;
        _slopeCos = float.NaN;
        _step = float.NaN;
        _slopeTried = false;
        _stepTried = false;
        _slopeDeg = 0f;
        FieldRayHeights.Reset();
        _rawHeight = _rawRadius = _wrUsed = _hrUsed = _midBeforeClamp = float.NaN;
        _waistSource = "unread";
    }

    /// <summary>The walkable cut: <c>cos(CharacterController.SlopeLimit)</c>. A contact
    /// whose normal Y is at or above this leans less off vertical than the avatar can
    /// climb, so it is floor or ramp and never a wall. NaN when the limit could not be
    /// read in a unit this code can trust.</summary>
    public static float SlopeCos(ManagedObject cc)
    {
        if (_slopeTried) return _slopeCos;
        _slopeTried = true;
        _slopeDeg = FieldProbeService.ToFloat(FieldProbeService.Member(cc, "SlopeLimit", typeof(float)));
        if (_slopeDeg > SLOPE_MIN_DEG && _slopeDeg < SLOPE_MAX_DEG)
            _slopeCos = (float)Math.Cos(_slopeDeg * Math.PI / 180.0);
        return _slopeCos;
    }

    /// <summary>How high above the feet an obstacle stops being a wall.
    ///
    /// <para><c>AvatarCollisionCache.GetGoupStepInfo</c> was the first candidate and is
    /// NOT a threshold: <c>GoupStepInfo</c> is a per-frame measurement of the step the
    /// game found right now, absent on every frame with nothing to climb. The height is
    /// the game's own <c>FOOT_FRONT</c> rung (already read by
    /// <see cref="FieldRayHeights"/>), and failing that <see cref="STEP_PARAM"/>. NaN
    /// means neither answered and steps must simply not be classified.</para></summary>
    public static float StepAboveFeet(ManagedObject avatar, float feetY, float waist, TypeDefinition vecType)
    {
        if (_stepTried) return _step;
        _stepTried = true;
        FieldRayHeights.ReadPublished(avatar, feetY, vecType);
        float foot = FieldRayHeights.Foot;
        if (foot > 0f && foot < waist) { _step = foot; return _step; }
        try
        {
            var p = FieldProbeService.Member(avatar, "ConstSystemParams") as ManagedObject;
            float v = FieldProbeService.ToFloat(FieldProbeService.Member(p, STEP_PARAM, typeof(float)));
            if (v > 0f && v < waist) _step = v;
        }
        catch { }
        return _step;
    }

    /// <summary>The avatar's live capsule scale from
    /// <c>AvatarBase.GetCurrentCharacterControllerSizeRatio(ref float, ref float)</c>.
    /// Neutral (1, 1) when the game does not answer — the identity of a ratio, which is
    /// the one value that adds nothing of our own to the capsule's authored size.</summary>
    private static (float w, float h) SizeRatio(ManagedObject avatar)
    {
        var td = avatar?.GetTypeDefinition();
        string key = td?.GetFullName();
        if (key == null) return (1f, 1f);
        if (!SizeRatioByAvatar.TryGetValue(key, out var m))
        {
            m = FieldProbeService.FindByShape(td, "GetCurrentCharacterControllerSizeRatio", 2, "Single");
            SizeRatioByAvatar[key] = m;
        }
        if (m == null) return (1f, 1f);

        // ref float: REFramework copies nothing back into the object[] and a boxed
        // float is passed by value, so the ratios can only be received through
        // caller-owned unmanaged memory.
        _wRatio ??= FieldOutBuffer.Acquire(m.GetParameters()?[0].Type);
        _hRatio ??= FieldOutBuffer.Acquire(m.GetParameters()?[1].Type);
        if (_wRatio == null || _hRatio == null) return (1f, 1f);
        _wRatio.Clear();
        _hRatio.Clear();
        try { m.InvokeBoxed(null, avatar, new object[] { _wRatio.View, _hRatio.View }); }
        catch { return (1f, 1f); }
        float w = FieldProbeService.OutFloat(_wRatio), h = FieldProbeService.OutFloat(_hRatio);
        return (w > 0f ? w : 1f, h > 0f ? h : 1f);
    }

    /// <summary>The body column the radar casts at, as already resolved — for a log
    /// line, so nothing here re-reads the avatar.</summary>
    public static string DescribeBodyHeights()
    {
        if (!float.IsFinite(_waist)) return "unread";
        bool core = _radius > 0f && float.IsFinite(_heightScaled) && _heightScaled > _radius + _radius;
        return core
            ? "low=" + M(_radius) + " mid=" + M(_waist) + " high=" + M(_heightScaled - _radius) +
              " above the feet"
            : "waist only, " + M(_waist) + " above the feet (capsule has no cylindrical core)";
    }

    /// <summary>The thresholds and where each came from, for the caster's one-time
    /// route line. Anything unreadable says so, and says what is lost with it.</summary>
    public static string Describe() =>
        "waist=" + M(_waist) + " from " + _waistSource +
        " | RAW capsule Height=" + M(_rawHeight) + " Radius=" + M(_rawRadius) +
        " sizeRatio w=" + F(_wrUsed) + " h=" + F(_hrUsed) +
        " | capsule mid before clamp=" + M(_midBeforeClamp) + " after=" + M(_waist) +
        " | " + FieldRayHeights.Describe() +
        " | slopeLimit=" + _slopeDeg.ToString("F1", CultureInfo.InvariantCulture) + " deg -> cut " +
        (float.IsFinite(_slopeCos)
            ? _slopeCos.ToString("F3", CultureInfo.InvariantCulture)
            : "UNAVAILABLE (every contact reported as Unknown)") +
        ", step height " + (float.IsFinite(_step) ? M(_step) : "UNAVAILABLE (steps not classified)");

    private static string F(float v) =>
        float.IsFinite(v) ? v.ToString("F3", CultureInfo.InvariantCulture) : "unread";

    private static string M(float v) =>
        float.IsFinite(v) ? v.ToString("F3", CultureInfo.InvariantCulture) + " m" : "unread";
}
