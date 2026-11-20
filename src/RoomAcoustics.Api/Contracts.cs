namespace RoomAcoustics.Api;

/// <summary>A point in the room, metres, in Core's frame: x along the length, y across the width, z up.</summary>
public sealed record PointDto(double X, double Y, double Z);

/// <summary>
/// One surface group: a material (an index into /api/materials) and a scattering coefficient 0 to 1 used in every band. <c>CustomAlpha</c>, eight absorption
/// coefficients 0 to 1 (63 Hz to 8 kHz), is used in place of the material when it is given. <c>AudienceEdgeM</c> makes the group an audience (seating) area:
/// the length in metres of its edge that is open to the room rather than against a wall, which adds a 0.5 m wide strip along it to the area the absorption
/// acts over (the edge effect). <c>Open</c> makes it an opening to the outside (a window, a stage opening): every band is absorbed fully, as sound that
/// leaves the room does not come back. <c>Screen</c>, for a group of an imported model, makes its surfaces free-standing screens inside the room rather than
/// part of its closed surface. <c>TransmissionLossDb</c>, for a screen or partition, is its sound reduction R in the eight octave bands (null: no sound passes
/// through it); a wall of the room's closed surface has nothing beyond it, so it is not used there.
/// </summary>
public sealed record SurfaceDto(int MaterialId, double Scattering = 0.1, double[]? CustomAlpha = null, double? AudienceEdgeM = null, bool Open = false, bool Screen = false,
    double[]? TransmissionLossDb = null);

/// <summary>
/// A free-standing screen: a vertical rectangle <c>LengthM</c> long and <c>HeightM</c> high, centred on (<c>X</c>, <c>Y</c>) with its bottom edge at
/// <c>BaseZ</c>, turned <c>AngleDeg</c> from the x axis towards y, with the same finish on both sides. Sound reflects off either side and bends round its free
/// edges (Core's <c>ScreenDiffraction</c>). A <c>Door</c> is an opening in it, standing on its base: a screen across the whole room with a door in it is
/// a partition between two coupled rooms.
/// </summary>
public sealed record ScreenDto(double X, double Y, double BaseZ, double LengthM, double HeightM, double AngleDeg, SurfaceDto Finish, DoorDto? Door = null);

/// <summary>An opening in a screen, <c>WidthM</c> wide and <c>HeightM</c> high from the screen's base, starting <c>OffsetM</c> along it from its first end.</summary>
public sealed record DoorDto(double OffsetM, double WidthM, double HeightM);

public sealed record RoomDto(double LengthM, double WidthM, double HeightM, SurfaceDto Floor, SurfaceDto Walls, SurfaceDto Ceiling, IReadOnlyList<ScreenDto>? Screens = null);

/// <summary>
/// A directional source, aimed along an azimuth (degrees from +x towards +y) and an elevation (degrees above horizontal): by <c>Kind</c>, a horn-like
/// pattern from its published -6 dB coverage angles ("horn", the default), a coherent line array ("lineArray"), an uploaded measured balloon ("balloon")
/// or a human talker ("talker", which takes only the aim). Omit it for an omnidirectional source. A surface source ("surface") is a rectangle (<c>Surface</c>)
/// centred on the source's position and facing where it is aimed, radiating evenly over its area: a noisy wall or machine face, a window letting traffic in,
/// a ceiling of diffusers.
/// </summary>
public sealed record DirectivityDto(
    double HorizontalDeg, double VerticalDeg, double AimAzimuthDeg = 0, double AimElevationDeg = 0, double FloorDb = -20,
    string? Kind = null, LineArrayDto? LineArray = null, string? BalloonId = null, SurfaceSourceDto? Surface = null);

/// <summary>
/// A surface source's size: <c>WidthM</c> across (level, at right angles to where it faces; for one facing straight up or down, along its aim's azimuth turned
/// a right angle) and <c>HeightM</c> the other way. <c>OneSided</c> radiates only to the side it faces, as a cosine (Lambert) pattern, as a vibrating wall
/// does into the room; otherwise each part of it radiates evenly every way.
/// </summary>
public sealed record SurfaceSourceDto(double WidthM, double HeightM, bool OneSided = true);

/// <summary>
/// A coherent line array (Core's <c>CoherentLineArray</c>): N elements at a spacing, running up and down or across, with a delay step and a splay to steer and
/// curve it and a shading to trade beam width for side lobes. Each element has the horn-like pattern of the enclosing <see cref="DirectivityDto"/>.
/// </summary>
public sealed record LineArrayDto(
    int Elements = 8, double SpacingM = 0.2, string Orientation = "vertical", double DelayStepMs = 0, double SplayDegPerElement = 0,
    string Shading = "uniform", double SidelobeDb = 30);

/// <summary>
/// A further source in the same room: its place, its pattern (omit for omnidirectional), its delay, and its level: either <c>SoundPowerDb</c>, its own sound power
/// level in each octave band (63 Hz to 8 kHz), or, when that is omitted, <c>GainDb</c> dB above or below the first source's spectrum.
/// </summary>
public sealed record SourceDto(PointDto Position, DirectivityDto? Directivity = null, double GainDb = 0, double DelayMs = 0, IReadOnlyList<double>? SoundPowerDb = null);

/// <summary>An uploaded measured balloon (a CSV of levels by direction and octave band), kept in memory and referred to by id.</summary>
public sealed record BalloonDto(string Id, string Name, int Azimuths, int Elevations, double MinDb, double MaxDb, IReadOnlyList<PolarCutDto> Horizontal, IReadOnlyList<PolarCutDto> Vertical);

/// <summary>One band's level on a cut through the balloon: horizontal (elevation 0, angle = azimuth) or vertical (azimuth 0, angle = elevation), every 5 degrees.</summary>
public sealed record PolarCutDto(int BandHz, IReadOnlyList<double> AnglesDeg, IReadOnlyList<double> LevelsDb);

/// <summary>
/// An imported model (see /api/model/import) with a surface for each of its groups (DXF layers, OBJ materials). <c>Outdoors</c> takes it as standing in the
/// open air: every surface a two-sided screen inside an open box round the model (Core's <c>SurfaceMesh.ToOpenAir</c>), so it need not be closed.
/// </summary>
public sealed record ModelRoomDto(string ModelId, Dictionary<string, SurfaceDto> Surfaces, IReadOnlyList<ScreenDto>? Screens = null, bool Outdoors = false);

/// <summary>
/// The room is either a box (<c>Room</c>) or an imported model (<c>Model</c>); exactly one must be given. The Speech Transmission Index is set by the
/// A-weighted speech level at 1 m from a talker, the background noise as an NC rating (15 to 70 in steps of 5; any other value is none), the voice
/// ("male" or "female", which sets the STI's band weighting) and <c>StiSignal</c>: "talker" takes the speech to be that talker's standard spectrum,
/// "sources" takes it to be what the sources themselves give at the receiver, from their own sound power spectra (which must then be given).
/// </summary>
public sealed record SimulateRequest(
    RoomDto? Room,
    ModelRoomDto? Model,
    PointDto Source,
    PointDto Receiver,
    int RayCount = 60_000,
    int ImageSourceOrder = 2,
    double TemperatureC = 20,
    double HumidityPercent = 60,
    int Seed = 1,
    DirectivityDto? Directivity = null,
    IReadOnlyList<SourceDto>? ExtraSources = null,
    double SpeechLevelDbA = 60,
    int BackgroundNc = 30,
    string Voice = "male",
    IReadOnlyList<double>? SoundPowerDb = null,
    IReadOnlyList<PointDto>? ExtraReceivers = null,
    string StiSignal = "talker",
    bool AngleDependentAbsorption = false,
    double WorkroomD1M = 5,
    double WorkroomD2M = 16);

/// <summary>The reflections at a receiver from the first source: its paths of up to <c>MaxOrder</c> reflections (1 to 4), as far as the room's face count allows.</summary>
public sealed record ReflectionsRequest(
    RoomDto? Room, ModelRoomDto? Model, PointDto Source, PointDto Receiver, int MaxOrder = 2, double TemperatureC = 20, double HumidityPercent = 60,
    DirectivityDto? Directivity = null);

/// <summary>
/// One specular reflection: the surfaces it bounces off in order, its path length and how long after the direct sound it arrives, its level against the direct
/// sound in each octave band (null where nothing is left) and as one number (the mean of the 500 Hz and 1 kHz bands), and the route, source to receiver.
/// </summary>
public sealed record ReflectionDto(int Order, IReadOnlyList<string> Surfaces, double DistanceM, double DelayMs, double?[] LevelsDb, double? OverallDb, double[][] Path);

/// <summary><c>Total</c> is how many paths there are; the list is the first (the earliest) of them, at most <see cref="SimulationService.MaxListedReflections"/>.</summary>
public sealed record ReflectionsResponse(double DirectDistanceM, double DirectDelayMs, int ImageSourceOrderUsed, int Total, IReadOnlyList<ReflectionDto> Reflections);

public sealed record RaysRequest(
    RoomDto? Room,
    ModelRoomDto? Model,
    PointDto Source,
    PointDto? Receiver,
    int RayCount = 120,
    int MaxReflections = 12,
    int Band = 4, // 0 (63 Hz) to 7 (8 kHz), or -1 for the overall response (the mean of the 500 Hz and 1 kHz bands)
    double TemperatureC = 20,
    double HumidityPercent = 60,
    int Seed = 1,
    DirectivityDto? Directivity = null,
    IReadOnlyList<SourceDto>? ExtraSources = null);

public sealed record MaterialDto(int Id, string Name, string Category, double?[] Alpha);

public sealed record RayDto(double[][] Points, int Reflections, bool Scattered, bool ReachesReceiver, double[] LevelsDb);

/// <summary>
/// Per octave band values, 63 Hz to 8 kHz. A value that cannot be worked out (too short a decay, say) is null. <c>DirectStrengthGDb</c> is G of the direct sound alone
/// and <c>DirectToReverberantDb</c> the direct sound against all that follows it; both null where a wall blocks the direct sound, and absent from a quick estimate.
/// </summary>
public sealed record BandMetricsDto(
    double?[] T30S, double?[] T20S, double?[] EdtS, double?[] C50Db, double?[] C80Db, double?[] D50, double?[] CentreTimeS,
    double?[] StrengthGDb, double?[] SabineT60S, double?[] EyringT60S,
    double?[] EarlyStrengthGDb, double?[] LateStrengthGDb, double?[] Lf80, double?[] Lfc80, double?[] LateLateralLevelDb, double?[] StageSupportEarlyDb, double?[] StageSupportLateDb,
    double?[] SplDb, double?[] SplADb, double?[] EchoCriterionSpeech, double?[] EchoCriterionMusic,
    double?[] MillingtonT60S, double?[] ArauPuchadesT60S, double?[] FitzroyT60S,
    double?[]? DirectStrengthGDb = null, double?[]? DirectToReverberantDb = null, double?[]? StageSupportTotalDb = null, double?[]? EarlyEnsembleLevelDb = null,
    double?[]? IaccEarly = null, double?[]? IaccLate = null, double?[]? IaccAll = null);

/// <summary>
/// The ISO 3382-1 single-number values (see Core's <c>BroadbandMetrics</c> for the bands each is the mean of); null where they cannot be worked out. <c>IaccEarly</c>
/// and <c>IaccLate</c> are IACC E3 and L3, the mean of 500 Hz to 2 kHz, so that they share their names with the bands they come from.
/// </summary>
public sealed record BroadbandDto(
    double? T30S, double? T20S, double? EdtS, double? C50Db, double? C80Db, double? D50, double? CentreTimeS, double? StrengthGDb, double? EarlyStrengthGDb, double? LateStrengthGDb,
    double? Lf80, double? Lfc80, double? LateLateralLevelDb, double? StageSupportEarlyDb, double? StageSupportLateDb,
    double? SplDb, double? SplADb, double? EchoCriterionSpeech, double? EchoCriterionMusic, double? BassRatio, double? Brilliance,
    double? DirectStrengthGDb = null, double? DirectToReverberantDb = null, double? StageSupportTotalDb = null, double? EarlyEnsembleLevelDb = null,
    double? IaccEarly = null, double? IaccLate = null, double? BinauralQualityIndex = null, double? IaccAll = null);

/// <summary>One third-octave band at the receiver: its nominal centre and the parameters worked out in it (null where they cannot be).</summary>
public sealed record ThirdOctaveBandDto(int Hz, double? T30S, double? T20S, double? EdtS, double? C80Db, double? C50Db, double? D50, double? CentreTimeS, double? StrengthGDb);

/// <summary>The simulation at the receiver in the 23 third-octave bands 50 Hz to 8 kHz (Core's <c>ThirdOctaveSimulation</c>).</summary>
public sealed record ThirdOctaveResponse(IReadOnlyList<ThirdOctaveBandDto> Bands, double ComputeSeconds);

/// <summary>
/// The Speech Transmission Index at the receiver, with what it was worked out for: <c>SignalFrom</c> is "talker" (a talker at <c>SpeechLevelDbA</c> at 1 m) or
/// "sources" (the sources' own levels), and <c>SignalDbA</c> is the A-weighted speech level that reaches the receiver, 125 Hz to 8 kHz.
/// </summary>
public sealed record StiDto(double? Sti, string Rating, string Voice, double SpeechLevelDbA, int BackgroundNc, double? AlconsPercent, double? Stipa = null, double? Rasti = null,
    string SignalFrom = "talker", double? SignalDbA = null, double? Sii = null);

/// <summary>A source pattern's directivity index on its axis (where it is aimed) in each octave band, 63 Hz to 8 kHz, dB: 10 lg Q, so Lp(1 m, on axis) = Lw - 11 + DI.</summary>
public sealed record DirectivityIndexDto(double[] OnAxisDb);

public sealed record DecayDto(double StepS, double?[][] SchroederDb, double?[] OverallSchroederDb);

public sealed record ImpulseDto(double StepS, double?[][] LevelDb, double?[] OverallDb);

/// <summary>Every result at one receiver (the ones of the main results, for the first receiver): where it is, the direct path, the per band and single-number values, the STI, and its decay curves and impulse response.</summary>
public sealed record ReceiverResultDto(PointDto Position, double DirectDistanceM, BandMetricsDto Metrics, BroadbandDto Broadband, StiDto Sti, DecayDto? Decay = null, ImpulseDto? Impulse = null);

/// <summary><c>Receivers</c> lists every receiver: the main one first, then each of the request's <c>ExtraReceivers</c>; <c>Decay</c> and <c>Impulse</c> are the main one's (each receiver's own are in <c>Receivers</c>).</summary>
public sealed record SimulateResponse(
    double VolumeM3, double SurfaceAreaM2, double SpeedOfSoundMs, double DirectDistanceM, double ComputeSeconds,
    BandMetricsDto Metrics, DecayDto Decay, ImpulseDto Impulse,
    int ImageSourceOrderUsed, BroadbandDto Broadband, StiDto Sti, IReadOnlyList<ReceiverResultDto>? Receivers = null, OpenPlanDto? OpenPlan = null,
    WorkroomDto? Workroom = null);

/// <summary>One ISO 3382-3 position: which receiver (0 is the main one), its distance from the source, the standard's A-weighted speech level there and its STI.</summary>
public sealed record OpenPlanPositionDto(int Receiver, double DistanceM, double? SpeechDbA, double? Sti);

/// <summary>
/// The ISO 3382-3 open-plan office results, the source taken as the standard's omnidirectional source of normal speech and the receivers as the positions along
/// a line away from it (see Core's <c>OpenPlanOffice</c>). <c>NotWorkedOut</c> says why the single numbers are missing (more than one source, too few
/// receivers). <c>Omnidirectional</c> is false when the source has a pattern, which the standard does not use. <c>BackgroundDbA</c> is the A-weighted background
/// noise, 125 Hz to 8 kHz (null for none). <c>DecayPositions</c> and <c>StiPositions</c> are how many positions the two straight lines were drawn through.
/// </summary>
public sealed record OpenPlanDto(
    string? NotWorkedOut, bool Omnidirectional, double? BackgroundDbA, IReadOnlyList<OpenPlanPositionDto> Positions,
    double? SpatialDecayDb, double? SpeechAt4mDbA, double? ComfortDistanceM, double? DistractionDistanceM, double? PrivacyDistanceM, double? NearestSti,
    int DecayPositions, int StiPositions);

/// <summary>
/// One ISO 14257 position: which receiver (0 is the main one), its distance from the source, the free-field reference curve there (<c>ReferenceDb</c>), the sound
/// distribution value Lp - LW in each octave band 125 Hz to 4 kHz (<c>BandsDb</c>), for A-weighted pink noise (<c>NormalisedDb</c>) and for the source's own
/// sound power spectrum (<c>SourceDb</c>, null when it has none), all in dB.
/// </summary>
public sealed record WorkroomPositionDto(int Receiver, double DistanceM, double ReferenceDb, IReadOnlyList<double?> BandsDb, double? NormalisedDb, double? SourceDb);

/// <summary>
/// DL2, DLf and DL'fr of a curve in one region of distance (<c>Region</c>: "near", "middle" or "far", from <c>FromM</c> to <c>ToM</c>, null for no end), DL'fr
/// at <c>AtM</c>, and how many positions lie in the region; the three are null when fewer than three do.
/// </summary>
public sealed record WorkroomRegionDto(string Region, double FromM, double? ToM, double AtM, double? DecayPerDoublingDb, double? ExcessDb, double? ExcessAtDb, int Positions);

/// <summary>One spatial sound distribution curve (<c>Curve</c>: "normalised", "source" or an octave band's centre frequency, "125" to "4000") in each region.</summary>
public sealed record WorkroomCurveDto(string Curve, IReadOnlyList<WorkroomRegionDto> Regions);

/// <summary>
/// The ISO 14257 workroom results, the receivers taken as the positions along a line away from the one source (see Core's <c>Workroom</c>). <c>NotWorkedOut</c>
/// says why there are no curves (more than one source, too few receivers in every region). <c>Omnidirectional</c> is false when the source has a pattern.
/// <c>D1M</c> and <c>D2M</c> divide the near, middle and far regions.
/// </summary>
public sealed record WorkroomDto(
    string? NotWorkedOut, bool Omnidirectional, double D1M, double D2M, IReadOnlyList<WorkroomPositionDto> Positions, IReadOnlyList<WorkroomCurveDto> Curves);

/// <summary>One surface group of an imported model with its triangles, three corners (x y z each) per triangle, flat, for drawing, and how many flat surfaces it could be split into.</summary>
/// <summary>A surface group of a model: its name, area, triangles' corners, how many flat surfaces it splits into, and how much of its outline is open to the room (Core's <c>AudienceEdge</c>), for an audience area's exposed edge.</summary>
public sealed record ModelGroupDto(string Name, double AreaM2, double[] Positions, int Surfaces = 1, double ExposedEdgeM = 0);

public sealed record BoundsDto(PointDto Min, PointDto Max);

/// <summary>
/// An imported model. <c>ClosedError</c> says why it cannot be simulated (not a closed surface, say); the model is still returned so it can be looked at.
/// The suggested source and receiver are inside the room, towards its two ends. <c>ScreenGroups</c> are the groups named as screens ("Screens", "Barrier"...),
/// which are taken to be free-standing screens unless the page says otherwise.
/// </summary>
public sealed record ModelDto(
    string Id, string Name, int TriangleCount, IReadOnlyList<ModelGroupDto> Groups, BoundsDto Bounds, double? VolumeM3, string? ClosedError,
    PointDto? SuggestedSource, PointDto? SuggestedReceiver, IReadOnlyDictionary<string, int> Skipped, IReadOnlyList<string>? ScreenGroups = null,
    GeometryCheckDto? Check = null, IReadOnlyList<ModelSpaceDto>? Spaces = null, int? Space = null, IReadOnlyList<string>? Repairs = null);

/// <summary>One room volume of an IFC file, as the model's <c>Spaces</c> list them (<c>Space</c> is the one read): its name, volume, and whether it is an area object rather than a room.</summary>
public sealed record ModelSpaceDto(string Name, double VolumeM3, bool IsArea);

/// <summary>
/// One place where a model is not a closed surface: a hole, edges with more than two faces (crowded), or a separate part. <c>Edges</c> show where it is,
/// each as x1, y1, z1, x2, y2, z2 (a separate part's bounding box); <c>Groups</c> are the groups of the faces there. <c>Fillable</c> is a hole that
/// <c>POST /api/model/fill</c> can close: one loop of edges, in a plane, round an area.
/// </summary>
public sealed record GeometryFaultDto(string Kind, IReadOnlyList<double[]> Edges, IReadOnlyList<string> Groups, double LengthM, string Hint, bool Fillable = false);

/// <summary>Close holes in a model: <c>Holes</c> are their places in its geometry check's list of faults (each must be <c>Fillable</c>).</summary>
public sealed record FillRequest(string ModelId, IReadOnlyList<int> Holes);

/// <summary>Split a surface group of a model into its flat surfaces (see <c>SurfaceSplit</c>).</summary>
public sealed record SplitRequest(string ModelId, string Group);

/// <summary>
/// Edit a surface group of a model: <c>Action</c> "rename" gives it the new name <c>To</c>, "merge" puts its surfaces in the existing group <c>To</c>, and
/// "delete" takes its surfaces out of the model (furniture not wanted, say).
/// </summary>
public sealed record GroupEditRequest(string ModelId, string Group, string Action, string? To = null);

/// <summary>
/// Change a model's shape (see <c>MeshEdit</c>): <c>Action</c> "move" moves the corner at the first point to the second, "face" draws a flat face with the
/// points as its corners, in order, in the surface group <c>Group</c>, and "delete" takes out the flat surface that the face with the three points as its
/// corners is part of.
/// </summary>
public sealed record GeometryEditRequest(string ModelId, string Action, IReadOnlyList<PointDto> Points, string? Group = null);

/// <summary>
/// Repair a model that is not closed (see <c>ModelRepair</c>): <c>Screens</c> are the groups the page takes as free-standing screens (the groups named as
/// screens if not given); with <c>Boxes</c>, separate solids become their boxes.
/// </summary>
public sealed record RepairRequest(string ModelId, bool Boxes = false, IReadOnlyList<string>? Screens = null);

/// <summary>The geometry check of a model that is not a closed surface: how many edges have one face and how many more than two, how many separate surfaces, and the faults, largest first.</summary>
public sealed record GeometryCheckDto(int OpenEdges, int CrowdedEdges, int Parts, IReadOnlyList<GeometryFaultDto> Faults);

/// <summary>A plan map: one value of one metric in one octave band at every point of a horizontal grid.</summary>
public sealed record MapRequest(
    RoomDto? Room, ModelRoomDto? Model, PointDto Source,
    string Metric = "T30", double HeightM = 1.2, double SpacingM = 1.0, double ClearanceM = 0.6,
    int RayCount = 60_000, int ImageSourceOrder = 2, double TemperatureC = 20, double HumidityPercent = 60, int Seed = 1,
    DirectivityDto? Directivity = null,
    IReadOnlyList<SourceDto>? ExtraSources = null,
    string Mode = "plan", string Axis = "x", double PositionM = 0, IReadOnlyList<double>? HeightsM = null, string? SurfaceGroup = null,
    double SpeechLevelDbA = 60, int BackgroundNc = 30, string Voice = "male", IReadOnlyList<double>? SoundPowerDb = null,
    string? ReflectorGroup = null, double ReflectorFromMs = 5, double ReflectorToMs = 80, string StiSignal = "talker", bool AngleDependentAbsorption = false);

/// <summary>One map point: where it is. Its values, for every parameter, are in <see cref="MapResponse.Parameters"/>, in the same order as the points.</summary>
public sealed record MapPointDto(double X, double Y, double Z);

/// <summary>
/// One parameter at every point of a map: <c>Values</c> is, for each point, the value in each of the eight octave bands, 63 Hz to 8 kHz (null where it cannot be worked out;
/// the whole of it is null for the Speech Transmission Index, which is one number), and <c>Broadband</c> is each point's single-number value (ISO 3382-1 band averages).
/// </summary>
public sealed record MapParameterDto(string Unit, double?[][]? Values, double?[] Broadband);

/// <summary>
/// A map. <c>Mode</c> is "plan" (a horizontal grid at HeightM), "section" (a vertical plane x or y = PositionM, with the room's <c>Outline</c> on it as segments
/// of U1 V1 U2 V2, U across and V up), "volume" (the plan grid repeated at each of <c>Heights</c>) or "surface" (HeightM above the upward-facing surface <c>SurfaceGroup</c>,
/// following its slope).
/// </summary>
public sealed record MapResponse(
    string Metric, string Unit, double HeightM, double SpacingM, double ComputeSeconds, IReadOnlyList<MapPointDto> Points, IReadOnlyDictionary<string, MapParameterDto> Parameters,
    string Mode = "plan", string? Axis = null, double? PositionM = null, IReadOnlyList<double>? Heights = null, IReadOnlyList<IReadOnlyList<double>>? Outline = null, string? SurfaceGroup = null);

/// <summary>
/// The room's sound at a listener's two ears, for the browser to play any sound through: the source is traced to a head at the listener's position
/// (facing an azimuth, 0 along +x and 90 along +y), with the head's shadow and delay.
/// </summary>
/// With <c>Separate</c>, each source's response is also given on its own (<see cref="AuraliseResponse.Sources"/>), so that each can play its own sound.
/// <c>HrtfId</c> (from /api/hrtf/import) hears the room through that measured head instead of the spherical model.
public sealed record AuraliseRequest(
    RoomDto? Room, ModelRoomDto? Model, PointDto Source, PointDto Listener,
    double FacingAzimuthDeg = 0, int RayCount = 150_000, int ImageSourceOrder = 2, double TemperatureC = 20, double HumidityPercent = 60, int Seed = 1,
    int SampleRateHz = 44_100, DirectivityDto? Directivity = null, IReadOnlyList<SourceDto>? ExtraSources = null, IReadOnlyList<double>? SoundPowerDb = null,
    bool Separate = false, string? HrtfId = null, bool AngleDependentAbsorption = false, int AmbisonicOrder = 1);

/// <summary>An uploaded measured HRTF (a SOFA file), kept in memory and referred to by id: its name, how many directions were measured, and their sample rate.</summary>
public sealed record HrtfDto(string Id, string Name, int Measurements, double SampleRateHz);

/// <summary>
/// A measured impulse response's parameters per octave band, 63 Hz to 8 kHz, worked out as the simulation's are, with time zero at the direct sound
/// (<c>DirectArrivalS</c> into the recording). <c>DynamicRangeDb</c> is each band's peak over its noise floor (null where the end is silent, 0 where the
/// band could not be used); a decay time needs 10 dB more than the range it is fitted over, so a band short of that has null.
/// </summary>
public sealed record MeasuredDto(string Name, int SampleRateHz, int Channels, double DurationS, double DirectArrivalS, double?[] DynamicRangeDb,
    double?[] T30S, double?[] T20S, double?[] EdtS, double?[] C50Db, double?[] C80Db, double?[] D50, double?[] CentreTimeS);

/// <summary>One source's two ears' impulse responses, on the same scale as the others', so that they can be mixed as they are.</summary>
public sealed record EarsDto(string LeftF32, string RightF32);

/// <summary>
/// An Ambisonic impulse response at the listener's position (the facing is not used), of the request's order 1 to 3: (order + 1)² channels in the AmbiX
/// convention, ACN order (W, Y, Z, X first) with SN3D weighting, each little-endian 32-bit floats in base64, scaled so the largest sample of any is 0.5.
/// </summary>
public sealed record AmbisonicResponse(int SampleRateHz, int Samples, IReadOnlyList<string> ChannelsF32, double DurationS, double ComputeSeconds);

/// <summary>The two ears' impulse responses as little-endian 32-bit floats in base64, scaled so the larger peak is 0.5, with how the ears differ.</summary>
public sealed record AuraliseResponse(
    int SampleRateHz, int Samples, string LeftF32, string RightF32, double DurationS, double InterauralLevelDifferenceDb, double InterauralTimeDifferenceMs,
    double? IaccE3, double?[] IaccEarly, double?[] IaccLate, double? IaccL3, double? BinauralQualityIndex, double ComputeSeconds, IReadOnlyList<EarsDto>? Sources = null);
