using System;
using System.Globalization;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// <b>Ground truth for "can I actually walk there".</b> Every other passability answer in
/// this mod is INFERRED from rays, and a ray cannot tell a plaza from a balcony with a
/// drop behind it. The engine already knows:
/// <c>via.navigation.NavigationSurface.queryPathSync</c> runs the game's own A* over the
/// city navmesh and reports whether a route exists, how long it is, and where it turns.
///
/// <para><b>Its intended consumer is the MISSION BEACON</b>, not the radar: one
/// destination, known in advance, and the question a bearing cannot answer — the objective
/// is 40 m north-east, but the route is round three sides of a block, so "straight-line
/// 40 m" is a lie the player walks into a wall believing. For that single point, on
/// demand: is there a walkable path, how long is it WALKED versus STRAIGHT, and where is
/// the first corner. <b>Nothing calls it yet</b> — see the kill switch.</para>
///
/// <para><b>The instance comes from the AVATAR, never from a map handle.</b> The previous
/// navmesh attempt (<see cref="NavMeshNodes"/>) went
/// <c>WTCommon.CityResource.CityAIMap.findMapHandle()</c> and took SF6 down; the prime
/// suspect is that <c>findMapHandle</c> was bound BY NAME with zero arguments while
/// <c>via.navigation.AIMap</c> declares three overloads of it (<c>AIMap.cs:177/180/189</c>).
/// Nothing here touches <c>AIMap</c>. The player's own avatar carries a fully configured
/// agent — <c>AvatarBase.Components</c> (<c>AvatarBase.cs:1101</c>) →
/// <c>AvatarComponent.Controll</c> (<c>AvatarComponent.cs:66</c>) →
/// <c>AvatarNaviControllBase.NaviSurface</c> (<c>AvatarNaviControllBase.cs:430</c>) — so
/// nothing has to guess which map or which section is live.</para>
///
/// <para><b>The crash rules, honoured.</b> The method is bound BY SHAPE (three parameters:
/// value, value, reference), the only thing separating
/// <c>queryPathSync(vec3, vec3, PathQueryReport)</c> (<c>NavigationSurface.cs:556</c>) from
/// its five siblings, and the name is matched EXACTLY because the six <c>queryPath</c>
/// overloads (<c>:538-553</c>) take a <c>MulticastDelegate</c> — the documented crash
/// family (<c>docs/sf6-architecture.md</c>). No parameter is by-ref, so no reference type
/// is written through; the two <c>vec3</c>s are unmanaged <see cref="FieldOutBuffer"/>s;
/// the <c>PathQueryReport</c> is allocated per call through
/// <see cref="FieldProbeService.NewInstance"/> and let go, as <see cref="FieldRayCaster"/>
/// treats its <c>CastRayResult</c>. <c>Navigation.adjust()</c> / <c>start()</c> /
/// <c>stop()</c> (<c>Navigation.cs:1321-1330/1456/1459</c>) drive the live avatar and are
/// never called; neither is any unbounded node query. This class only ASKS.</para>
///
/// <para>Every engine call writes a numbered <c>NavPath step</c> line BEFORE it runs, so a
/// native fault — which walks straight through every <c>try/catch</c> — still names the
/// call that did not return. That trace is the only reason the previous crash was
/// diagnosable.</para>
/// </summary>
public static class FieldNavPath
{
    /// <summary><b>OFF, and it has no callers.</b> Nothing in this file has ever run in
    /// game and the previous navmesh route crashed SF6 on its first press (2026-09-08), so
    /// it is flipped to <c>true</c> only for a deliberate, supervised test — the USER'S
    /// decision. A field rather than a <c>const</c> so the code behind it still compiles as
    /// live code: a kill switch that rots what it guards is one nobody can re-enable.</summary>
    private static readonly bool PATHFINDER_ENABLED = false;

    private const string SURFACE_TYPE = "via.navigation.NavigationSurface";
    private const string DISTANCE_ENUM = "via.navigation.map.PathInfo.DistanceType";

    /// <summary>Matched EXACTLY. <c>queryPath</c> (no <c>Sync</c>) is the async family and
    /// carries a delegate parameter — see the class remarks.</summary>
    private const string QUERY_PATH_SYNC = "queryPathSync";

    /// <summary>Shortest gap between two synchronous path queries, in milliseconds.
    /// <c>queryPathSync</c> is a BLOCKING A* across a city and must never run at sensing
    /// cadence, so the floor is the beacon's own fastest ping gap (<see cref="HomingCue"/>'s
    /// private <c>GAP_NEAR_MS</c>, a quarter of a second — the shortest silence that still
    /// reads as two pings; restated here because it cannot be reached). One answer per
    /// beacon ping however often a caller polls, charged for the whole ATTEMPT so a query that
    /// faults or cannot resolve its surface is not retried at frame rate.</summary>
    public const long MinQueryIntervalMs = 250;

    /// <summary>What the pathfinder said. <c>Ok == false</c> means "could not answer" and
    /// a caller must NEVER read that as walkable.</summary>
    public readonly struct PathAnswer
    {
        /// <summary><see cref="Ok"/>: the query ran and was read. <see cref="Exists"/>:
        /// the engine found a route (<c>PathQueryReport.Exist</c>), meaningful only when
        /// <see cref="Ok"/>. <see cref="Throttled"/>: refused by the rate limiter, not by
        /// the engine — ask again later, keeping the previous answer.</summary>
        public readonly bool Ok, Exists, Throttled;

        /// <summary>Metres actually WALKED along the route (<c>DistanceType.Path</c>).</summary>
        public readonly float PathM;

        /// <summary>The route's STRAIGHT-line length (<c>DistanceType.Raw</c>), or 0 when
        /// the enum could not be read. <see cref="DetourRatio"/> divides the two: one
        /// number for "how far out of my way does this destination actually take me".</summary>
        public readonly float StraightM;

        /// <summary>Corners in the route (<c>PathInfo.PathPointCount</c>).</summary>
        public readonly int Points;

        /// <summary>Where the route first turns — what a beacon should point AT while the
        /// destination itself is round a corner. <see cref="HasCorner"/> says the position
        /// was readable at all.</summary>
        public readonly bool HasCorner;
        public readonly float CornerX, CornerY, CornerZ;

        public PathAnswer(bool ok, bool exists, bool throttled, float pathM, float straightM,
                          int points, bool hasCorner, float cx, float cy, float cz)
        {
            Ok = ok; Exists = exists; Throttled = throttled;
            PathM = pathM; StraightM = straightM; Points = points;
            HasCorner = hasCorner; CornerX = cx; CornerY = cy; CornerZ = cz;
        }

        /// <summary>The detour ratio, or 0 when the straight-line length is unknown.</summary>
        public float DetourRatio => StraightM > 0f ? PathM / StraightM : 0f;
    }

    // ---- the only survivors between calls: TDB metadata and unmanaged memory ----
    private static Method _queryPathSync, _hasValidMap, _calcDistance, _calcDistanceTyped, _getPathPointInfo;
    private static TypeDefinition _vecType, _reportType;
    private static FieldOutBuffer _start, _end;
    private static int _rawId = -1, _pathId = -1;
    private static long _nextQueryAt;
    private static bool _distanceIdsTried, _bindTried, _bound, _tracing = true, _failLogged, _summaryLogged;

    /// <summary>Switch on, the avatar's surface resolved, and the engine itself says this
    /// place has a navmesh. It touches engine objects, so it is <c>false</c> immediately
    /// when the switch is off; call it when arming a beacon, not every frame.</summary>
    public static bool Available => PATHFINDER_ENABLED && ReadySurface() != null;

    /// <summary>One line for the arming log: what bound, or why nothing did.</summary>
    public static string RouteDescription { get; private set; } = "not probed yet";

    /// <summary>Is there a walkable route from the avatar to this world point, how long is
    /// it, and where does it first turn? Never throws.</summary>
    public static PathAnswer TryReach(float x, float y, float z)
    {
        if (!PATHFINDER_ENABLED) return default;
        long now = Environment.TickCount64;
        if (now < _nextQueryAt) return new PathAnswer(false, false, true, 0f, 0f, 0, false, 0f, 0f, 0f);
        // Charged for the whole ATTEMPT, before anything is resolved: a query that faults,
        // hangs or cannot resolve its surface must not be retried at frame rate either.
        _nextQueryAt = now + MinQueryIntervalMs;
        try
        {
            var surface = ReadySurface();
            if (surface == null) return default;

            var me = AvatarFieldReader.ReadPlayerPos(null);
            if (!me.ok) { Fail("the avatar's own position is unreadable"); return default; }
            if (!Write(_start, me.x, me.y, me.z) || !Write(_end, x, y, z))
            { Fail("could not write the query endpoints"); return default; }

            Step(5, $"allocating a {_reportType?.FullName}");
            var report = FieldProbeService.NewInstance(_reportType);
            if (report == null) { Fail($"{_reportType?.FullName} is not constructible"); return default; }

            Step(6, string.Format(CultureInfo.InvariantCulture,
                "calling {0}(vec3, vec3, report) from ({1:F2}, {2:F2}, {3:F2}) to ({4:F2}, {5:F2}, {6:F2}) " +
                "- the first call in this route that has never run in game",
                QUERY_PATH_SYNC, me.x, me.y, me.z, x, y, z));
            var ran = _queryPathSync.InvokeBoxed(typeof(bool), surface,
                                                 new object[] { _start.View, _end.View, report });

            Step(7, "reading PathQueryReport.Exist / PathInfo");
            bool exists = FieldProbeService.Member(report, "Exist", typeof(bool)) is bool e && e;
            var info = FieldProbeService.Member(report, "PathInfo") as ManagedObject;

            float pathM = 0f, rawM = 0f, cx = 0f, cy = 0f, cz = 0f;
            int points = 0;
            bool hasCorner = false;
            if (info != null)
            {
                Step(8, "reading PathInfo.calcDistance / PathPointCount / getPathPointInfo(0)");
                EnsureDistanceIds();
                pathM = Distance(info, _pathId, orUntyped: true);
                rawM = Distance(info, _rawId, orUntyped: false);
                var n = FieldProbeService.Member(info, "PathPointCount", typeof(uint));
                points = n == null ? 0 : (int)Convert.ToUInt32(n);
                hasCorner = FirstCorner(info, points, out cx, out cy, out cz);
            }

            Step(9, string.Format(CultureInfo.InvariantCulture,
                "{0} returned {1}; Exist={2}; points={3}; first corner {4} vs avatar ({5:F2}, {6:F2}, {7:F2}) " +
                "- compare the two to settle whether path point 0 is the start or the first turn",
                QUERY_PATH_SYNC, ran, exists, points,
                hasCorner ? string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2}, {2:F2})", cx, cy, cz)
                          : "unreadable",
                me.x, me.y, me.z));
            LogRouteOnce(surface);
            _tracing = false;
            return new PathAnswer(true, exists, false, pathM, rawM, points, hasCorner, cx, cy, cz);
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    /// <summary>One line describing an answer, so every consumer words it the same way.</summary>
    public static string Describe(in PathAnswer a)
    {
        if (a.Throttled) return "not asked yet (rate limited)";
        if (!a.Ok) return "no answer";
        if (!a.Exists) return "NO WALKABLE PATH";
        return string.Format(CultureInfo.InvariantCulture,
            "path {0:F2} m walked / {1:F2} m straight (detour x{2:F2}), {3} points",
            a.PathM, a.StraightM, a.DetourRatio, a.Points);
    }

    /// <summary>Drop everything derived from the current city. The unmanaged endpoint
    /// buffers stay — unmanaged memory does not move, and nothing about a city load
    /// invalidates them.</summary>
    public static void Reset()
    {
        _queryPathSync = _hasValidMap = _calcDistance = _calcDistanceTyped = _getPathPointInfo = null;
        _vecType = _reportType = null;
        _bindTried = _bound = _failLogged = _summaryLogged = false;
        _tracing = true;
        _nextQueryAt = 0;
        RouteDescription = "not probed yet";
    }

    // ---------- resolving the surface (steps 1-4, all zero-risk reads) ----------

    /// <summary>The avatar's own navmesh agent, bound and confirmed live, or null with a
    /// reason. The only engine CALL here is <c>hasValidMap()</c>: no arguments, returns a
    /// bool.</summary>
    private static ManagedObject ReadySurface()
    {
        try
        {
            Step(1, "avatar -> Components -> Controll");
            var avatar = FieldRayCaster.PlayerAvatar();
            var comps = FieldProbeService.Member(avatar, "Components") as ManagedObject;
            var controll = FieldProbeService.Member(comps, "Controll") as ManagedObject;
            if (controll == null) { Fail("AvatarComponent.Controll null (not in the field?)"); return null; }

            Step(2, "reading AvatarNaviControllBase.IsDontUseAIMap");
            if (FieldProbeService.Member(controll, "IsDontUseAIMap", typeof(bool)) is bool skip && skip)
            { Fail("the avatar's controller sets IsDontUseAIMap: it is not on the AI map"); return null; }

            Step(3, "reading AvatarNaviControllBase.NaviSurface");
            var surface = FieldProbeService.Member(controll, "NaviSurface") as ManagedObject
                          ?? ComponentSurface(avatar);
            if (surface == null) { Fail($"no {SURFACE_TYPE} on the player avatar"); return null; }
            if (!Bind(surface)) return null;

            Step(4, "calling via.navigation.Navigation.hasValidMap()");
            if (!(_hasValidMap.InvokeBoxed(typeof(bool), surface, null) is bool valid) || !valid)
            {
                // The engine saying there is no navmesh here. No further probing helps.
                Fail("hasValidMap() is false: this place has no navmesh loaded");
                return null;
            }
            return surface;
        }
        catch (Exception ex) { Fail($"{ex.GetType().Name}: {ex.Message}"); return null; }
    }

    /// <summary>Fallback when the controller publishes no <c>NaviSurface</c>:
    /// <c>via.GameObject.getComponent(System.Type)</c> (<c>GameObject.cs:194</c>) — its
    /// only overload, one reference parameter, nothing by-ref.</summary>
    private static ManagedObject ComponentSurface(ManagedObject avatar)
    {
        var go = FlowHelper.Call(avatar, "get_GameObject") as ManagedObject;
        var runtimeType = TDB.Get()?.FindType(SURFACE_TYPE)?.GetRuntimeType();
        if (go == null || runtimeType == null) return null;
        Step(3, $"NaviSurface null; via.GameObject.getComponent({SURFACE_TYPE})");
        return FlowHelper.Call(go, "getComponent", runtimeType) as ManagedObject;
    }

    // ---------- binding, by shape only ----------

    private static bool Bind(ManagedObject surface)
    {
        if (_bindTried) return _bound;
        _bindTried = true;
        var td = surface.GetTypeDefinition();

        _hasValidMap = Find(td, "hasValidMap", m => ParamCount(m) == 0);
        // Three parameters, value / value / reference: the ONLY overload of the six that
        // takes just the two endpoints and a report (NavigationSurface.cs:556). The four-
        // and six-parameter ones add a PathQueryInfo, an interrupt distance or node
        // priorities, and none of them is what this class means.
        _queryPathSync = Find(td, QUERY_PATH_SYNC, m =>
        {
            var ps = m.GetParameters();
            return ps != null && ps.Count == 3
                   && ps[0].Type?.IsValueType() == true && ps[1].Type?.IsValueType() == true
                   && ps[2].Type != null && !ps[2].Type.IsValueType();
        });

        if (_queryPathSync == null || _hasValidMap == null)
        {
            Fail($"{td?.GetFullName()} publishes no {QUERY_PATH_SYNC}(vec3, vec3, report) / hasValidMap()");
            return false;
        }

        var pars = _queryPathSync.GetParameters();
        _vecType = pars[0].Type;
        _reportType = pars[2].Type;
        _start ??= FieldOutBuffer.Acquire(_vecType);
        _end ??= FieldOutBuffer.Acquire(_vecType);
        if (_start == null || _end == null) { Fail(FieldOutBuffer.Refusal(_vecType)); return false; }

        _bound = true;
        return true;
    }

    /// <summary>A method by name plus an arbitrary SHAPE test, walking up from the concrete
    /// type to wherever it is declared — <c>hasValidMap</c> lives on
    /// <c>via.navigation.Navigation</c>, the surface's own parent.</summary>
    private static Method Find(TypeDefinition start, string name, Func<Method, bool> shape)
    {
        for (var td = start; td != null; td = td.ParentType)
        {
            var methods = td.GetMethods();
            if (methods == null) continue;
            foreach (var m in methods)
            {
                if (m.Name != name) continue;
                try { if (shape(m)) return m; } catch { }
            }
        }
        return null;
    }

    private static int ParamCount(Method m) => m.GetParameters()?.Count ?? -1;

    // ---------- reading the answer ----------

    /// <summary><c>DistanceType.Raw</c> / <c>.Path</c> straight out of the TDB, once —
    /// never the literals 0 and 1.</summary>
    private static void EnsureDistanceIds()
    {
        if (_distanceIdsTried) return;
        _distanceIdsTried = true;
        _rawId = FieldProbeService.EnumValue(DISTANCE_ENUM, "Raw");
        _pathId = FieldProbeService.EnumValue(DISTANCE_ENUM, "Path");
    }

    /// <summary>One <c>PathInfo.calcDistance</c> reading in metres.
    /// <paramref name="orUntyped"/> allows the no-argument overload (<c>PathInfo.cs:289</c>)
    /// as a fallback — right for the walked length, wrong for the straight-line one, which
    /// returns 0 rather than a number that is not what it claims to be.</summary>
    private static float Distance(ManagedObject info, int distanceId, bool orUntyped)
    {
        var td = info.GetTypeDefinition();
        _calcDistanceTyped ??= Find(td, "calcDistance",
            m => ParamCount(m) == 1 && m.GetParameters()[0].Type?.IsValueType() == true);
        _calcDistance ??= Find(td, "calcDistance", m => ParamCount(m) == 0);

        if (distanceId >= 0 && _calcDistanceTyped != null)
            return FieldProbeService.ToFloat(
                _calcDistanceTyped.InvokeBoxed(typeof(float), info, new object[] { distanceId }));
        if (!orUntyped || _calcDistance == null) return 0f;
        return FieldProbeService.ToFloat(_calcDistance.InvokeBoxed(typeof(float), info, null));
    }

    /// <summary>Where the route first turns: path point 0, through
    /// <c>PathInfo.getPathPointInfo(uint)</c> (<c>PathInfo.cs:274</c>) — the accessor
    /// <c>PathPointCount</c> itself bounds, preferred over the portal accessors, whose index
    /// domain is the LINK count. <c>PathPointInfo.PortalPos</c> is a <c>via.vec3</c> returned
    /// BY VALUE, so no caller buffer is involved.</summary>
    private static bool FirstCorner(ManagedObject info, int points, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (points < 1) return false;
        _getPathPointInfo ??= Find(info.GetTypeDefinition(), "getPathPointInfo",
            m => ParamCount(m) == 1 && m.GetParameters()[0].Type?.IsValueType() == true);
        if (_getPathPointInfo == null) return false;

        // Index 0: the first element the engine published, not a tuned constant.
        var point = _getPathPointInfo.InvokeBoxed(typeof(object), info, new object[] { 0u }) as ManagedObject;
        var pos = point == null ? null : FieldProbeService.Member(point, "PortalPos");
        if (pos == null) return false;
        x = FlowHelper.ReadVecComponent(pos, "x");
        y = FlowHelper.ReadVecComponent(pos, "y");
        z = FlowHelper.ReadVecComponent(pos, "z");
        return true;
    }

    private static bool Write(FieldOutBuffer buf, float x, float y, float z)
    {
        if (!buf.SetComponent("x", x) || !buf.SetComponent("y", y) || !buf.SetComponent("z", z)) return false;
        // Read back through the same field metadata the engine uses: evidence the value
        // landed where native code looks for it, not a half-filled struct.
        return buf.Component("x") == x && buf.Component("y") == y && buf.Component("z") == z;
    }

    // ---------- trace and log ----------

    /// <summary>Say what is about to be called, BEFORE calling it. Tracing stops after the
    /// first query that reaches its summary: a crash hunt needs the first run spelled out, a
    /// working pathfinder must not write nine lines a query.</summary>
    private static void Step(int number, string what)
    {
        if (_tracing) API.LogInfo($"[SF6Access] NavPath step {number}: {what}");
    }

    private static void Fail(string reason)
    {
        RouteDescription = "unavailable: " + reason;
        if (_failLogged) return;
        _failLogged = true;
        API.LogWarning("[SF6Access] NavPath " + RouteDescription);
    }

    /// <summary>One line, once: the agent the query actually ran on, so a session in play
    /// can say whether the answers belong to the player's own body size.</summary>
    private static void LogRouteOnce(ManagedObject surface)
    {
        if (_summaryLogged) return;
        _summaryLogged = true;
        RouteDescription = string.Format(CultureInfo.InvariantCulture,
            "{0}.{1}(vec3, vec3, {2}) on the player avatar's own NaviSurface; map='{3}' agent radius={4:F3} m " +
            "height={5:F3} m; at most one query per {6} ms",
            SURFACE_TYPE, QUERY_PATH_SYNC, _reportType?.FullName ?? "?",
            FieldProbeService.Member(surface, "MapName") as string ?? "?",
            FieldProbeService.ToFloat(FieldProbeService.Member(surface, "AgentRad", typeof(float))),
            FieldProbeService.ToFloat(FieldProbeService.Member(surface, "AgentHeight", typeof(float))),
            MinQueryIntervalMs);
        API.LogInfo("[SF6Access] NavPath route: " + RouteDescription);
    }
}
