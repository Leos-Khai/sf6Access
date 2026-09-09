using System;
using System.Collections.Generic;
using REFrameworkNET;
using SF6Access.Services;

namespace SF6Access.Services.WorldTour;

/// <summary>
/// The city's NAVMESH read as a set of doorways: where the walkable surface the
/// avatar is standing on actually continues, and how wide each of those gaps is.
/// The ENGINE calls all live in <see cref="NavMeshNodes"/>; this file owns the cache,
/// the edge geometry and the one summary line.
///
/// <para><b>Why not another ray sweep.</b> A ray answers "is there geometry along this
/// segment"; it cannot answer "is there a way through" - a 24-ray fan misses a 1.6 m
/// gap at 6 m entirely, and a wall met at a grazing angle reads exactly like an
/// opening. The navmesh already holds the answer: a node is a walkable polygon, and
/// every edge it shares with a linked polygon IS a way through, with an exact width, at
/// any distance.</para>
///
/// <para><b>DISABLED AND UNPROVEN (2026-09-08).</b> The first in-game press took SF6
/// down somewhere inside <see cref="NavMeshNodes"/> and wrote nothing at all, so
/// <c>FieldNavRadarHooks.MESH_WAYS_ENABLED</c> is <c>false</c> and only the user
/// decides when it goes back on. What the rewrite changed is that the next attempt
/// cannot be silent: every engine call now announces itself first, so the last
/// <c>NavMesh step</c> line in the log names the call that killed the game. Nothing
/// here is simulated to cover the gap - a step that cannot be made safely is skipped
/// and said out loud.</para>
///
/// <para><b>Openings come per LINKED NEIGHBOUR</b>, not from a sweep:
/// <c>NodeInfo.queryLinkToNodes()</c> lists the polygons this one connects to, and the
/// edge shared with each is the pair of vertices both hold in common
/// (<c>getGlobalVertex</c>, world space); that segment's length is the gap's real
/// width. A neighbour flagged <c>Wall</c> is a wall node, so its edge is NOT passable
/// however wide it is.</para>
///
/// <para><b>Nothing engine-owned is cached.</b> The map handle and every
/// <c>NodeInfo</c> are re-resolved per query, because city streaming can retire them
/// and a stale binding is the failure mode the house rules warn about. The RESULT is
/// cached as plain floats and recomputed only once the player has moved far enough for
/// it to be able to change, which is what makes this safe to poll a couple of times a
/// second.</para>
///
/// <para><b>Not bound (documented, not simulated):</b> <c>NodeInfo.queryLinks()</c>
/// yields <c>LinkInfo</c>, which carries no edge geometry at all (only
/// <c>queryFromNode</c>/<c>queryToNode</c>), so it cannot give a width and is unused.
/// <c>queryClosestLinkBoundaryEdge(vec3)</c> returns ONE <c>via.LineSegment</c> for the
/// whole node, so it cannot enumerate the exits either; it is left for a future
/// cross-check of the widths computed here. The <c>queryClosestNode(vec3,
/// NodeQueryInfo)</c> overload, which could bound the query to a region, is NOT used:
/// it needs a <c>NodeQueryInfo</c> built through the TDB and a <c>setRegion</c> shape
/// argument, i.e. two more never-run calls in the very route being crash-hunted.</para>
/// </summary>
public static class NavMeshOpenings
{
    /// <summary>Two vertices are THE SAME vertex below this squared distance. Not a game
    /// value and not a tuning knob: adjacent polygons store the shared corner once each
    /// and world-space float arithmetic does not guarantee bit-identical copies. Same
    /// degeneracy guard as <c>FieldNavSideRays.MIN_SEGMENT_SQR_LEN</c> - 1e-6 m^2 is a
    /// millimetre, far below any real navmesh feature and far above float noise.</summary>
    private const float SAME_VERTEX_SQR_M = 1e-6f;

    /// <summary>One way out of the polygon the avatar is standing on: the midpoint of
    /// the edge shared with a linked polygon (world X/Z, so the caller derives bearing
    /// the same way it does for any other field target), that edge's true width, its
    /// distance from the player, and whether the avatar's own collision capsule fits
    /// through it.</summary>
    public readonly struct MeshOpening
    {
        public readonly float X, Z;
        public readonly float WidthM;
        public readonly float DistanceM;
        public readonly bool Passable;

        public MeshOpening(float x, float z, float widthM, float distanceM, bool passable)
        {
            X = x; Z = z; WidthM = widthM; DistanceM = distanceM; Passable = passable;
        }
    }

    /// <summary>The handle bound AND a node query against it returned real geometry.
    /// False until the first successful query - never assumed.</summary>
    public static bool Available => _available;

    /// <summary>What bound and what did not, in one line, for the log and for a
    /// diagnostic readout.</summary>
    public static string RouteDescription => _route ?? NavMeshNodes.Route;

    // ---- state: results are plain floats; nothing engine-owned lives here ----
    private static readonly List<MeshOpening> Cache = new();
    private static bool _cacheValid;
    private static float _cacheX, _cacheY, _cacheZ;
    private static float _refreshSqrM;
    private static bool _available;
    private static bool _refused;
    private static string _route;

    /// <summary>Every way out of the polygon under the player, nearest first. Returns
    /// an empty list - never null, never throws - when the navmesh cannot be reached;
    /// an empty list means NO INFORMATION and must never be spoken as "no way out".
    /// The returned list is this class's own cache: read it, do not mutate it.</summary>
    public static List<MeshOpening> FromPlayer()
    {
        try
        {
            // A navmesh that did not answer is not asked again until the city changes.
            // Retrying costs a full engine query per sample for nothing, and the one
            // thing worse than an unavailable navmesh is polling an unavailable navmesh
            // several times a second in continuous mode.
            if (_refused) return Cache;

            var player = AvatarFieldReader.ReadPlayerPos(WorldTourStateService.GetAvatarManager());
            if (!player.ok) return Cache;
            if (_cacheValid && MovedSqr(player.x, player.y, player.z) < _refreshSqrM) return Cache;

            Cache.Clear();
            _cacheValid = true;
            _cacheX = player.x; _cacheY = player.y; _cacheZ = player.z;
            Rebuild(player.x, player.y, player.z);
            Cache.Sort((a, b) => a.DistanceM.CompareTo(b.DistanceM));
            return Cache;
        }
        catch (Exception ex)
        {
            // Only a MANAGED failure lands here. A native one does not throw at all,
            // which is precisely why every engine call logs before it runs.
            _refused = true;
            NavMeshNodes.Fail($"{ex.GetType().Name}: {ex.Message}");
            return Cache;
        }
    }

    /// <summary>Drop everything derived from the current city. Call on a city load or
    /// when leaving the field: the next query rebinds from scratch and traces its own
    /// steps again, so each city says for itself whether its navmesh answered.</summary>
    public static void Reset()
    {
        Cache.Clear();
        _cacheValid = false;
        _refreshSqrM = 0f;
        _available = false;
        _refused = false;
        _route = null;
        NavMeshNodes.Reset();
    }

    // ---------- one rebuild, step by traced step ----------

    private static void Rebuild(float px, float py, float pz)
    {
        var node = NavMeshNodes.ClosestNode(NavMeshNodes.FindHandle(), px, py, pz);   // steps 1-2
        if (node == null) { _refused = true; return; }

        var mine = NavMeshNodes.GlobalVertices(node, 3, "the player's node");          // step 3
        if (mine.Count < 2)
        {
            _refused = true;
            NavMeshNodes.Fail("the node under the player published no usable vertices");
            return;
        }

        // Step 4 before the links: it is what decides both "does the avatar fit" and
        // how far the player may walk before this answer is worth recomputing.
        NavMeshNodes.Step(4, "reading the avatar's collision capsule (FieldRayCaster.CapsuleRadius)");
        float radius = FieldRayCaster.CapsuleRadius();
        float diameter = radius * 2f;
        _refreshSqrM = RefreshDistanceSqr(radius, mine, px, py, pz);
        if (_refreshSqrM <= 0f)
        {
            // No body size and a degenerate polygon: nothing says when this answer goes
            // stale, so it would be re-queried every single sample. Answer once, then
            // stop, rather than poll the engine without a rate at all.
            _refused = true;
        }

        var links = NavMeshNodes.LinkedNodes(node, out int reported);                  // steps 5-6
        int resolved = 0;
        for (int i = 0; i < links.Count; i++)
        {
            var other = links[i];
            var theirs = NavMeshNodes.GlobalVertices(other, 6, $"neighbour {i} of {links.Count}");
            if (!SharedEdge(mine, theirs, out var a, out var b)) continue;
            resolved++;

            float width = Distance(a.x, a.y, a.z, b.x, b.y, b.z);
            float mx = (a.x + b.x) * 0.5f, my = (a.y + b.y) * 0.5f, mz = (a.z + b.z) * 0.5f;
            // A navmesh WALL node is not somewhere to walk, however wide the edge is,
            // and an unknown capsule may never be reported as "it fits".
            bool fits = diameter > 0f && width >= diameter && !NavMeshNodes.IsWall(other);
            Cache.Add(new MeshOpening(mx, mz, width, Distance(px, py, pz, mx, my, mz), fits));
        }

        _available = Cache.Count > 0;
        Summarize(node, mine.Count, px, py, pz, reported, resolved, diameter);
    }

    /// <summary>How far the player must move before the answer is worth recomputing,
    /// squared. One body radius is the shortest move that can change which polygon the
    /// avatar stands on; when the capsule is unreadable the polygon's own geometry
    /// stands in - its nearest corner is a distance the player cannot cross while
    /// certainly still standing on it. Zero only when both are unknown.</summary>
    private static float RefreshDistanceSqr(float radius, List<(float x, float y, float z)> node,
                                            float px, float py, float pz)
    {
        if (radius > 0f) return radius * radius;
        float nearest = 0f;
        foreach (var v in node)
        {
            float dx = v.x - px, dy = v.y - py, dz = v.z - pz;
            float d = dx * dx + dy * dy + dz * dz;
            if (nearest == 0f || d < nearest) nearest = d;
        }
        return nearest;
    }

    /// <summary>The two vertices two polygons have in common - the edge between them.
    /// False when they share no edge (a link the engine expresses some other way), and
    /// the neighbour is then skipped rather than given an invented width.</summary>
    private static bool SharedEdge(List<(float x, float y, float z)> mine,
                                   List<(float x, float y, float z)> other,
                                   out (float x, float y, float z) a,
                                   out (float x, float y, float z) b)
    {
        a = default; b = default;
        int found = 0;
        foreach (var v in mine)
        {
            bool shared = false;
            foreach (var w in other)
            {
                float dx = v.x - w.x, dy = v.y - w.y, dz = v.z - w.z;
                if (dx * dx + dy * dy + dz * dz <= SAME_VERTEX_SQR_M) { shared = true; break; }
            }
            if (!shared) continue;
            if (found == 0) a = v;
            else if (found == 1) b = v;
            else return false;   // more than an edge in common: not a shape we can measure
            found++;
        }
        return found == 2;
    }

    // ---------- plumbing ----------

    private static float MovedSqr(float x, float y, float z)
    {
        float dx = x - _cacheX, dy = y - _cacheY, dz = z - _cacheZ;
        return dx * dx + dy * dy + dz * dz;
    }

    private static float Distance(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = ax - bx, dy = ay - by, dz = az - bz;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The line that says the whole route survived, printed once per city. It
    /// prints the node's own position NEXT TO the player position this mod already
    /// reads correctly, because a zeroed vec3 out of a new read path is a marshalling
    /// bug far more often than a real value (see <c>docs/sf6-screens.md</c>,
    /// "Value-type reads: a known trap") - and an exact origin here is called out as
    /// exactly that instead of being reported as data.</summary>
    private static void Summarize(ManagedObject node, int vertexCount,
                                  float px, float py, float pz, int links, int resolved, float diameter)
    {
        var n = NavMeshNodes.Pos(node);

        // Each formatted number goes through Invariant on its own: concatenating
        // interpolated strings would produce a plain string, and a comma decimal
        // separator in a log line is not a coordinate anyone can compare.
        string nodePos = FormattableString.Invariant($"({n.x:F2}, {n.y:F2}, {n.z:F2})");
        string playerPos = FormattableString.Invariant($"({px:F2}, {py:F2}, {pz:F2})");
        string capsule = FormattableString.Invariant($"{diameter:F3}");
        _route = "queryClosestNode(vec3) ok via WTCommon.CityResource.CityAIMap.findMapHandle(); " +
                 $"node {nodePos} vs player {playerPos}; " +
                 $"{vertexCount} vertices, {links} links, {resolved} shared edges, capsule {capsule} m";

        if (n.x == 0f && n.y == 0f && n.z == 0f)
            API.LogWarning("[SF6Access] NavMesh openings: " + _route +
                           " - node.Pos is EXACTLY the origin, which is the value-type marshalling bug " +
                           "rather than a node at (0,0,0); the widths come from getGlobalVertex and are " +
                           "the reads to trust or disprove.");
        else
            API.LogInfo("[SF6Access] NavMesh openings: " + _route);

        // Every step returned: the trace has done its job and goes quiet.
        NavMeshNodes.Complete();
    }
}
