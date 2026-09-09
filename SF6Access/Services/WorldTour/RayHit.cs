using REFrameworkNET;

namespace SF6Access.Services.WorldTour;

/// <summary>What one contact along a ray turned out to be. Only <see cref="Wall"/>
/// — and <see cref="Unknown"/>, which means the walkable cut could not be derived —
/// shapes the street; <see cref="Ground"/> and <see cref="Ceiling"/> never block.</summary>
public enum HitClass { None, Ground, Ceiling, Step, Wall, Unknown }

/// <summary>One direction's answer. Two distances, as in the RE7 mod: the nearest
/// thing of any kind, and the nearest thing that actually defines the street.</summary>
public readonly struct RayHit
{
    /// <summary>A blocking contact was found (ground and ceiling never count).</summary>
    public readonly bool Hit;
    /// <summary>Metres to the nearest blocking contact of ANY kind — kerbs and props
    /// included. 0 when nothing blocking was met within the reach.</summary>
    public readonly float Distance;
    /// <summary>Metres to the first surface that defines the street: a wall standing
    /// on a level collision layer. 0 when none was met within the reach.</summary>
    public readonly float Architectural;
    public readonly HitClass Class;
    /// <summary>The blocking contact's surface normal Y, for diagnosis.</summary>
    public readonly float NormalY;
    /// <summary>The blocking contact's surface normal on the ground plane, NOT
    /// normalized. A beam may only claim a surface it is looking at close to face-on,
    /// and that test is the angle between the beam and this — see
    /// <c>FieldRadarTuning.FaceOnCos</c>.</summary>
    public readonly float NormalX, NormalZ;
    /// <summary>The blocking contact's GameObject address, 0 when the contact is
    /// static level geometry (which carries no GameObject) or the read failed. Left
    /// for another layer to turn into a name.</summary>
    public readonly ulong ObjectAddress;

    public RayHit(float blocking, float architectural, HitClass cls,
                  float normalX, float normalY, float normalZ, ulong obj)
    {
        Hit = blocking > 0f;
        Distance = blocking;
        Architectural = architectural;
        Class = cls;
        NormalX = normalX;
        NormalY = normalY;
        NormalZ = normalZ;
        ObjectAddress = obj;
    }
}

/// <summary>The radar's three sensing directions, as the RE7 mod aims them:
/// left, straight ahead and right of the CAMERA (the frame World Tour movement is
/// expressed in). Three, not four — a beam behind the player answers a question
/// nobody walking forward is asking, and RE7 ships three.</summary>
public enum RadarBeam { Left, Front, Right }
