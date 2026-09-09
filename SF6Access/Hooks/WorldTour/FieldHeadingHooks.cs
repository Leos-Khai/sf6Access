using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// Hands-free compass for the World Tour field: as the camera turns, the
/// compass point it now faces is spoken ("north", "southwest"), so the city
/// gets a fixed frame that does not move with the player the way clock hours
/// do. North itself comes from <see cref="FieldHeadingService"/>.
///
/// <para><b>Quiet by construction:</b> it only speaks when the compass point
/// CHANGES, and a change only counts once the heading is well inside the new
/// sector (<see cref="HYSTERESIS_DEG"/>), so aiming along a boundary never
/// flaps between two names. Standing still and walking straight cost nothing.
/// The first reading after entering the field is remembered silently.</para>
///
/// <para>Each announcement freezes the look input for a beat
/// (<see cref="CameraHoldService"/>) so the word lands while the player is
/// still pointing at what it names — the reason the user asked for a compass
/// in the first place.</para>
/// </summary>
public class FieldHeadingHooks
{
    /// <summary>A sector switch is accepted only once the heading is this far
    /// past the boundary. One sixth of a sector (7.5° of 45°): enough that a
    /// hand resting on the stick does not toggle two names, small enough that
    /// a deliberate turn is answered without lag.</summary>
    private const float HYSTERESIS_DEG = FieldHeadingService.DEGREES_PER_SECTOR / 6f;

    /// <summary>Read cadence in LateUpdate ticks, the same 10 Hz as
    /// <see cref="FieldAimHooks"/>: reading the camera every frame only creates
    /// wrappers for the GC, and no turn crosses a 45° sector in a tenth of a
    /// second that the hysteresis would not have swallowed anyway.</summary>
    private const int POLL_TICKS = 6;

    private static int _tick;
    private static int _sector = -1;
    private static bool _wasInField;

    /// <summary>The compass point the camera faces right now, or null when it
    /// cannot be read. For on-demand readers that want to append it.</summary>
    public static string CurrentFacing()
    {
        float heading = FieldHeadingService.HeadingDegrees(FieldDirectionService.GetCameraForward());
        if (float.IsNaN(heading)) return null;
        return FieldHeadingService.SectorName(FieldHeadingService.Sector(heading));
    }

    [PluginEntryPoint]
    public static void Initialize()
    {
        API.LogInfo("[SF6Access] FieldHeadingHooks initialized (hands-free compass)");
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        FieldPresenceService.Refresh();

        // Leaving the field forgets north and the last sector: the next city
        // derives its own north, and arriving anywhere starts silent.
        if (!FieldPresenceService.InField)
        {
            if (_wasInField) { FieldHeadingService.Reset(); _sector = -1; }
            _wasInField = false;
            return;
        }
        _wasInField = true;

        if (!FieldPresenceService.CanSpeak) return;
        if (SF6Access.Hooks.SpTalkNovelHooks.DialogueActive) return;
        if (PadGuideHooks.Active) return;

        if (++_tick < POLL_TICKS) return;
        _tick = 0;

        float heading = FieldHeadingService.HeadingDegrees(FieldDirectionService.GetCameraForward());
        if (float.IsNaN(heading)) return;

        int candidate = FieldHeadingService.Sector(heading);
        if (_sector < 0) { _sector = candidate; return; }
        if (candidate == _sector) return;

        // Inside the new sector, but not yet clear of its edge: keep the old name.
        float fromCentre = System.Math.Abs(FieldHeadingService.OffsetFromSectorCentre(heading, candidate));
        if (fromCentre > FieldHeadingService.DEGREES_PER_SECTOR / 2f - HYSTERESIS_DEG) return;

        _sector = candidate;
        CameraHoldService.Hold();
        ScreenReaderService.Speak(FieldHeadingService.SectorName(_sector), interrupt: true);
    }
}
