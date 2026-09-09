namespace SF6Access.Services.WorldTour;

/// <summary>
/// Every number the reactive radar runs on, each with the line it was taken from.
///
/// <para><b>Provenance.</b> These are A Hero's Call's own values, reached through the
/// RE7 accessibility mod's faithful port of that radar
/// (<c>D:\code\re engine\Re7Access\src\RadarService.cs</c>, HEAD = commit
/// <c>0d545e4</c>). Citations name the RE7 line, and RE7's own comments name the AHC
/// line behind it. Nothing here is tuned by ear in this repo.</para>
///
/// <para><b>Two AHC premises this game does not meet.</b> Each was measured in play
/// before it was replaced, and each carries its evidence on the constant itself. The
/// reach is BOUNDED, where AHC's is 1000 tiles (<see cref="ReachM"/>). And the
/// open/close channel runs on a passability THRESHOLD, where AHC has none at all
/// (<see cref="PassableEnterRadii"/>). Both come from the same root: AHC's heading is
/// discrete and its world is tiles; World Tour's camera is continuous and its world is
/// a city. Everything else below is still AHC's own number.</para>
///
/// <para>Time-to-contact buckets, cell dedup and a FAR state are still deliberately
/// absent: RE7 shipped all three and then DELETED them at v13/v14, its header
/// recording that they "were our inventions stacked on top of the AHC design, each
/// patched against the others", and that the player asked for the original back.</para>
/// </summary>
internal static class FieldRadarTuning
{
    // ---------------- Reach ----------------

    /// <summary>How far the beams look.
    ///
    /// <para><b>This is 30 m, not AHC's 1000.</b> AHC casts its open/close ray at a
    /// literal 1000 tiles (<c>ReactiveRadar.cs:165</c>) and that was ported verbatim —
    /// but it rests on a premise AHC never had to state and this game does not meet.
    /// AHC's heading is DISCRETE (a tile grid with stepped turns), so a beam points at
    /// one of eight fixed directions and cannot drift; its tile world cannot express a
    /// sub-tile edge either. Here the camera is continuous and a third-person follow
    /// camera settles constantly, so at long range a beam hovers on a depth edge and
    /// flips across it: a measured session logged the left beam alternating between two
    /// static surfaces 21 m apart in depth, returning to each within 9 cm, 100 cues in
    /// 30 seconds. The reach is what turns a fraction of a degree into a 21 m jump.</para>
    ///
    /// <para>The value is AHC's own other distance, not a number chosen here:
    /// <c>GameConfig.ScanDistance = 30</c> tiles, the range over which AHC tells the
    /// player about the world at all (its POI and door scan), which the RE7 port already
    /// carries as <c>ScanRange</c>/<c>DoorScanRange</c>
    /// (<c>Re7Access/src/RadarService.cs:87,93</c>).</para></summary>
    public const float ReachM = 30f;

    // ---------------- Cadence (ms on the monotonic clock) ----------------

    /// <summary>Normal sensing interval, ~30 Hz. Three beams × three body heights =
    /// nine classified casts per evaluation (RE7 <c>:106</c>).</summary>
    public const long DirectSenseMs = 33;

    /// <summary>While the camera turns, ~60 Hz — AHC's own fixed tick
    /// (<c>RPGGameLoop.cs:10</c>, RE7 <c>:108</c>). At 30 Hz a sweeping beam steps
    /// over narrow openings between samples, and turning IS the scan.</summary>
    public const long TurnSenseMs = 16;

    /// <summary>Backoff when an evaluation could not run at all (RE7 <c>:111</c>).</summary>
    public const long FallbackSenseMs = 100;

    /// <summary>One global gate after any cue: AHC's <c>delay = 0.1 s</c>
    /// (<c>ReactiveRadar.cs:96</c>, RE7 <c>:94</c>). Gates EMISSION only — a break met
    /// while it is shut is re-detected and fires when it lifts, never consumed.</summary>
    public const long CueCooldownMs = 100;

    // ---------------- Travel signal ----------------

    /// <summary>Slowest speed (m/s) the radar calls locomotion (RE7 <c>:114</c>).</summary>
    public const float MinBodySpeedMs = 0.15f;

    /// <summary>Seconds; time-normalized EMA on the travel direction (RE7 <c>:115</c>).</summary>
    public const float TravelSmoothTau = 0.15f;

    /// <summary>Seconds; EMA on speed (RE7 <c>:116</c>).</summary>
    public const float SpeedSmoothTau = 0.3f;

    /// <summary>A per-evaluation position jump above this is a teleport, not a walk
    /// (RE7 <c>:117</c>) — forget every beam rather than invent a line across a
    /// loading boundary.</summary>
    public const float TeleportM = 3.0f;

    /// <summary>Deliberate turning, in degrees per second. NOT a hand-picked rate:
    /// AHC wipes the laterals on ANY nonzero look input, with no threshold
    /// (<c>FPExploring.cs:362-365</c>); a continuous camera needs one only to reject
    /// sensor noise. RE7 measured its yaw noise while walking without turning at zero
    /// across 92/92 samples, bounding it at ~2.9 °/s, and takes four times that
    /// (RE7 <c>:137</c>). The 60 °/s this replaces was a guess, and it let ordinary
    /// slow turns sweep the laterals' memory across the geometry.</summary>
    public const float TurnResetRateDegPerSec = 4f * 2.9f;

    // ---------------- Proximity pitch ----------------

    /// <summary>AHC's own rule: a tone on every change of the ROUNDED distance while
    /// the rounded value is under this, and only on a beam travelling the player's way
    /// (<c>ReactiveRadar.cs:177-191</c>, RE7 <c>:142</c>). Absolute metres — tiles and
    /// metres are one to one here — and explicitly NOT a time-to-contact bucket.</summary>
    public const int PitchReachM = 3;

    /// <summary>Minimum "face-on-ness" before a beam may call a surface its own:
    /// cos 30°. Beams sit 90° apart, so every flat surface lies within 45° of SOME
    /// beam; a graze past this is firmly the neighbouring beam's wall seen edge-on, and
    /// letting both claim it is what made several tones sound at once for one direction
    /// of travel (RE7 <c>RadarService.cs:190</c>, kept there from its v12.11 dump: a
    /// beam grazing its neighbour's wall reported it 1.41× further away as a second
    /// obstacle).</summary>
    public const float FaceOnCos = 0.866f;

    // ---------------- Passability ----------------
    //
    // The two thresholds on a beam's BODY clearance that decide whether the player can
    // go that way (FieldRadarClearance). Both are expressed in the avatar's own capsule
    // radius, read from the game at runtime — the mod never states a distance in metres
    // that the avatar's own size did not produce.
    //
    // The multipliers are a DESIGN choice and are named as such rather than dressed up
    // as measurements: nothing in SF6 publishes "how deep a gap must be before a blind
    // player wants to hear about it". What makes them safe is that the quantity they
    // cut is bimodal by construction. Clearance is measured ALONG the beam, so a side
    // alley reports its own DEPTH (tens of metres) while the open side of an ordinary
    // street reports half its WIDTH (one to three). There is no city geometry in
    // between, so the pair can sit far apart and any value inside the gap behaves the
    // same. They are logged at arming so a session in play can correct them.

    /// <summary>Clearance at or above this many body radii is a way through: 12, i.e.
    /// four times RE7's measured door-leaf span of three radii
    /// (<see cref="DoorLeafSpanRadii"/>, RE7 <c>:193</c>). Deliberately deeper than any
    /// alcove a facade contains — a shopfront recess, a doorway reveal, the space
    /// behind a bench — so what clears it is a street, an alley or a plaza.</summary>
    public const float PassableEnterRadii = 12f;

    /// <summary>Fallback only, for the evaluations before the probe has measured.
    /// The real blocked boundary is the game's own — the length of its longest forward
    /// feeler, which is what the spoken readout already means by "blocked"
    /// (<see cref="FieldNavSideRays.PublishedReachM"/>, 2.00 m at runtime). Four radii
    /// is 2.0 m at the capsule this game reports, so the fallback agrees with it rather
    /// than competing. Either way the gap to <see cref="PassableEnterRadii"/> is the
    /// dead band, and it is the whole point: the avatar must physically travel about
    /// four metres to change a verdict, where the retired line model flipped on a
    /// fraction of a degree of camera settle.</summary>
    public const float BlockedEnterRadii = 4f;

    /// <summary>How long a BLOCKED verdict must hold before it is believed. Sized as
    /// four sensing intervals at the walking rate (<see cref="DirectSenseMs"/>), the
    /// same "four times the noise floor" margin RE7 uses for its turn rate
    /// (<see cref="TurnResetRateDegPerSec"/>). The hysteresis band already rejects
    /// sensor noise; this rejects the transient — a pedestrian or a car crossing a beam
    /// for a frame or two is not a wall appearing.
    ///
    /// <para><b>It applies to BLOCKED only, and the asymmetry is physical, not a
    /// tuning preference.</b> A transient object can only ever make a beam's clearance
    /// SHORTER: something moving into the beam is nearer than what was behind it. There
    /// is no object whose arrival makes the way further open. So an outward jump in
    /// clearance has exactly two causes — the geometry ended, or something that was
    /// blocking left — and both of those are true news the moment they happen. A
    /// PASSABLE verdict therefore fires on the first sample that sees it, with no delay
    /// at all, which is the cue a player walking at speed cannot afford to receive late:
    /// this window at a run is most of a metre of travel, and an alley mouth is only a
    /// few metres wide.</para>
    ///
    /// <para>Firing PASSABLE immediately cannot chatter, because the hysteresis is what
    /// stops chatter, not the delay: leaving PASSABLE needs the clearance to fall all
    /// the way to <see cref="BlockedEnterRadii"/>, metres of real travel away.</para></summary>
    public const long BlockedConfirmMs = 4 * DirectSenseMs;

    // ---------------- Body units ----------------

    /// <summary>A door leaf spans about three body radii (RE7 <c>:193</c>, measured
    /// there). It is the calibration behind <see cref="PassableEnterRadii"/> — the one
    /// figure available for "how big is an opening a person walks through", which is
    /// what makes twelve radii defensible as "deeper than any alcove".</summary>
    public const float DoorLeafSpanRadii = 3f;

    // ---------------- Stairs ----------------

    /// <summary>Minimum gap before the stairs motif re-announces on the same beam
    /// (RE7 <c>:156</c>).</summary>
    public const long StairMotifCooldownMs = 1500;

    /// <summary>On a walkable slope the hit distance grows steadily with ray height;
    /// each step between heights must land inside this band (RE7 <c>:164-165</c>).</summary>
    public const float StairGapMinM = 0.3f, StairGapMaxM = 2.0f;

    /// <summary>Only look for stairs this close (RE7 <c>:166</c>).</summary>
    public const float StairDetectM = 3.0f;

    // ---------------- Emission ----------------

    /// <summary>Flat stereo pan: <c>sin(angle) × 0.9</c> (RE7 <c>:815</c>). Never 3D —
    /// a radar cue is egocentric and categorical, and HRTF would make "ahead"
    /// ambiguous with "behind" and double-encode distance.</summary>
    public const float PanScale = 0.9f;

    /// <summary>The level of EVERY radar cue — open, closed, stairs and the approach
    /// tone alike. One number, deliberately.
    ///
    /// <para>A radar cue is <b>categorical</b>: it means "left / ahead / right", not
    /// "an object exists at that point". The cross-game reference is explicit that such
    /// a cue must sound <i>identical on every fire</i>, differing only in pan and in its
    /// deliberate pitch — anything that varies its loudness with distance double-encodes
    /// distance and destroys the instant recognition the cue exists for — the shared
    /// audio-navigation reference states it as a hard rule, "radar cues are FLAT PAN,
    /// never 3D audio". RE7 nonetheless faded its approach tone
    /// with range (<c>RadarService.cs:602</c>, 0.45/0.35/0.28) and set the open/close
    /// pair at 0.4 (<c>:655/660</c>); both are dropped here on the tester's report that
    /// the cues were too quiet and should be "panned but fixed", which is the rule
    /// rather than the exception.</para>
    ///
    /// <para>Set well above <see cref="SF6Access.Services.AudioService.DEFAULT_VOLUME"/>
    /// (the mod's general cue level, documented as "over the street ambience, under the
    /// reader's voice"): the radar is the primary navigation channel, not an
    /// accompaniment. It stays short of full scale so several cues summing in the mixer
    /// cannot clip.</para></summary>
    public const float CueVolume = 0.9f;

    /// <summary>Something closed in / the beam sees past where the boundary was. The
    /// mod's own cue files, the pair RE7 uses for the same two events.</summary>
    public const string WallSound = "impassable.mp3";
    public const string ExitSound = "exit.mp3";
}
