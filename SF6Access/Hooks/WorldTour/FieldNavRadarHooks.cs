using System.Collections.Generic;
using System.Runtime.InteropServices;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// <b>World Tour navigation radar</b> — echolocation of GEOMETRY: walls, openings
/// and drops, read from the avatar's own sensing rays through
/// <see cref="FieldNavRadarService"/>. It is the complement of
/// <see cref="FieldAwarenessHooks"/> (N), which names PEOPLE; the two never talk
/// about the same thing and neither knows about the other.
///
/// <list type="bullet">
/// <item><b>B</b> — one-shot readout: the obstacle in front and how far, whether
///   each side is open or blocked, and whether there is floor ahead.</item>
/// <item><b>Shift+B</b> — toggles the continuous mode: the REACTIVE RADAR
///   (<see cref="FieldRadarService"/>, the replication of the RE7 mod's
///   <c>RadarService</c>) plus SPEECH only when the game's own verdict changes.
///   The radar is silent until the shape of the street around the avatar CHANGES
///   and then makes ONE panned sound; words stay rare for the reason the RE7 radar
///   kept them rare — a voice that talks constantly is one the player filters out.</item>
/// </list>
///
/// <para><b>Cue vocabulary</b> — <c>exit.mp3</c> is a way opening on that side,
/// <c>impassable.mp3</c> a wall closing in, a rising three-note motif is stairs, a
/// single rising note is contact approaching in TIME along the way you are
/// walking, and a descending three-note motif marks a drop, which is the only cue
/// here that is a safety matter rather than navigation. Speech carries the
/// obstacle CLASS, never the geometry.</para>
///
/// <para><b>Rebuilt 2026-09-08 as a replication of the RE7 mod's radar.</b> The
/// spoken class ("wall", "blocked", "wall you can run along") comes from the
/// GAME's own verdict (<c>NavReading.Block</c>, from <c>FieldNavVerdictService</c>),
/// never the ray ladder — it says "you are stopped, by this" at the moment of
/// contact. The four fixed-threshold open/closed BEAMS that used to sit here are
/// gone: they asked whether a direction reached further than a fixed twelve
/// metres, so the same doorway read as an exit from close up and as a wall from
/// across the street.</para>
///
/// <para>The mode is OPT-IN, so it deliberately does not stand down for the panel
/// guide the way the always-on readers do: geometry is orthogonal to whatever
/// those are guiding towards. It does hold for dialogue — nothing may talk over
/// the game's own voice — and for the shared field gate, so it never samples in a
/// menu or a battle.</para>
/// </summary>
public class FieldNavRadarHooks
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>B, the radar key. A letter shortcut, so it only ever acts while
    /// SF6 owns the foreground window.</summary>
    private const int VK_B = 0x42;
    private const int VK_SHIFT = 0x10;

    /// <summary>How often the continuous mode re-reads the GAME'S OWN verdict, in
    /// milliseconds on the monotonic clock.
    ///
    /// <para>Milliseconds, not LateUpdate ticks: a frame count means a different
    /// sampling rate on every machine, and a wall approached at a run has to be met
    /// at the same distance whatever the frame rate. The interval is RE7's
    /// <see cref="FieldRadarTuning.FallbackSenseMs"/> — that mod's own rate for a
    /// channel that yields only coarse information, which is exactly what an obstacle
    /// CLASS is. The geometry radar underneath runs far faster and on its own clock;
    /// see <see cref="FieldRadarService"/>.</para></summary>
    private const long VERDICT_SENSE_MS = FieldRadarTuning.FallbackSenseMs;

    /// <summary>Consecutive identical samples before a new situation is announced.
    /// Ray hits are binary tests against real geometry, so a railing, a doorframe or
    /// a lamp post can flicker a side feeler on and off as the player walks past it,
    /// and a cue per flicker is the noise this design exists to avoid. Two samples
    /// is a third of a second of confirmation — short enough to still warn before a
    /// wall, long enough to swallow a single-sample flicker.</summary>
    internal const int CONFIRM_SAMPLES = 2;

    /// <summary>The drop warning: a DESCENDING motif, so the shape of the sound is
    /// the shape of the hazard. Built from AudioService's equal-temperament note
    /// constants rather than raw frequencies.</summary>
    private static readonly float[] DropMotif =
        { AudioService.NoteLaHigh, AudioService.NoteMi, AudioService.NoteLa };

    private static bool _keyDown;
    private static bool _continuous;
    private static long _nextVerdictAt;

    // The last CONFIRMED situation, and the one currently being confirmed.
    private static NavReading _announced;
    private static bool _haveBaseline;
    private static NavReading _pending;
    private static int _pendingSamples;

    [PluginEntryPoint]
    public static void Initialize()
    {
        API.LogInfo("[SF6Access] FieldNavRadarHooks initialized (B = navigation radar, Shift+B = continuous)");
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        // The key must be sampled every frame — a short press between two polls is
        // a press the player has to repeat.
        bool down = (GetAsyncKeyState(VK_B) & 0x8000) != 0;
        bool edge = down && !_keyDown;
        _keyDown = down;

        FieldPresenceService.Refresh();

        if (edge && ReadoutShortcut.IsGameForeground()) HandlePress();

        if (!_continuous) return;
        // The shared field gate: in a walkable World Tour field, no menu owning the
        // screen, no fight. Never cast rays anywhere else.
        if (!FieldPresenceService.CanSpeak) { ResetContinuous(); return; }
        // Nothing may sound over the game's own dialogue voice.
        if (SF6Access.Hooks.SpTalkNovelHooks.DialogueActive) return;

        // The reactive radar — the replication of the RE7 mod's RadarService. It owns
        // its own monotonic cadence (faster while the player turns), so it is offered
        // every frame and decides for itself when to sense.
        FieldRadarService.Update();

        long clock = System.Environment.TickCount64;
        if (clock < _nextVerdictAt) return;
        _nextVerdictAt = clock + VERDICT_SENSE_MS;

        var now = FieldNavRadarService.Sample();
        if (now.Ok) Confirm(now);

        var cameraForward = FieldDirectionService.GetCameraForward();
        // Automatic diagnostic: the player is blind and cannot aim F10 at a
        // specific object, so every sample where the game's own verdict is the
        // one BlockPhrase() would speak as "wall"/"blocked" (never the ray
        // ladder) also logs what is actually being hit. ContactCatalog does its
        // own identity dedupe and log-rate limiting, so this fires unconditionally
        // while blocked.
        if (now.Ok && now.Block == FrontBlock.Blocked) ContactCatalog.Note(cameraForward);
    }

    /// <summary>Shift+B toggles the continuous mode; B alone answers once. Both are
    /// refused outside the field: an explicit press deserves an answer, but there is
    /// no geometry to answer with in a menu or a battle.</summary>
    private static void HandlePress()
    {
        if (!FieldPresenceService.CanSpeak)
        {
            API.LogInfo("[SF6Access] Nav radar key pressed outside the World Tour field — ignored " +
                        $"(InField={FieldPresenceService.InField}, Fighting={FieldPresenceService.Fighting})");
            return;
        }

        if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0) ToggleContinuous();
        else ReadOutOnce();
    }

    private static void ToggleContinuous()
    {
        _continuous = !_continuous;
        ResetContinuous();
        ScreenReaderService.Speak(_continuous ? LocalizedText.NavRadarOn() : LocalizedText.NavRadarOff(),
                                  interrupt: true);
        API.LogInfo($"[SF6Access] Nav radar continuous mode {(_continuous ? "on" : "off")}");
    }

    /// <summary>The full situation, in answer to a press. Spoken with an interrupt:
    /// a request must always be answered, ahead of whatever was being said.</summary>
    private static void ReadOutOnce()
    {
        var r = FieldNavRadarService.Sample();
        if (!r.Ok)
        {
            API.LogInfo("[SF6Access] Nav radar: no reading (avatar field state or ray API unreachable)");
            return;
        }
        ScreenReaderService.Speak(Describe(r), interrupt: true);
        // The continuous mode's baseline is now stale relative to what the player
        // has just been told; re-seed it so the next change is measured from here.
        if (_continuous) Seed(r);
    }

    private static string Describe(NavReading r)
    {
        // Measure the sides with the radar's own body-clearance sensor so the spoken
        // readout and the cues quote ONE measurement. The game-state verdict stays as
        // the fallback: a side reported open because the sensor failed is the one
        // answer a navigation readout may never give.
        var cam = FieldDirectionService.GetCameraForward();
        bool sensed = cam.Ok && FieldRadarSense.Measure(cam);

        var parts = new List<string>(4 + MAX_WAYS_SPOKEN)
        {
            FrontPhrase(r),
            SidePhrase(RadarBeam.Left, sensed, r.LeftBlocked),
            SidePhrase(RadarBeam.Right, sensed, r.RightBlocked),
            r.GroundSolid ? LocalizedText.NavFloorSolid() : LocalizedText.NavFloorDrop(),
        };
        AppendWaysOut(parts);
        return string.Join(", ", parts);
    }

    /// <summary>One side, with the distance the avatar's BODY could travel that way.
    ///
    /// <para>"left open" on its own was true of anything past two metres, so the
    /// readout could confirm a cue about a shopfront recess and then, three steps
    /// later, report the wall behind it. The metre figure is the whole difference
    /// between "you can go left" and "there is two metres of left".</para></summary>
    private static string SidePhrase(RadarBeam beam, bool sensed, bool blockedFallback)
    {
        if (sensed)
        {
            var reading = FieldRadarSense.Beam(beam);
            if (reading.Ok)
            {
                float clear = reading.Mid;
                bool blocked = clear <= FieldRadarClearance.BlockedAtM(FieldRayCaster.CapsuleRadius());
                if (beam == RadarBeam.Left)
                    return blocked ? LocalizedText.NavObstacleAt(LocalizedText.NavLeftBlocked(), clear)
                                   : LocalizedText.NavLeftClearFor(clear);
                return blocked ? LocalizedText.NavObstacleAt(LocalizedText.NavRightBlocked(), clear)
                               : LocalizedText.NavRightClearFor(clear);
            }
        }

        if (beam == RadarBeam.Left)
            return blockedFallback ? LocalizedText.NavLeftBlocked() : LocalizedText.NavLeftOpen();
        return blockedFallback ? LocalizedText.NavRightBlocked() : LocalizedText.NavRightOpen();
    }

    /// <summary>How many ways out one press may list. A readout has to stay a
    /// sentence the player can hold in their head, and the list arrives nearest
    /// first, so the ones past this are the ones a second press from a few steps
    /// along would answer better anyway.</summary>
    private const int MAX_WAYS_SPOKEN = 3;

    /// <summary>Name the ways out of where the avatar is standing, from the GAME'S
    /// OWN navmesh (<see cref="NavMeshOpenings"/>) rather than from geometry rays.
    ///
    /// <para>This is the part the ray-based radar could never do. A ring of rays
    /// cannot answer "where is the exit": a 1.6 m doorway six metres away subtends
    /// about five degrees, so at any ray spacing coarse enough to afford, no ray
    /// goes through it — and a wall seen at a grazing angle produces exactly the
    /// same big jump between neighbouring rays that a real gap does. (Both were
    /// reproduced in a standalone simulation before this was written, which is why
    /// no ray-based gap finder ships here.) A navmesh has no such limit: a walkable
    /// polygon's links ARE the ways out, and the shared edge between two polygons
    /// has a real width, so "1.8 metres wide" is a measurement and "you fit"
    /// compares it against the avatar's own capsule.</para>
    ///
    /// <para>Passable ways are listed first and alone. Only when none of them fit
    /// is a too-narrow gap spoken, because "there is a gap there and you cannot use
    /// it" is worth knowing precisely when there is nothing better to report.</para></summary>
    /// <summary>The navmesh route is DISABLED after it crashed the game on its first
    /// in-game press (2026-09-08): the log stops dead at the radar sample that runs
    /// immediately before it, and no <c>NavMesh openings</c> line was ever written, so
    /// it died inside the first query. This mod has form here — constructing a generic
    /// interface through the TDB once produced a bogus managed wrapper whose finalizer
    /// raised an AccessViolationException on the GC thread, with an equally clean log —
    /// so the route stays off, rather than being left in behind a try/catch that a
    /// native fault walks straight through.
    ///
    /// <para>The reader itself was rewritten afterwards (<see cref="NavMeshNodes"/> +
    /// <see cref="NavMeshOpenings"/>): every engine call now writes a numbered
    /// <c>NavMesh step</c> line BEFORE it runs, so the next attempt cannot be silent —
    /// the last step in the log will name the call that does not return. Turning this
    /// back on is the USER'S decision, not a code decision; it stays <c>false</c> until
    /// they ask for the run.</para></summary>
    /// Kept as a field rather than a const so the disabled body still compiles as
    /// live code: a kill switch that rots the code behind it is one nobody can
    /// re-enable safely.
    private static readonly bool MESH_WAYS_ENABLED = false;

    private static void AppendWaysOut(List<string> parts)
    {
        if (!MESH_WAYS_ENABLED) return;
        var ways = NavMeshOpenings.FromPlayer();
        if (ways.Count == 0) return;

        var forward = FieldDirectionService.GetCameraForward();
        var me = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
        if (!forward.Ok || !me.ok) return;

        int spoken = 0;
        foreach (var w in ways)
        {
            if (spoken >= MAX_WAYS_SPOKEN) break;
            if (!w.Passable) continue;
            int hour = FieldDirectionService.ClockHour(forward, w.X - me.x, w.Z - me.z);
            if (hour == 0) continue;
            parts.Add(LocalizedText.NavOpening(hour, w.WidthM, w.DistanceM));
            spoken++;
        }
        if (spoken > 0) return;

        foreach (var w in ways)
        {
            if (spoken >= MAX_WAYS_SPOKEN) break;
            int hour = FieldDirectionService.ClockHour(forward, w.X - me.x, w.Z - me.z);
            if (hour == 0) continue;
            parts.Add(LocalizedText.NavGapTooNarrow(hour, w.WidthM));
            spoken++;
        }
        if (spoken == 0) parts.Add(LocalizedText.NavNoOpenings());
    }

    /// <summary>The obstacle class with its distance. When nothing is blocking and
    /// the height stack is open the distance still matters — it is how far the long
    /// forward ray reached before finding something — but it is a different sentence,
    /// because "clear ahead at 1.9 meters" would read as an obstruction.</summary>
    private static string FrontPhrase(NavReading r)
    {
        if (r.Block != FrontBlock.None) return WithDistance(BlockPhrase(r), r);
        if (r.Front == FrontProfile.Open)
            return r.HasDistance ? LocalizedText.NavClearFor(r.Distance) : LocalizedText.NavFront(r.Front);
        return WithDistance(LocalizedText.NavFront(r.Front), r);
    }

    private static string WithDistance(string what, NavReading r)
        => r.HasDistance ? LocalizedText.NavObstacleAt(what, r.Distance) : what;

    /// <summary>The word for the game's own "you are stopped" verdict. The rays are
    /// used only to NAME what is stopping the avatar, never to decide that it is:
    /// when they saw nothing — a fence or a prop the ray filter does not report, but
    /// the capsule collides with — the plain "blocked" is the honest answer, and
    /// saying "clear ahead" there is precisely the false positive reported in
    /// play.</summary>
    private static string BlockPhrase(NavReading r) => r.Block switch
    {
        FrontBlock.WallRide => LocalizedText.NavWallRide(),
        _ when r.Front != FrontProfile.Open => LocalizedText.NavFront(r.Front),
        _ => LocalizedText.NavBlocked(),
    };

    /// <summary>Hold a new situation for <see cref="CONFIRM_SAMPLES"/> consecutive
    /// samples, then cue the transition from the last confirmed one — exactly once,
    /// since the counter only equals the threshold on a single sample.</summary>
    private static void Confirm(NavReading now)
    {
        if (!now.SameStateAs(_pending)) { _pending = now; _pendingSamples = 1; return; }
        if (++_pendingSamples != CONFIRM_SAMPLES) return;

        var previous = _announced;
        bool hadBaseline = _haveBaseline;
        Seed(now);
        // The first confirmed reading after switching on (or after the gate closed
        // and reopened) is a BASELINE, not an event: cueing it would fire a burst of
        // sounds describing where the player was already standing.
        if (hadBaseline) Cue(previous, now);
    }

    /// <summary>Everything that changed in the game's verdict: the drop motif and
    /// the one spoken class. Proximity is not cued here at all: it is
    /// spoken once rather than also chimed.</summary>
    private static void Cue(NavReading was, NavReading now)
    {
        // The drop goes first: it is the only cue that is a safety matter, so it
        // must not queue behind a wall cue in the same sample.
        if (was.GroundSolid && !now.GroundSolid) AudioService.PlayTone(DropMotif);

        // BLOCKED is the game's own verdict, never the ray ladder: a rung that hits
        // means "something of about this height is somewhere along that ray", which
        // is not the same question and was answering it wrongly in both directions.
        bool wasBlocked = was.Block == FrontBlock.Blocked;
        bool isBlocked = now.Block == FrontBlock.Blocked;
        // Without interrupting: the word only has to arrive, not to arrive first.
        if (!wasBlocked && isBlocked) ScreenReaderService.Speak(BlockPhrase(now), interrupt: false);

        if (isBlocked && wasBlocked && was.Front != now.Front)
        {
            // Blocked before and blocked still, but a DIFFERENT obstacle — walking
            // from a kerb up to the wall behind it. That used to be silent, and it
            // is exactly the moment the player's options change (a Step can be
            // walked over, a Wall cannot). NO sound: the cue pair means
            // "closed" / "opened" and neither happened, so re-firing one would lie.
            // The word alone carries it, and the confirmation window plus the
            // reader's duplicate filter keep a wobbling class from chattering.
            ScreenReaderService.Speak(BlockPhrase(now), interrupt: false);
        }

        // A wall the avatar can run along is a route, not a dead end, so it never
        // fires the impassable cue — but arriving at one is worth a word.
        if (now.Block == FrontBlock.WallRide && was.Block != FrontBlock.WallRide)
            ScreenReaderService.Speak(LocalizedText.NavWallRide(), interrupt: false);
    }

    private static void Seed(NavReading r)
    {
        _announced = r;
        _pending = r;
        _pendingSamples = CONFIRM_SAMPLES;
        _haveBaseline = true;
    }

    /// <summary>Forget everything, so switching the mode on — or walking back into
    /// the field after a menu — starts from a fresh silent baseline.</summary>
    private static void ResetContinuous()
    {
        _nextVerdictAt = 0;
        FieldRadarService.Reset();
        _announced = default;
        _pending = default;
        _pendingSamples = 0;
        _haveBaseline = false;
        FieldRayMetrics.Reset();
        NavMeshOpenings.Reset();
    }
}
