using System;
using System.Collections.Generic;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Where the avatar is GOING and how fast, plus how fast the camera is turning. The
/// reactive radar needs all three: the travel direction assigns each beam its role,
/// the speed turns a distance into a TIME to contact, and the yaw rate is what tells
/// a deliberate turn from noise.
///
/// <para><b>Speed comes from the game, never from the stick.</b> Primary source is
/// <c>AvatarBase.GetVelocity() : vec3</c> (documented in <c>docs/sf6-screens.md</c>
/// § World Tour — spatial navigation APIs). Input axes are refused on principle: they
/// say what was asked for, not what the avatar did, and they are expressed in a frame
/// that has already been mirrored once in this mod's history.</para>
///
/// <para><b>The fallback is announced, never silent.</b> If the engine's velocity
/// never reports motion while the avatar's own position is demonstrably moving, this
/// switches to the position delta — a time-normalized EMA, the way the RE7 mod's
/// <c>PlayerHelper</c> derives travel when the engine's fields sit under their noise
/// floor — and says which one it is using in the log.</para>
/// </summary>
internal static class FieldRadarTravel
{
    /// <summary>Where the travel signal is coming from, for the radar's one startup
    /// line.</summary>
    public enum Source { Unbound, EngineVelocity, PositionDelta }

    public static Source SpeedSource { get; private set; } = Source.Unbound;

    /// <summary>True while the avatar is locomoting. Standing still, every beam loses
    /// its role and the radar re-seeds — RE7's own behaviour.</summary>
    public static bool Moving { get; private set; }

    /// <summary>Smoothed unit travel direction on the ground plane. Only meaningful
    /// while <see cref="Moving"/>.</summary>
    public static float TravelX { get; private set; }
    public static float TravelZ { get; private set; }

    /// <summary>Smoothed ground speed in m/s.</summary>
    public static float SpeedMs { get; private set; }

    /// <summary>Camera yaw rate in degrees per second — a RATE, so it means the same
    /// at any frame rate.</summary>
    public static float YawRateDegPerSec { get; private set; }

    /// <summary>True while the camera is turning faster than
    /// <see cref="FieldRadarTuning.TurnResetRateDegPerSec"/>.</summary>
    public static bool Turning { get; private set; }

    /// <summary>True on the evaluation where the avatar jumped further than
    /// locomotion allows — a warp, a load, a scene change. The caller must forget
    /// everything: the world it was tracking is gone.</summary>
    public static bool Teleported { get; private set; }

    private static bool _hasPos, _hasSmoothed, _hasPrevForward, _engineEverMoved, _deltaEverMoved;
    private static float _lastX, _lastY, _lastZ, _prevFwdX, _prevFwdZ;

    /// <summary>Read this evaluation's travel picture. <paramref name="dtSec"/> is the
    /// real elapsed time, so every smoothing constant below is in seconds and holds at
    /// any sense rate.</summary>
    public static void Update(float x, float y, float z, FieldDirectionService.FlatDir camForward,
                              float dtSec)
    {
        Teleported = false;

        // --- Teleport: a jump no locomotion could produce ---
        if (_hasPos)
        {
            float jx = x - _lastX, jy = y - _lastY, jz = z - _lastZ;
            if ((float)Math.Sqrt(jx * jx + jy * jy + jz * jz) > FieldRadarTuning.TeleportM)
                Teleported = true;
        }

        // --- Raw travel: the engine's velocity first, the position delta as witness ---
        var (evx, evz, espeed, engineOk) = EngineVelocity();
        float dvx = 0f, dvz = 0f, dspeed = 0f;
        if (_hasPos && dtSec > 0f && !Teleported)
        {
            float dx = x - _lastX, dz = z - _lastZ;
            float mag = (float)Math.Sqrt(dx * dx + dz * dz);
            dspeed = mag / dtSec;
            if (mag > 0f) { dvx = dx / mag; dvz = dz / mag; }
        }
        _lastX = x; _lastY = y; _lastZ = z; _hasPos = true;

        if (engineOk && espeed > FieldRadarTuning.MinBodySpeedMs) _engineEverMoved = true;
        if (dspeed > FieldRadarTuning.MinBodySpeedMs) _deltaEverMoved = true;
        ChooseSource(engineOk);

        float vx, vz, speed;
        if (SpeedSource == Source.EngineVelocity) { vx = evx; vz = evz; speed = espeed; }
        else { vx = dvx; vz = dvz; speed = dspeed; }

        bool moving = speed > FieldRadarTuning.MinBodySpeedMs && (vx != 0f || vz != 0f);
        if (Teleported) moving = false;

        // --- Time-normalized EMA: the same smoothing whatever the sense rate ---
        if (moving)
        {
            if (!_hasSmoothed) { TravelX = vx; TravelZ = vz; SpeedMs = speed; _hasSmoothed = true; }
            else
            {
                float aDir = 1f - (float)Math.Exp(-dtSec / FieldRadarTuning.TravelSmoothTau);
                TravelX += aDir * (vx - TravelX);
                TravelZ += aDir * (vz - TravelZ);
                float m = (float)Math.Sqrt(TravelX * TravelX + TravelZ * TravelZ);
                if (m > 0f) { TravelX /= m; TravelZ /= m; }
                float aSpd = 1f - (float)Math.Exp(-dtSec / FieldRadarTuning.SpeedSmoothTau);
                SpeedMs += aSpd * (speed - SpeedMs);
            }
        }
        else { _hasSmoothed = false; SpeedMs = 0f; }
        Moving = moving;

        // --- Yaw rate from the camera's own forward, as an angle over real time ---
        YawRateDegPerSec = 0f;
        if (camForward.Ok)
        {
            if (_hasPrevForward && dtSec > 0f)
            {
                float dot = Math.Clamp(camForward.X * _prevFwdX + camForward.Z * _prevFwdZ, -1f, 1f);
                YawRateDegPerSec = (float)(Math.Acos(dot) * (180.0 / Math.PI)) / dtSec;
            }
            _prevFwdX = camForward.X; _prevFwdZ = camForward.Z; _hasPrevForward = true;
        }
        Turning = YawRateDegPerSec > FieldRadarTuning.TurnResetRateDegPerSec;

        // --- Travel sector, in the SAME basis the beams are built from ---
        // AHC quantizes its heading to 8 directions built from the beams' own basis,
        // so every role test is an exact identity rather than an angular comparison
        // (ReactiveRadar.cs:145-151). Standing still there is no sector at all: the
        // velocity is zero, nothing matches it, and every beam therefore evaluates —
        // which is exactly why the radar answers while the player TURNS IN PLACE.
        TravelSector = -1;
        if (Moving && camForward.Ok)
        {
            float fwdC = TravelX * camForward.X + TravelZ * camForward.Z;
            float rgtC = TravelX * -camForward.Z + TravelZ * camForward.X;
            TravelSector = (((int)Math.Round(Math.Atan2(rgtC, fwdC) / (Math.PI / 4.0))) % 8 + 8) % 8;
        }
    }

    /// <summary>Which of the camera basis's eight 45° sectors the avatar is travelling
    /// in, or −1 when it is not travelling. Compared against
    /// <see cref="FieldRadarSense.SectorOf"/> to give a beam its role.</summary>
    public static int TravelSector { get; private set; } = -1;

    /// <summary>Forget the history — the mode was switched off, the field gate closed,
    /// or the avatar teleported. The SOURCE choice survives: it describes the build,
    /// not the walk.</summary>
    public static void Reset()
    {
        _hasPos = false;
        _hasSmoothed = false;
        _hasPrevForward = false;
        TravelSector = -1;
        Moving = false;
        SpeedMs = 0f;
        YawRateDegPerSec = 0f;
        Turning = false;
        Teleported = false;
    }

    /// <summary>A one-line description of the bound travel signal, for the radar's
    /// startup log.</summary>
    public static string Describe() => SpeedSource switch
    {
        Source.EngineVelocity => "AvatarBase.GetVelocity()",
        Source.PositionDelta when _deltaEverMoved && !_engineEverMoved =>
            "avatar position delta (GetVelocity readable but never reported motion)",
        Source.PositionDelta => "avatar position delta (GetVelocity unreadable)",
        _ => "not yet resolved",
    };

    /// <summary>Pick the source, and log every change. The engine's velocity wins
    /// as soon as it is readable; it is demoted only on EVIDENCE — the avatar's own
    /// position moved and the velocity did not — never on a single zero sample, since
    /// standing still is a perfectly valid zero.</summary>
    private static void ChooseSource(bool engineOk)
    {
        Source want = engineOk && !(_deltaEverMoved && !_engineEverMoved)
            ? Source.EngineVelocity
            : Source.PositionDelta;
        if (want == SpeedSource) return;

        SpeedSource = want;
        // Every change is announced, including a change BACK: a travel signal that
        // silently swapped underneath would make a play report impossible to read.
        API.LogInfo("[SF6Access] Field radar travel signal: " + Describe());
    }

    private const string VELOCITY_METHOD = "GetVelocity";
    private static readonly Dictionary<string, Method> VelocityByAvatar = new();

    /// <summary>The avatar's own ground-plane velocity. <c>ok</c> false means the call
    /// could not be read at all — which is NOT the same as a zero velocity.
    ///
    /// <para>The method is resolved from the TYPE first and cached, never called
    /// hopefully: a missing method asked for at 30 Hz writes a "Method not found" line
    /// per call, and this mod has already had a reader flood a session log with
    /// thousands of them.</para></summary>
    private static (float vx, float vz, float speed, bool ok) EngineVelocity()
    {
        try
        {
            var avatar = FieldRayCaster.PlayerAvatar();
            var td = avatar?.GetTypeDefinition();
            string key = td?.GetFullName();
            if (key == null) return (0f, 0f, 0f, false);
            if (!VelocityByAvatar.TryGetValue(key, out var m))
            {
                for (var t = td; t != null && m == null; t = t.ParentType)
                {
                    try { m = t.GetMethod(VELOCITY_METHOD); } catch { }
                }
                VelocityByAvatar[key] = m;
            }
            if (m == null) return (0f, 0f, 0f, false);

            // No target type is named for the return: naming a generated vec3
            // interface wraps the value in a dispatch proxy that reads back as zeros
            // (the trap documented on FieldRayContacts).
            var v = m.InvokeBoxed(typeof(object), avatar, null);
            if (v == null) return (0f, 0f, 0f, false);
            float x = FlowHelper.ReadVecComponent(v, "x");
            float z = FlowHelper.ReadVecComponent(v, "z");
            if (!float.IsFinite(x) || !float.IsFinite(z)) return (0f, 0f, 0f, false);
            float speed = (float)Math.Sqrt(x * x + z * z);
            if (speed <= 0f) return (0f, 0f, 0f, true);
            return (x / speed, z / speed, speed, true);
        }
        catch { return (0f, 0f, 0f, false); }
    }
}
