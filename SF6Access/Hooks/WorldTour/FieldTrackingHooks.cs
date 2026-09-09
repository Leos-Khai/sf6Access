using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// Continuous field tracker (WT-1 follow-up, user-requested): hands-free
/// guidance toward the nearest NAMED person without hammering the radar key.
/// Their camera-relative clock hour and distance are spoken, with the full
/// name repeated only when the target CHANGES.
///
/// <para><b>Notable people only (2026-09-07).</b> The tracker used to follow the
/// literal nearest avatar, and in a street that is a passer-by every few
/// steps: each one a sentence, each one passing at arm's length so its clock
/// hour swept half the dial in a second — a word per hour. That was the
/// "distance spam while walking" reported in play. The crowd is now the
/// homing pulse's business (a sound, not a sentence); the voice follows the
/// nearest MASTER or player — see <see cref="AvatarNameCache"/> for what
/// counts and why a name alone does not.</para>
///
/// <para><b>Events plus a distance-paced repeat</b> (2026-09-06, user request
/// "verbalise more often, like the beacon"): once walking toward the same
/// target, a terse update is spoken when the clock HOUR changes or the distance
/// crosses a coarse band (see <see cref="CrossedBand"/>), and otherwise on a
/// repeat whose period shrinks as the target gets closer — the same shape as
/// the homing pulse (<see cref="REPEAT_NEAR_MS"/> at the interaction radius,
/// <see cref="REPEAT_FAR_MS"/> at the edge of the homing range), so the voice
/// and the sound agree about urgency. Hour changes are NOT spoken inside
/// <see cref="CLOSE_M"/>: that near, a step sideways is an hour, and the pulse's
/// pan already says it. A fixed 2 s repeat was tried first and read as
/// nagging; a purely event-based version (2026-09-05) went quiet for too long
/// on a straight approach; 3 s / 8 s (2026-09-06) was too dense once the
/// events piled on top of it.</para>
///
/// <para><b>Always on</b> (user rule 2026-08-14): no toggle key. The silence
/// rules below are what keeps that bearable — it is quiet unless the reading
/// actually changed, so standing still costs nothing.</para>
///
/// Silence rules (so it never talks over what matters):
/// <list type="bullet">
/// <item>only speaks when the spoken text actually changed — standing still
///   stays silent;</item>
/// <item>holds while the panel guide is running
///   (<see cref="PadGuideHooks.Active"/>), which owns the objective and the mic
///   during that tutorial;</item>
/// <item>holds while a World Tour dialogue is on screen
///   (<see cref="SF6Access.Hooks.SpTalkNovelHooks.DialogueActive"/>);</item>
/// <item>holds while any target is in interaction range — arrival is announced
///   by <see cref="FieldAwarenessHooks"/>'s target-change reader, which owns
///   that moment;</item>
/// <item>auto-stops silently when the field unloads (leaving World Tour).</item>
/// </list>
/// </summary>
public class FieldTrackingHooks
{
    // Poll cadence in LateUpdate ticks (0.5 s at 60 fps): fine enough that a
    // due repeat lands close to its time, coarse enough that walking the avatar
    // list stays cheap. Whether an update is actually spoken is decided by the
    // hour/band/repeat check below, never by this counter alone.
    private const int POLL_TICKS = 30;

    /// <summary>Repeat period when the target is at the interaction radius
    /// (<see cref="MissionBeaconHooks.ARRIVED_M"/>): five seconds — a sentence
    /// and a breath, with the pulse sounding several times in between.</summary>
    private const long REPEAT_NEAR_MS = 5000;

    /// <summary>Repeat period at the edge of the homing range
    /// (<see cref="FieldBeaconHooks.HOME_RANGE_M"/>) and beyond: ten seconds,
    /// so a long approach is still narrated but not nagged.</summary>
    private const long REPEAT_FAR_MS = 10000;

    /// <summary>Inside this the clock hour is left to the pulse: twice the
    /// interaction radius, i.e. the last few steps, where the bearing to a
    /// person swings through several hours from one stride to the next.</summary>
    private const float CLOSE_M = 2f * MissionBeaconHooks.ARRIVED_M;

    // Hold after the reader speaks with an interrupt, so an update never lands on
    // top of a tutorial line or an arrival announcement.
    private const long READER_HOLD_MS = 1200;

    private static StickyTarget Target => StickyTarget.NearestNamed;

    private static int _tick;
    private static string _lastTargetDesc;
    private static string _lastSpoken;
    private static int _lastHour;
    private static int _lastAnnouncedMeters;
    private static long _nextRepeatTick;

    [PluginEntryPoint]
    public static void Initialize()
    {
        API.LogInfo("[SF6Access] FieldTrackingHooks initialized");
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        FieldPresenceService.Refresh();

        // ONLY WHILE WALKING (user rule 2026-08-14). Without this the reader
        // repeated distances endlessly while the player stood reading a tutorial
        // or a menu, talking across the game's own text. Standing still is also
        // exactly when the reading is least useful: it cannot have changed for
        // any reason the player caused.
        if (!FieldPresenceService.CanSpeakWhileMoving)
        {
            // Forget the last reading so the next walk speaks again rather than
            // suppressing itself as a duplicate.
            Reset();
            return;
        }

        var mgr = WorldTourStateService.GetAvatarManager();
        if (mgr == null) { Reset(); return; }

        // Hold (without disabling) while a dialogue line is on screen, while the
        // panel guide is running, or while something is already in interaction
        // range — those readers own the mic.
        if (SF6Access.Hooks.SpTalkNovelHooks.DialogueActive) return;
        if (PadGuideHooks.Active) return;
        if (AvatarFieldReader.GetAccessInfoCount(mgr) > 0) return;
        // Anything the reader just announced with an interrupt — a tutorial line,
        // an arrival — gets to finish. The WT dialogue flag above only covers
        // novel-style dialogue, not tutorial text, so this is what protects it.
        if (System.Environment.TickCount64 - ScreenReaderService.LastInterruptTick < READER_HOLD_MS) return;

        if (++_tick < POLL_TICKS) return;
        _tick = 0;

        var notable = AvatarNameCache.Notable(AvatarFieldReader.ReadOthers(mgr));
        if (notable.Count == 0) return;

        var nearest = Target.Pick(notable);
        int meters = (int)System.Math.Round(nearest.Dist);
        int hour = FieldDirectionService.ClockHour(
            FieldDirectionService.GetCameraForward(), nearest.Dx, nearest.Dz);

        string desc = AvatarNameCache.NameOf(nearest);
        bool newTarget = desc != _lastTargetDesc;
        _lastTargetDesc = desc;

        long now = System.Environment.TickCount64;
        string spoken;
        bool periodic = false;
        if (newTarget)
        {
            // The full sentence always fires on a target change — that is the
            // one event this tracker must never stay silent about.
            spoken = hour > 0
                ? LocalizedText.AtClockMeters(desc, hour, meters)
                : LocalizedText.AtMeters(desc, meters);
        }
        else
        {
            // Same target: another word when the clock hour moved, the distance
            // crossed a coarse band, or the distance-paced repeat is due.
            bool hourMoved = hour != _lastHour && nearest.Dist > CLOSE_M;
            bool changed = hourMoved || CrossedBand(meters);
            periodic = !changed && now >= _nextRepeatTick;
            if (!changed && !periodic) return;
            spoken = hour > 0 ? LocalizedText.ClockShort(hour, meters) : LocalizedText.AtMeters(desc, meters);
        }

        _lastHour = hour;
        _lastAnnouncedMeters = meters;

        // An event that lands on the same phrase as last time is not news. A
        // due repeat IS allowed to say the same thing again: that is its job.
        if (!periodic && spoken == _lastSpoken) return;
        _lastSpoken = spoken;
        _nextRepeatTick = now + RepeatPeriod(meters);

        ScreenReaderService.Speak(spoken, interrupt: false);
    }

    /// <summary>Milliseconds until the next repeat: shortest at the interaction
    /// radius, longest from the edge of the homing range out, straight-line in
    /// between — the same ramp the homing pulse plays.</summary>
    private static long RepeatPeriod(int meters)
    {
        float span = FieldBeaconHooks.HOME_RANGE_M - MissionBeaconHooks.ARRIVED_M;
        float t = span <= 0f ? 1f : System.Math.Clamp((meters - MissionBeaconHooks.ARRIVED_M) / span, 0f, 1f);
        return (long)(REPEAT_NEAR_MS + t * (REPEAT_FAR_MS - REPEAT_NEAR_MS));
    }

    /// <summary>Whether the rounded distance has moved far enough from the last
    /// ANNOUNCED one to be worth a fresh update: halved (closing in) or doubled
    /// (pulling away). Anything finer is noise — a sticky target's distance
    /// drifts by a metre or two from normal walking alone.
    ///
    /// <para>The reference never shrinks below <see cref="MissionBeaconHooks.ARRIVED_M"/>:
    /// that is the same "close enough" radius the mission beacon already uses
    /// for this field, reused here instead of inventing a second one, and it
    /// keeps the ladder from producing sub-metre bands right as the target is
    /// about to be reached (at which point the interaction-range gate above
    /// takes over anyway).</para>
    /// </summary>
    private static bool CrossedBand(int meters)
    {
        if (_lastAnnouncedMeters <= 0) return true;
        float reference = System.Math.Max(_lastAnnouncedMeters, MissionBeaconHooks.ARRIVED_M);
        return meters <= reference / 2f || meters >= reference * 2f;
    }

    private static void Reset()
    {
        Target.Reset();
        _tick = 0;
        _lastTargetDesc = null;
        _lastSpoken = null;
        _lastHour = 0;
        _lastAnnouncedMeters = 0;
        _nextRepeatTick = 0;
    }
}
