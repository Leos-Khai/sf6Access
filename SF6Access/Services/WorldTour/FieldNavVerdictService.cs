using System;
using System.Collections.Generic;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// The navigation radar's VERDICT source: whether the avatar is actually stopped,
/// and whether what is in front of it is something the game will simply walk over.
/// Both answers are READ FROM THE GAME, never inferred from ray heights.
///
/// <para><b>Why this exists.</b> The radar's first cut classified the front purely
/// from which of the game's named forward rays hit. In play that produced false
/// positives in both directions, exactly as reported: "the way opened" while a
/// waist-high wall was still stopping the avatar (the rays of the stack have
/// different reaches, and <c>TerrainRayFilter</c> does not see the fences and props
/// the capsule nevertheless collides with), and "wall" for kerbs and low fences the
/// avatar climbs without the player doing anything. A ray answers "is there geometry
/// along this segment"; it cannot answer "am I stopped". The game answers that
/// itself, every frame, and publishes the answer for free.</para>
///
/// <para><b>The sources, in the order they are trusted:</b></para>
/// <list type="number">
/// <item><b>The avatar's volatile collision info</b> —
///   <c>AvatarBase.__GetVolatileParam().Collision</c>
///   (<c>AvatarFieldParam_Volatile.CollisionInfo</c>). <c>IsWallContact()</c> is the
///   game's own "I am against a wall" and <c>WallMovableRate</c> is how much of the
///   requested motion that wall still allows (0..1), so a shallow slide along a
///   surface is distinguishable from a dead stop. This route is CONFIRMED IN GAME
///   (2026-09-04 probe run: <c>IsGround</c>, <c>IsSlope</c>, <c>IsWallContact()</c>
///   and <c>WallContactInfoList</c> all read correctly).</item>
/// <item><b>The character controller</b> — <c>AvatarCollisionManager.CharaController</c>
///   (or the avatar's own <c>Components.CharacterController</c>, the route the probe
///   confirmed in game) and its cached <c>Wall</c> flag. Used only when the volatile
///   route does not bind: it is a plain boolean with no movable rate, so it cannot
///   tell a slide from a stop.</item>
/// <item><b>The auto-step cache</b> — <c>AvatarCollisionCache.GetGoupStepInfo(GoupStepTypes)</c>,
///   the game's own per-size-class "can I climb this by myself" (XS / S / M / FenceF —
///   note the game counts FENCES among the things it steps over). A size class whose
///   <c>Seted</c> flag is up is an obstacle the avatar will surmount, so it is spoken
///   as a step and never as a block.</item>
/// <item><b>The wall-ride exemption</b> — <c>AvatarBase.GetContactedWallInfos</c> and
///   <c>ContactedWallInfo.CanWallRide</c>. A wall the avatar can run along is a
///   surface to use, not a dead end. DISABLED: the call takes a caller-allocated
///   generic <c>IList&lt;ContactedWallInfo&gt;</c>, and constructing an interface
///   through REFramework yields a wrapper over a non-object that crashes in the
///   finalizer (see <c>CanWallRide</c>). Wall-ride is therefore never claimed.</item>
/// </list>
///
/// <para>Every handle is resolved once and cached by the owning type's name — the
/// concrete field state changes with what the avatar is doing, so a single global
/// handle would be the stale binding the house rules warn about. Which routes bound
/// is logged ONCE at info level, so an in-game test says which of them answered.</para>
/// </summary>
public static class FieldNavVerdictService
{
    /// <summary>The game's own step-up size classes. <c>_NUM_</c> is the count
    /// sentinel, not a class, and is never queried.</summary>
    private const string GOUP_STEP_ENUM = "app.worldtour.avatar.GoupStepTypes";
    private const string GOUP_STEP_SENTINEL = "_NUM_";

    /// <summary>The flag on a <c>GoupStepInfo</c> that means the game filled that
    /// size class in this frame — i.e. it found something it can climb.</summary>
    private const string CHECK_FLAG_ENUM =
        "app.worldtour.avatar.AvatarCollisionCache.GoupStepInfo.CheckFlagType";
    private const string SETED_FLAG = "Seted";

    /// <summary>Only used when <c>AvatarConstMoveParams.DashMove_StopWallRate</c>
    /// cannot be read. <c>WallMovableRate</c> is documented by its own use as a 0..1
    /// fraction of the requested motion the wall still allows, so the midpoint of
    /// that range is the one threshold derivable from the quantity itself rather than
    /// picked out of the air. It is a fallback and is reported as such in the log —
    /// the game's own constant is always preferred.</summary>
    private const float MOVABLE_RATE_FALLBACK = 0.5f;

    /// <summary>One frame's answer. <see cref="Bound"/> false means NO game-owned
    /// route could be read at all, and the caller must fall back to its own
    /// behaviour rather than treating the silence as "nothing is blocking".</summary>
    public readonly struct Verdict
    {
        /// <summary>Whether a game-owned route answered the BLOCK question. False
        /// means the caller must fall back to its own behaviour rather than treating
        /// the silence as "nothing is blocking". <see cref="Steppable"/> is reported
        /// independently and stays usable either way.</summary>
        public readonly bool Bound;
        public readonly FrontBlock Block;
        /// <summary>The game says it will climb whatever is in front by itself.</summary>
        public readonly bool Steppable;

        public Verdict(bool bound, FrontBlock block, bool steppable)
        {
            Bound = bound;
            Block = block;
            Steppable = steppable;
        }
    }

    // --- cached handles and enum values (resolved at first use, never per frame) ---
    private static readonly Dictionary<string, Method> GetGoupStepByCache = new();
    private static readonly Dictionary<string, Method> GetFlagByInfo = new();
    private static readonly List<int> StepTypeIds = new();
    private static int _setedFlagId = -1;
    private static bool _enumsRead;

    private static bool _thresholdRead;
    private static float _stopWallRate = float.NaN;

    // Which routes answered, for the single summary line.
    private static bool _routesLogged;
    private static bool _volatileOk, _charaCtrlOk, _goupStepOk, _wallRideTried;

    /// <summary>Read the game's verdict for this frame. Never throws: an unreachable
    /// route is an unbound route, and an unbound route is reported, not guessed.</summary>
    public static Verdict Read(ManagedObject avatar, ManagedObject state)
    {
        try
        {
            ReadEnumsOnce();

            FrontBlock block = ReadWallContact(avatar, state, out bool wallBound);
            // Always READ, so the one-time route log is honest about whether the
            // step route binds; only USED when nothing is stopping the avatar, since
            // an obstacle it is already pressed against is news whatever its size.
            bool steppable = ReadSteppable(state);

            LogRoutesOnce();
            // Bound describes the BLOCK question only. The step answer stands on its
            // own — "the game will climb this" is true whether or not a wall route
            // bound — so it is reported separately and the caller may use it even
            // while falling back to the rays for blocking.
            return new Verdict(wallBound, block, steppable && block == FrontBlock.None);
        }
        catch (Exception ex)
        {
            if (!_routesLogged)
            {
                _routesLogged = true;
                API.LogWarning($"[SF6Access] NavRadar verdict routes: none — {ex.GetType().Name}: {ex.Message}. " +
                               "Falling back to the ray height stack alone.");
            }
            return default;
        }
    }

    // ---------- 1 + 2: am I stopped? ----------

    /// <summary>The wall verdict, volatile-collision route first and the character
    /// controller as the fallback. <paramref name="bound"/> false means neither
    /// answered — which is NOT the same as "no wall".</summary>
    private static FrontBlock ReadWallContact(ManagedObject avatar, ManagedObject state, out bool bound)
    {
        var col = CollisionInfo(avatar, state);
        if (col != null)
        {
            _volatileOk = true;
            bound = true;
            return FromCollisionInfo(avatar, state, col);
        }

        var cc = CharaController(avatar, state);
        if (cc == null) { bound = false; return FrontBlock.None; }

        _charaCtrlOk = true;
        bound = true;
        // A plain cached boolean: no movable rate to soften it with, so a contact is
        // a block. Wall-ride is still checked, since that answer comes from the avatar.
        bool wall = Truth(FieldProbeService.Member(cc, "Wall", typeof(bool))) == true;
        if (!wall) return FrontBlock.None;
        return CanWallRide(avatar) ? FrontBlock.WallRide : FrontBlock.Blocked;
    }

    /// <summary>The game's own wall reasoning, in its own order of authority:
    /// no contact at all, then the surface being one the avatar can run along, then
    /// the game's explicit "this wall stops a dash", and finally how much motion the
    /// wall still allows measured against the game's own stopping rate.</summary>
    private static FrontBlock FromCollisionInfo(ManagedObject avatar, ManagedObject state, ManagedObject col)
    {
        // The game marks its own wall data invalid on frames where it did not
        // resolve one; reading it anyway would be the stale read the house rules ban.
        if (Truth(FieldProbeService.Member(col, "ValidWallContactInfo", typeof(bool))) == false)
            return FrontBlock.None;
        if (Truth(FlowHelper.Call(col, "IsWallContact")) != true)
            return FrontBlock.None;

        if (CanWallRide(avatar)) return FrontBlock.WallRide;

        // The game's own verdict that this wall is hard enough to stop a dash.
        if (Truth(FieldProbeService.Member(col, "IsContactedDashStopWall", typeof(bool))) == true)
            return FrontBlock.Blocked;

        float rate = FieldProbeService.ToFloat(FieldProbeService.Member(col, "WallMovableRate", typeof(float)));
        // Outside its own 0..1 range the value was not read, and an unread rate is no
        // reason to tell the player a wall they are touching is not there.
        if (!float.IsFinite(rate) || rate < 0f || rate > 1f) return FrontBlock.Blocked;

        return rate < StopWallRate(state) ? FrontBlock.Blocked : FrontBlock.None;
    }

    /// <summary>The rate below which the game itself stops movement against a wall,
    /// from <c>AvatarConstMoveParams.DashMove_StopWallRate</c>. Read once: it is
    /// authored tuning data and cannot change while the game runs.</summary>
    private static float StopWallRate(ManagedObject state)
    {
        if (_thresholdRead) return float.IsNaN(_stopWallRate) ? MOVABLE_RATE_FALLBACK : _stopWallRate;
        _thresholdRead = true;

        var p = FieldProbeService.Member(state, "ConstMoveParams") as ManagedObject
                ?? FlowHelper.Call(state, "__GetConstMoveParam") as ManagedObject;
        float v = FieldProbeService.ToFloat(FieldProbeService.Member(p, "DashMove_StopWallRate", typeof(float)));
        if (v > 0f && v <= 1f) _stopWallRate = v;
        return float.IsNaN(_stopWallRate) ? MOVABLE_RATE_FALLBACK : _stopWallRate;
    }

    /// <summary><c>AvatarFieldParam_Volatile.CollisionInfo</c>, by the route the probe
    /// confirmed in game (the avatar's own <c>__GetVolatileParam()</c>), falling back
    /// to the property the field state publishes.</summary>
    private static ManagedObject CollisionInfo(ManagedObject avatar, ManagedObject state)
    {
        var vol = FlowHelper.Call(avatar, "__GetVolatileParam") as ManagedObject
                  ?? FieldProbeService.Member(state, "VolatileParam") as ManagedObject;
        return FieldProbeService.Member(vol, "Collision") as ManagedObject;
    }

    /// <summary>The capsule. The avatar's single <c>Components</c> object is the route
    /// confirmed in game; the collision manager on the field state is the documented
    /// alternate. <c>Components</c> is ONE object, never a collection — iterating it
    /// is a trap this codebase has already fallen into.</summary>
    private static ManagedObject CharaController(ManagedObject avatar, ManagedObject state)
    {
        var comps = FieldProbeService.Member(avatar, "Components") as ManagedObject;
        var cc = FieldProbeService.Member(comps, "CharacterController") as ManagedObject;
        if (cc != null) return cc;

        var acm = FieldProbeService.Member(state, "CollisionManager") as ManagedObject;
        return FieldProbeService.Member(acm, "CharaController") as ManagedObject;
    }

    // ---------- 3: will the game climb it for me? ----------

    /// <summary>True when any of the game's auto-step size classes has been filled
    /// this frame — an obstacle the avatar surmounts on its own.</summary>
    private static bool ReadSteppable(ManagedObject state)
    {
        if (StepTypeIds.Count == 0 || _setedFlagId < 0) return false;

        var cache = FieldProbeService.Member(state, "CollisionCache") as ManagedObject;
        if (cache == null) return false;

        // When the game is not running the step check, whatever the cache holds is
        // from an earlier frame and must not be read as this frame's answer.
        if (Truth(FieldProbeService.Member(cache, "DoesGoupStepCheck", typeof(bool))) == false) return false;

        var getInfo = Resolve(GetGoupStepByCache, cache, "GetGoupStepInfo", 1, "GoupStepTypes");
        if (getInfo == null) return false;
        _goupStepOk = true;

        foreach (int typeId in StepTypeIds)
        {
            ManagedObject info = null;
            try { info = getInfo.InvokeBoxed(null, cache, new object[] { typeId }) as ManagedObject; }
            catch { }
            if (info == null) continue;

            var getFlag = Resolve(GetFlagByInfo, info, "GetFlag", 1, "CheckFlagType");
            if (getFlag == null) return false;
            try
            {
                if (Truth(getFlag.InvokeBoxed(typeof(bool), info, new object[] { _setedFlagId })) == true)
                    return true;
            }
            catch { }
        }
        return false;
    }

    // ---------- 4: is it a wall I can run along? ----------

    /// <summary>Whether any wall the avatar is touching is one it can run along.
    /// The list is caller-allocated and generic, which is exactly what the probe
    /// could not construct — so a false here means "not claimed", never "checked and
    /// no".</summary>
    private static bool CanWallRide(ManagedObject avatar)
    {
        // NOT read, on purpose. GetContactedWallInfos takes a caller-allocated
        // IList<ContactedWallInfo> — a generic INTERFACE. REFramework's CreateInstance
        // has no guard: it hands the game's Activator that type and wraps whatever comes
        // back. For an interface that is a wrapper over a non-object, and it crashed the
        // game (AccessViolationException in ManagedObject.Finalize, 2026-09-06). That
        // finalizer fault is the SAME one traced on 2026-09-09 — see
        // FieldProbeService.NewInstance: what CreateInstance returns is a per-thread
        // LOCAL object the engine reclaims, never an AddRef'd one. Until a constructible
        // list type is found, wall-ride is never
        // CLAIMED, exactly as the header documents for an unbound route.
        _wallRideTried = true;
        return false;
    }

    // ---------- shared plumbing ----------

    /// <summary>A method on the owner's own type chain, picked by SHAPE and cached by
    /// the owner's type name — the same per-concrete-type caching the ray sweep uses,
    /// for the same reason.</summary>
    private static Method Resolve(Dictionary<string, Method> cache, ManagedObject owner,
                                  string name, int paramCount, string firstParamSuffix)
    {
        var td = owner?.GetTypeDefinition();
        string typeName = td?.GetFullName();
        if (typeName == null) return null;
        if (cache.TryGetValue(typeName, out var cached)) return cached;

        var found = FieldProbeService.FindByShape(td, name, paramCount, firstParamSuffix);
        cache[typeName] = found;
        return found;
    }

    /// <summary>A boxed engine boolean as a tri-state: true, false, or "not read".
    /// The difference matters everywhere here — an unread flag may never be spoken as
    /// a negative answer.</summary>
    private static bool? Truth(object boxed)
    {
        if (boxed == null) return null;
        try { return Convert.ToBoolean(boxed); }
        catch { return null; }
    }

    /// <summary>Both enums by NAME out of the TDB, once for the process. A name the
    /// game does not publish is simply not queried; nothing here guesses an ordinal.</summary>
    private static void ReadEnumsOnce()
    {
        if (_enumsRead) return;
        _enumsRead = true;

        foreach (var (name, value) in FieldProbeService.ReadEnum(GOUP_STEP_ENUM, byteWidth: false))
            if (name != GOUP_STEP_SENTINEL) StepTypeIds.Add(value);
        _setedFlagId = FieldProbeService.EnumValue(CHECK_FLAG_ENUM, SETED_FLAG);
    }

    /// <summary>One line, once, naming which verdict routes answered. This is what an
    /// in-game test reads to know whether the fix is running on the game's own data
    /// or on the ray fallback.</summary>
    private static void LogRoutesOnce()
    {
        if (_routesLogged) return;
        _routesLogged = true;

        string rate = float.IsNaN(_stopWallRate)
            ? FormattableString.Invariant($"fallback {MOVABLE_RATE_FALLBACK:F2}")
            : FormattableString.Invariant($"{_stopWallRate:F3}");
        string msg = $"[SF6Access] NavRadar verdict routes: volatile={Ok(_volatileOk)}, " +
                     // The capsule is only consulted when the volatile route did NOT
                     // answer, so on a healthy session it reads "untried" — which is
                     // not a failure and must not look like one to whoever reads the
                     // log. Saying "missing" here cost a false alarm once.
                     $"charaCtrl={(_volatileOk && !_charaCtrlOk ? "untried" : Ok(_charaCtrlOk))}, " +
                     $"goupStep={Ok(_goupStepOk)}, " +
                     // Only reached on a frame where a wall is actually touched, and
                     // then deliberately not read (see CanWallRide): never "ok".
                     $"wallRide={(_wallRideTried ? "disabled" : "untried")}, stopWallRate={rate} " +
                     $"({StepTypeIds.Count} step classes, Seted={_setedFlagId})";
        if (_volatileOk || _charaCtrlOk) API.LogInfo(msg);
        else API.LogWarning(msg + " — NO game-owned verdict bound; the radar is back to classifying " +
                                  "by ray height alone and will report the old false positives.");
    }

    private static string Ok(bool bound) => bound ? "ok" : "missing";
}
