using System.Collections.Generic;
using REFrameworkNET;
using REFrameworkNET.Attributes;
using REFrameworkNET.Callbacks;
using SF6Access.Services;
using SF6Access.Services.WorldTour;

namespace SF6Access.Hooks.WorldTour;

/// <summary>
/// "What is around me?" — <b>Shift+Z</b> sweeps the eight compass directions
/// from where the player stands and answers in one sentence: "Open: north,
/// east. Walls: south 3 meters, west 6 meters". Z alone is "where am I"
/// (<see cref="ZoneHooks"/>); the chord is the same question one step wider.
///
/// <para>On demand only, by design: a picture of the street is worth asking
/// for and worthless as a running commentary. The sensor is
/// <see cref="FieldNavRadarService.Sweep"/>; the directions are absolute
/// (compass points), so the answer does not change when the camera turns —
/// that is what makes it a map rather than a feeler.</para>
/// </summary>
public class FieldSweepHooks
{
    private const int VK_Z = 0x5A;

    private static readonly ReadoutShortcut SweepKey = new(VK_Z, ReadoutShortcut.PAD_NONE, shift: true);

    [PluginEntryPoint]
    public static void Initialize()
    {
        API.LogInfo("[SF6Access] FieldSweepHooks initialized (Shift+Z = compass sweep)");
    }

    [Callback(typeof(LateUpdateBehavior), CallbackType.Post)]
    public static void Tick()
    {
        if (!SweepKey.Pressed()) return;

        FieldPresenceService.Refresh();
        if (!FieldPresenceService.CanSpeak)
        {
            API.LogInfo("[SF6Access] Sweep key pressed outside the World Tour field — ignored");
            return;
        }

        var hits = FieldNavRadarService.Sweep();
        if (hits == null)
        {
            ScreenReaderService.Speak(LocalizedText.SweepUnavailable(), interrupt: true);
            return;
        }
        ScreenReaderService.Speak(Describe(hits), interrupt: true);
    }

    private static string Describe(float[] hits)
    {
        var open = new List<string>(hits.Length);
        var walls = new List<string>(hits.Length);
        for (int k = 0; k < hits.Length; k++)
        {
            string point = FieldHeadingService.SectorName(k);
            if (hits[k] <= 0f) open.Add(point);
            else walls.Add(LocalizedText.SweepEntry(point, (int)System.Math.Round(hits[k])));
        }

        if (walls.Count == 0) return LocalizedText.SweepAllOpen();
        var parts = new List<string>(2);
        if (open.Count > 0) parts.Add(LocalizedText.SweepOpen(string.Join(", ", open)));
        parts.Add(LocalizedText.SweepWalls(string.Join(", ", walls)));
        return string.Join(". ", parts);
    }
}
