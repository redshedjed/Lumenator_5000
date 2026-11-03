using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace YargDmx.Input;

/// <summary>One packet of YARG's UDP data stream (Settings > Data Stream: broadcast to port 36107, about
/// 88 a second; YARG DataStreamController, datagram version 3). Unlike the sACN output it has the tempo,
/// pause, scene, spotlights, sing-alongs, camera cuts and vocal pitch, and its fields can't collide; it
/// has no Stage Kit LEDs. Undocumented: the layout is read from YARG's source.</summary>
public readonly record struct DataStreamPacket(
    byte Scene, byte Paused, byte VenueSize, float Bpm, byte SongSection,
    byte Guitar, byte Bass, byte Drums, byte Keys,
    float Vocal, float Harmony0, float Harmony1, float Harmony2,
    byte LightingCue, byte PostProcessing, bool Fog, byte Strobe, byte Beat, byte Keyframe, bool Bonus,
    bool AutoGenVenue, byte Spotlight, byte Singalong, byte CameraConstraint, byte CameraPriority, byte CameraSubject)
{
    public const int Port = 36107;
    const uint Header = 0x59415247;                  // "YARG"
    const int Length = 47;

    // YARG.Core enums, in their order (the stream sends the index)
    public enum Scenes : byte { Unknown, Menu, Gameplay, Score, Calibration, Practice }
    /// <summary>PauseStateType: 0 at a menu, 1 playing, 2 paused.</summary>
    public bool IsPaused => Paused == 2;
    /// <summary>Beat is a BeatlineType (0 measure, 1 strong, 2 weak) in the one packet after the beatline;
    /// YARG sets it to 3 ("off") after every send. Keyframe and Bonus are one-packet events the same way.</summary>
    public const byte NoBeat = 3;

    /// <summary>YARG.Core LightingType, by index.</summary>
    public static readonly string[] LightingTypes =
    {
        "Default", "Dischord", "Chorus", "CoolManual", "Stomp", "Verse", "WarmManual",
        "BigRockEnding", "BlackoutFast", "BlackoutSlow", "BlackoutSpotlight", "CoolAutomatic", "FlareFast",
        "FlareSlow", "Frenzy", "Intro", "Harmony", "Silhouettes", "SilhouettesSpotlight", "Searchlights",
        "StrobeFastest", "StrobeFast", "StrobeMedium", "StrobeSlow", "StrobeOff", "Sweep", "WarmAutomatic",
        "KeyframeFirst", "KeyframeNext", "KeyframePrevious", "Menu", "Score", "NoCue",
    };

    /// <summary>YARG.Core PostProcessingType, by index (underscores dropped, as our names have them).</summary>
    public static readonly string[] PostProcessingTypes =
    {
        "Default", "Bloom", "Bright", "Contrast", "Posterize", "PhotoNegative", "Mirror",
        "BlackAndWhite", "SepiaTone", "SilverTone", "ChoppyBlackAndWhite", "PhotoNegativeRedAndBlack",
        "PolarizedBlackAndWhite", "PolarizedRedAndBlue", "DesaturatedBlue", "DesaturatedRed",
        "ContrastRed", "ContrastGreen", "ContrastBlue", "GrainyFilm", "GrainyChromaticAbberation",
        "Scanlines", "ScanlinesBlackAndWhite", "ScanlinesBlue", "ScanlinesSecurity",
        "Trails", "TrailsLong", "TrailsDesaturated", "TrailsFlickery", "TrailsSpacey",
    };

    /// <summary>The lighting type's name, or "" for one YARG doesn't have (a newer version).</summary>
    public static string Lighting(byte index) => index < LightingTypes.Length ? LightingTypes[index] : "";

    /// <summary>A cue we know for this lighting type (YARG's Automatic cues are our Loops), else null:
    /// strobe and keyframe types aren't cues.</summary>
    public static Cue? CueFor(byte lighting) => Lighting(lighting) switch
    {
        "CoolAutomatic" => Cue.CoolLoop,
        "WarmAutomatic" => Cue.WarmLoop,
        var name => Enum.TryParse<Cue>(name, out var c) ? c : null,
    };

    public static Strobe StrobeFor(byte lighting) => Lighting(lighting) switch
    {
        "StrobeSlow" => Input.Strobe.Slow, "StrobeMedium" => Input.Strobe.Medium,
        "StrobeFast" => Input.Strobe.Fast, "StrobeFastest" => Input.Strobe.Fastest, _ => Input.Strobe.Off,
    };

    public static PostProcessing PostProcessingFor(byte index) =>
        index < PostProcessingTypes.Length && Enum.TryParse<PostProcessing>(PostProcessingTypes[index], out var p) ? p : Input.PostProcessing.Default;

    public static bool TryParse(ReadOnlySpan<byte> p, out DataStreamPacket packet)
    {
        packet = default;
        if (p.Length < Length || BinaryPrimitives.ReadUInt32LittleEndian(p) != Header || p[4] != 3)
            return false;
        static float F(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadSingleLittleEndian(p[at..]);
        packet = new DataStreamPacket(
            Scene: p[6], Paused: p[7], VenueSize: p[8], Bpm: F(p, 9), SongSection: p[13],
            Guitar: p[14], Bass: p[15], Drums: p[16], Keys: p[17],
            Vocal: F(p, 18), Harmony0: F(p, 22), Harmony1: F(p, 26), Harmony2: F(p, 30),
            LightingCue: p[34], PostProcessing: p[35], Fog: p[36] != 0, Strobe: p[37], Beat: p[38], Keyframe: p[39],
            Bonus: p[40] != 0, AutoGenVenue: p[41] != 0, Spotlight: p[42], Singalong: p[43],
            CameraConstraint: p[44], CameraPriority: p[45], CameraSubject: p[46]);
        return true;
    }

    /// <summary>A packet as YARG would send it (for tests and the simulator).</summary>
    public byte[] ToBytes()
    {
        var b = new byte[Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Header);
        b[4] = 3;
        b[6] = Scene; b[7] = Paused; b[8] = VenueSize;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(9), Bpm);
        b[13] = SongSection; b[14] = Guitar; b[15] = Bass; b[16] = Drums; b[17] = Keys;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(18), Vocal);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(22), Harmony0);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(26), Harmony1);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(30), Harmony2);
        b[34] = LightingCue; b[35] = PostProcessing; b[36] = (byte)(Fog ? 1 : 0); b[37] = Strobe; b[38] = Beat;
        b[39] = Keyframe; b[40] = (byte)(Bonus ? 1 : 0); b[41] = (byte)(AutoGenVenue ? 1 : 0);
        b[42] = Spotlight; b[43] = Singalong; b[44] = CameraConstraint; b[45] = CameraPriority; b[46] = CameraSubject;
        return b;
    }
}

/// <summary>Listens for YARG's data stream broadcasts and hands each packet on.</summary>
public sealed class DataStreamReceiver : IDisposable
{
    readonly UdpClient _udp;
    readonly CancellationTokenSource _stop = new();

    public DataStreamReceiver(Action<DataStreamPacket> onPacket, int port = DataStreamPacket.Port)
    {
        _udp = new UdpClient { ExclusiveAddressUse = false, EnableBroadcast = true };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _ = Task.Run(() => Loop(onPacket));
    }

    async Task Loop(Action<DataStreamPacket> onPacket)
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { continue; }
            if (DataStreamPacket.TryParse(r.Buffer, out var p))
                onPacket(p);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}
