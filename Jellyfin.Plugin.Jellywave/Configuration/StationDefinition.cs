namespace Jellyfin.Plugin.Jellywave.Configuration;

/// <summary>
/// One station in the Live Radio tile. Serialized by Jellyfin's <c>XmlSerializer</c> (the configuration file) and by
/// <c>System.Text.Json</c> (the configuration page), so every property is public and settable. Every station is relayed
/// through Jellyfin (card L10): the <c>Delivery</c> and <c>ClientStreamUrl</c> elements an earlier build saved are
/// unknown to both serializers now and are skipped on load.
/// </summary>
public class StationDefinition
{
    /// <summary>
    /// Default stream mount path of a SUB/WAVE station.
    /// </summary>
    public const string DefaultStreamPath = "/stream.mp3";

    /// <summary>
    /// Default codec and container (SUB/WAVE's default mount is MP3).
    /// </summary>
    public const string DefaultFormat = "mp3";

    /// <summary>
    /// Longest genre the server serves (card L8); a longer one is cut where it is read.
    /// </summary>
    public const int MaxGenreLength = 60;

    /// <summary>
    /// Longest notes text the server serves (card L8); a longer one is cut where it is read.
    /// </summary>
    public const int MaxNotesLength = 280;

    /// <summary>
    /// Initializes a new instance of the <see cref="StationDefinition"/> class with today's single-station defaults.
    /// </summary>
    public StationDefinition()
    {
        Id = string.Empty;
        Name = string.Empty;
        Kind = StationKind.SubWave;
        Enabled = true;
        SortOrder = 0;
        BaseUrl = string.Empty;
        StreamPath = DefaultStreamPath;
        StreamUrl = string.Empty;
        LogoUrl = string.Empty;
        Tagline = string.Empty;
        Genre = string.Empty;
        Notes = string.Empty;
        Website = string.Empty;
        Codec = DefaultFormat;
        Container = DefaultFormat;
        BitrateKbps = PluginConfiguration.DefaultStreamBitrateKbps;
        SampleRate = PluginConfiguration.DefaultStreamSampleRate;
        Channels = PluginConfiguration.DefaultStreamChannels;
    }

    /// <summary>
    /// Gets or sets the station id, the live item's external id. The station migrated from a 0.4.x configuration keeps
    /// <c>live</c>, the external id of the single live item before Live Radio.
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Gets or sets the station's display name (the item name, the station page header).
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the kind of station.
    /// </summary>
    public StationKind Kind { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the station is listed and served.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the position in the station list; lower comes first.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Gets or sets the SUB/WAVE base URL as seen from the Jellyfin server (<see cref="StationKind.SubWave"/> only).
    /// </summary>
    public string BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the stream mount path, resolved under <see cref="BaseUrl"/> (<see cref="StationKind.SubWave"/> only).
    /// </summary>
    public string StreamPath { get; set; }

    /// <summary>
    /// Gets or sets the whole stream URL (<see cref="StationKind.Stream"/> only).
    /// </summary>
    public string StreamUrl { get; set; }

    /// <summary>
    /// Gets or sets an optional logo URL (<see cref="StationKind.Stream"/> only).
    /// </summary>
    public string LogoUrl { get; set; }

    /// <summary>
    /// Gets or sets the station tagline.
    /// </summary>
    public string Tagline { get; set; }

    /// <summary>
    /// Gets or sets the station's genre (card L8, any kind): plain text for the station page header, at most
    /// <see cref="MaxGenreLength"/> characters where it is read.
    /// </summary>
    public string Genre { get; set; }

    /// <summary>
    /// Gets or sets the station's notes (card L8, any kind): plain text for the station page header and a plain stream's
    /// item description, at most <see cref="MaxNotesLength"/> characters where it is read.
    /// </summary>
    public string Notes { get; set; }

    /// <summary>
    /// Gets or sets the station's website (card L8, any kind): a link the station page header shows, never requested by
    /// the server. Read through <c>StationUrls.WebsiteLink</c>; a value that fails the website rule is dropped there.
    /// </summary>
    public string Website { get; set; }

    /// <summary>
    /// Gets or sets the audio codec of the stream.
    /// </summary>
    public string Codec { get; set; }

    /// <summary>
    /// Gets or sets the container of the stream.
    /// </summary>
    public string Container { get; set; }

    /// <summary>
    /// Gets or sets the stream bitrate in kbit/s.
    /// </summary>
    public int BitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the stream sample rate in Hz.
    /// </summary>
    public int SampleRate { get; set; }

    /// <summary>
    /// Gets or sets the stream channel count.
    /// </summary>
    public int Channels { get; set; }
}
