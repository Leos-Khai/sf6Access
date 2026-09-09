using REFrameworkNET;
using REFrameworkNET.Attributes;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// Briefly freezes the World Tour field camera's LOOK input (right stick and
/// mouse) so a spoken direction lands while the player is still pointing where
/// the reader said.
///
/// <para><b>Why:</b> the player turns to align with a heading or a person. By
/// the time the reader has said "north" the stick has carried the camera past
/// it. A short hold on the look input at the moment of the announcement gives
/// them the beat they need to let go of the stick.</para>
///
/// <para><b>How:</b> <c>app.worldtour.WTPlayerCameraController</c> consumes the
/// look input in two per-frame methods: <c>camera_input_proc(dt)</c> for the
/// pad stick and <c>camera_input_proc_with_mouse(dt, axis)</c> for the mouse
/// (found in the decompiled controller). A pre-hook on each returns
/// <see cref="PreHookResult.Skip"/> while a hold is active, so those frames
/// apply no look input at all. Nothing else in the controller (follow, collision,
/// preset cameras) is touched, and movement input is a different code path.</para>
///
/// <para>The hooks are dynamic (<c>AddHook(false)</c>, pre only) like every
/// other hook in this plugin, and they hook the method, not an instance, so the
/// controller being recreated between cities costs nothing.</para>
/// </summary>
public static class CameraHoldService
{
    private const string CONTROLLER = "app.worldtour.WTPlayerCameraController";
    private const string PAD_PROC = "camera_input_proc";
    private const string MOUSE_PROC = "camera_input_proc_with_mouse";

    /// <summary>How long a direction announcement freezes the look input.
    /// User preference (2026-09-05): long enough to release the stick, short
    /// enough not to read as a stutter.</summary>
    public const int HOLD_MS = 50;

    private static long _holdUntil;
    private static bool _hooked;

    /// <summary>Whether a hold is in force right now.</summary>
    public static bool Holding => System.Environment.TickCount64 < _holdUntil;

    /// <summary>Whether the controller methods were found and hooked. False means
    /// the announcements still speak but nothing freezes.</summary>
    public static bool Available => _hooked;

    [PluginEntryPoint]
    public static void Initialize()
    {
        var td = TDB.Get().FindType(CONTROLLER);
        var pad = td?.GetMethod(PAD_PROC);
        var mouse = td?.GetMethod(MOUSE_PROC);
        if (pad == null && mouse == null)
        {
            API.LogWarning($"[SF6Access] CameraHoldService: {CONTROLLER} look-input methods not found; camera hold disabled");
            return;
        }

        pad?.AddHook(false).AddPre(_ => Holding ? PreHookResult.Skip : PreHookResult.Continue);
        mouse?.AddHook(false).AddPre(_ => Holding ? PreHookResult.Skip : PreHookResult.Continue);
        _hooked = true;
        API.LogInfo($"[SF6Access] CameraHoldService initialized (pad={pad != null}, mouse={mouse != null}, hold={HOLD_MS} ms)");
    }

    /// <summary>Freeze the look input for <see cref="HOLD_MS"/> from now. Calling
    /// it again during a hold extends it; it never shortens one.</summary>
    public static void Hold()
    {
        long until = System.Environment.TickCount64 + HOLD_MS;
        if (until > _holdUntil) _holdUntil = until;
    }
}
