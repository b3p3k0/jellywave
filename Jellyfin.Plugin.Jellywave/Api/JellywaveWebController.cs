using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellywave.Channels;
using Jellyfin.Plugin.Jellywave.Configuration;
using Jellyfin.Plugin.Jellywave.Services;
using Jellyfin.Plugin.Jellywave.Web;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Jellywave.Api;

/// <summary>
/// Endpoints behind the station page: the injected assets (anonymous, like the web client itself), the feed
/// (any signed-in user) and the injection status (administrators, for the configuration page).
/// </summary>
[ApiController]
[Route("Jellywave")]
public class JellywaveWebController : ControllerBase
{
    private const string ScriptResource = "Jellyfin.Plugin.Jellywave.Web.ui.js";
    private const string StyleResource = "Jellyfin.Plugin.Jellywave.Web.ui.css";
    private static readonly TimeSpan AssetCacheLifetime = TimeSpan.FromDays(1);

    private readonly NowPlayingState _state;
    private readonly LiveItemFinder _finder;
    private readonly IImageProcessor _imageProcessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellywaveWebController"/> class.
    /// </summary>
    /// <param name="state">The shared now-playing state.</param>
    /// <param name="finder">The live item finder.</param>
    /// <param name="imageProcessor">The image processor, for image tags.</param>
    public JellywaveWebController(NowPlayingState state, LiveItemFinder finder, IImageProcessor imageProcessor)
    {
        _state = state;
        _finder = finder;
        _imageProcessor = imageProcessor;
    }

    /// <summary>
    /// The injected script. Anonymous: it is part of the web client's page load, before sign-in.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("ui.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public IActionResult Script() => Asset(ScriptResource, "text/javascript");

    /// <summary>
    /// The injected stylesheet.
    /// </summary>
    /// <returns>The stylesheet.</returns>
    [HttpGet("ui.css")]
    [AllowAnonymous]
    [Produces("text/css")]
    public IActionResult Style() => Asset(StyleResource, "text/css");

    /// <summary>
    /// What is on air, for the station page. Any signed-in user.
    /// </summary>
    /// <returns>The feed.</returns>
    [HttpGet("Feed")]
    [Authorize]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<FeedResponse> Feed()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var feed = _state.Feed();
        var next = feed?.Context?.ShowHandover?.NextShow;
        var response = new FeedResponse
        {
            StationName = JellywaveChannel.StationName(config),
            Online = feed?.StreamOnline,
            Title = feed?.NowPlaying?.Title,
            Artist = feed?.NowPlaying?.Artist,
            Album = feed?.NowPlaying?.Album,
            TrackLine = _state.TrackLine(),
            Description = _state.Description() ?? JellywaveChannel.DefaultOverview,
            Dj = feed?.Dj?.Name,
            DjTagline = feed?.Dj?.Tagline,
            Show = feed?.ActiveShow?.Name,
            NextShow = next?.Name,
            NextShowPresenter = next?.Presenter,
            NextShowStartsAt = next?.StartsAt,
            Listeners = feed?.Listeners?.Current,
            FetchedUtc = feed is null ? null : _state.FetchedUtc
        };

        var channel = _finder.FindChannel(config);
        response.ChannelId = channel?.Id;
        var item = channel is null ? null : _finder.FindLiveItem(channel);
        if (item is not null)
        {
            response.ItemId = item.Id;
            var image = item.GetImageInfo(ImageType.Primary, 0);
            response.ImageTag = image is null ? null : _imageProcessor.GetImageCacheTag(item, image);
        }

        return response;
    }

    /// <summary>
    /// Whether the station page is injected into the web client (configuration page).
    /// </summary>
    /// <returns>The status.</returns>
    [HttpGet("WebUi")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces("application/json")]
    public ActionResult<WebUiStatus> WebUi() => WebUiStartupTask.Status;

    private IActionResult Asset(string resource, string contentType)
    {
        var stream = typeof(JellywaveWebController).Assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound();
        }

        var etag = new EntityTagHeaderValue("\"" + WebUiTransforms.AssetVersion() + "\"");
        Response.Headers[HeaderNames.CacheControl] = "public, max-age=" + (int)AssetCacheLifetime.TotalSeconds;
        return File(stream, contentType, lastModified: null, entityTag: etag);
    }
}
