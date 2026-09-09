using System;

namespace SF6Access.Services.WorldTour;

/// <summary>One beam's measurements for one evaluation.</summary>
internal readonly struct BeamReading
{
    /// <summary>The beam was measured at all. False means "no information", which a
    /// caller must never read as open space.</summary>
    public readonly bool Ok;

    /// <summary>The waist ray met blocking geometry inside the reach. When false the
    /// beam has NOTHING to track and must forget its line — it does not announce
    /// "open" (AHC zeroes the point and nulls the line).</summary>
    public readonly bool HasWaist;

    /// <summary>Metres from the avatar to that waist contact.</summary>
    public readonly float WaistDist;

    /// <summary>Blocking distance at each body height, lowest first, or the full reach
    /// where that height met nothing. These feed the stairs probe and the proximity
    /// tone, never the line memory.</summary>
    public readonly float Low, Mid, High;

    /// <summary>How face-on the waist surface is to this beam, 0…1 — the cosine
    /// between the beam and the surface normal. 1 when the normal could not be read,
    /// because "something is there" is this mod's standing policy for an unreadable
    /// contact and a missed wall costs more than an extra tone.</summary>
    public readonly float WaistFace;

    public BeamReading(bool hasWaist, float waistDist, float low, float mid, float high, float face)
    {
        Ok = true;
        HasWaist = hasWaist;
        WaistDist = waistDist;
        Low = low; Mid = mid; High = high;
        WaistFace = face;
    }
}

/// <summary>
/// The reactive radar's SENSING layer: three beams around the camera, each cast at
/// three body heights, as one sample from one origin at one instant.
///
/// <para><b>The input channel is the waist ray's BLOCKING contact</b> — wall, street
/// furniture or closed shutter alike. AHC's radar cast returns on
/// <c>IsWall || IsImpassable || IsDoor</c> (<c>CollisionHelperCopy.cs:35</c>): to the
/// radar, doors and furniture are OPAQUE. The RE7 mod once ran this channel on its
/// architectural distance instead, whose interactable veto let beams see THROUGH
/// every closed door, minting phantom openings the original never had. So the line
/// memory rides <see cref="RayHit.Distance"/>, never
/// <see cref="RayHit.Architectural"/>.</para>
///
/// <para>AHC has no height at all — one 2D ray. The extra knee and shoulder rays are
/// this port's own addition, because a city has kerbs, railings and sills that a
/// single waist ray cannot tell apart, and they feed only the stairs probe and the
/// proximity tone.</para>
/// </summary>
internal static class FieldRadarSense
{
    /// <summary>Not a tuning number: it is how many members <see cref="RadarBeam"/>
    /// has. Three beams, never five (<c>ReactiveRadar.cs:82-110</c>).</summary>
    public const int BEAM_COUNT = 3;

    /// <summary>Each beam's angle from the camera forward, in degrees, indexed by
    /// <see cref="RadarBeam"/>. The laterals are square to the camera, which is what
    /// makes "left" and "right" mean the stick directions the player pushes.</summary>
    private static readonly float[] AngleDeg = { -90f, 0f, 90f };

    /// <summary>Each beam's 45° bucket in the camera basis, so the travel-role test is
    /// an exact identity rather than an angular comparison — AHC quantizes its heading
    /// to 8 directions built from the same basis as the beams
    /// (<c>ReactiveRadar.cs:145-151</c>).</summary>
    private static readonly int[] Bucket = new int[BEAM_COUNT];

    private static readonly float[] Cos = new float[BEAM_COUNT];
    private static readonly float[] Sin = new float[BEAM_COUNT];
    private static readonly float[] PanByBeam = new float[BEAM_COUNT];

    private static readonly FieldDirectionService.FlatDir[] Dirs =
        new FieldDirectionService.FlatDir[BEAM_COUNT];
    private static readonly RayHit[] Hits = new RayHit[BEAM_COUNT * FieldRayCaster.BODY_HEIGHTS];
    private static readonly BeamReading[] Readings = new BeamReading[BEAM_COUNT];

    static FieldRadarSense()
    {
        for (int b = 0; b < BEAM_COUNT; b++)
        {
            double rad = AngleDeg[b] * Math.PI / 180.0;
            Cos[b] = (float)Math.Cos(rad);
            Sin[b] = (float)Math.Sin(rad);
            PanByBeam[b] = Sin[b] * FieldRadarTuning.PanScale;
            Bucket[b] = (((int)Math.Round(AngleDeg[b] / 45f)) % 8 + 8) % 8;
        }
    }

    /// <summary>Measure all three beams as ONE sample. False means the sensor could
    /// not run.</summary>
    public static bool Measure(FieldDirectionService.FlatDir camForward)
    {
        for (int b = 0; b < BEAM_COUNT; b++) Readings[b] = default;
        if (!camForward.Ok) return false;

        // Rightward basis = forward × up = (−fz, fx) on the ground plane, the
        // handedness CONFIRMED in game for this mod (FieldDirectionService.GetBearing).
        float rx = -camForward.Z, rz = camForward.X;
        for (int b = 0; b < BEAM_COUNT; b++)
            Dirs[b] = new FieldDirectionService.FlatDir(
                camForward.X * Cos[b] + rx * Sin[b],
                camForward.Z * Cos[b] + rz * Sin[b], true);

        if (!FieldRayCaster.TryCastBodyStack(Dirs, FieldRadarTuning.ReachM, Hits)) return false;

        for (int b = 0; b < BEAM_COUNT; b++)
        {
            int at = b * FieldRayCaster.BODY_HEIGHTS;
            ref readonly RayHit waist = ref Hits[at + 1];
            bool hasWaist = waist.Hit && waist.Distance > 0f;
            Readings[b] = new BeamReading(
                hasWaist, hasWaist ? waist.Distance : 0f,
                Block(Hits[at]), Block(Hits[at + 1]), Block(Hits[at + 2]),
                FaceOn(Dirs[b], in waist));
        }
        return true;
    }

    public static BeamReading Beam(RadarBeam beam) => Readings[(int)beam];

    /// <summary>The beam's world direction this evaluation — what turns its contact
    /// distance into a world contact point.</summary>
    public static FieldDirectionService.FlatDir Direction(RadarBeam beam) => Dirs[(int)beam];

    /// <summary>The beam's stereo pan, −1 left … +1 right.</summary>
    public static float Pan(RadarBeam beam) => PanByBeam[(int)beam];

    /// <summary>The beam's 45° sector in the camera basis: Left = 6, Front = 0,
    /// Right = 2.</summary>
    public static int SectorOf(RadarBeam beam) => Bucket[(int)beam];

    /// <summary>Stairs or a ramp ahead of this beam: the contact distance must GROW
    /// steadily with ray height, every step inside the slope band. Pure maths over the
    /// three heights already measured — no extra cast. Ported from
    /// <c>Re7Access/src/RadarService.cs</c> <c>StairAhead</c>.</summary>
    public static bool StairsAhead(in BeamReading r)
    {
        if (!r.Ok || r.Low >= FieldRadarTuning.StairDetectM) return false;
        float span = r.High - r.Low;
        if (span < 2f * FieldRadarTuning.StairGapMinM || span > 2f * FieldRadarTuning.StairGapMaxM)
            return false;
        float g1 = r.Mid - r.Low, g2 = r.High - r.Mid;
        return g1 >= FieldRadarTuning.StairGapMinM && g1 <= FieldRadarTuning.StairGapMaxM
            && g2 >= FieldRadarTuning.StairGapMinM && g2 <= FieldRadarTuning.StairGapMaxM;
    }

    /// <summary>The cosine between a beam and the surface it hit — how squarely it is
    /// looking at it. The normal arrives unnormalized and in world space; only its
    /// ground-plane part matters, since the beams are horizontal. A normal too short to
    /// carry a direction returns 1 (see <see cref="BeamReading.WaistFace"/>).</summary>
    private static float FaceOn(FieldDirectionService.FlatDir dir, in RayHit h)
    {
        float nx = h.NormalX, nz = h.NormalZ;
        if (!float.IsFinite(nx) || !float.IsFinite(nz)) return 1f;
        float m = (float)Math.Sqrt(nx * nx + nz * nz);
        if (m <= 1e-4f) return 1f;
        return Math.Abs(dir.X * (nx / m) + dir.Z * (nz / m));
    }

    /// <summary>A <see cref="RayHit"/> reports 0 when it met nothing, so "nothing"
    /// becomes the full reach — the distance the beam actually looked.</summary>
    private static float Block(in RayHit h)
        => h.Hit && h.Distance > 0f ? h.Distance : FieldRadarTuning.ReachM;
}
