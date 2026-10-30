# Roku: audio items show no picture in a grid

A note for a possible pull request to [jellyfin-roku](https://github.com/jellyfin/jellyfin-roku). HI wants to look at
it later; it is not part of any Jellywave release. Written 2026-10-30.

**Status:** read from the source, not tested on a device. Nothing here has been sent upstream.

## What we saw

Jellyfin Roku 3.1.0 on a Roku Express (3960X), Jellyfin 12.4.0: the Live Radio tile, the station list and playback all
work, but the station entries have no picture. The same stations show their pictures in the web client and the Android
app, so the server has them.

## Cause

The Roku app gives a text value to a field that only takes a node, so the picture address is lost.

1. `components/ItemGrid/LoadItemsTask2.bs`, the `item.Type = "Audio"` branch:

   ```brightscript
   tmp = CreateObject("roSGNode", "MusicSongData")
   tmp.type = "Audio"
   tmp.image = api.items.GetImageURL(item.id, "primary", 0, { "maxHeight": 280, "maxWidth": 280, "quality": "90" })
   ```

2. `api.items.GetImageURL` returns a URL string (`source/api/sdk.bs`, `buildURL(...)`).
3. `components/data/MusicSongData.xml` declares that field as a node:
   `<field id="image" type="node" onChange="setPoster" />`.
4. `components/data/MusicSongData.bs` reads `.url` from it and otherwise leaves the poster empty:

   ```brightscript
   sub setPoster()
       if m.top.image <> invalid
           m.top.posterURL = m.top.image.url
       else
           m.top.posterURL = ""
       end if
   end sub
   ```

5. `components/ItemGrid/GridItem.bs` draws `itemData.PosterUrl`, which stays empty.

| Version | Line in `LoadItemsTask2.bs` |
|---|---|
| 3.1.0 (on our Roku) | 363 |
| 3.2.4 (latest release on 2026-10-30) | 527 |
| master at 72cfe98 (2026-10-10) | 516 |

The neighbouring `Playlist` branch does it correctly, which is the pattern to copy:

```brightscript
tmp.image = PosterImage(item, imageSizes.MUSIC_COVER)
```

`PosterImage` (`source/api/Image.bs`) returns an image node through `ImageDataNode`, so `.url` exists.

**The list reaches this branch because stations are `Audio` items.** It should hit every `Audio` item shown in a Roku
grid, not only Jellywave's, so a music library's songs should be a way to reproduce it without the plugin.

## Likely fix

One line in the `Audio` branch:

```brightscript
tmp.image = PosterImage(item, imageSizes.MUSIC_COVER)
```

Check before sending it:

- `PosterImage` uses `ItemImageURL`, and the recent "verify image tags" work (PR #1108) may make that depend on
  `ImageTags` in the item JSON. Confirm the grid's request asks for image tags, or that a channel item has them.
- `imageSizes.MUSIC_COVER` is 500 × 500; the current call asks for 280 × 280. Pick a size that suits the grid and a
  Roku Express.
- An audio item with no picture should fall back cleanly (`PosterImage` returns invalid; `setPoster` handles that).

## How to confirm it

1. Enable developer mode on a Roku and sideload a build of the app.
2. Watch the debug console (telnet to the Roku on port 8085) while opening a grid with audio items. Before the fix, a
   type mismatch warning for the `image` field of `MusicSongData` should appear. Roku's docs and forum reports differ on
   whether a wrong type only warns or stops the script; our grid keeps working, which fits the warning.
3. Apply the one-line fix: the songs and the stations show pictures.

## Before opening a pull request

- Two searches of the project's issues on 2026-10-30 ("audio poster", "song image grid") found no existing report;
  search again.
- Read the project's contributing guide.
- Test on current master, not on 3.1.0.

## Why the plugin cannot fix it

The plugin could only avoid this branch by making stations a different item type, and that would break playback and the
station page on every other client. The picture is already on the item; the Roku app drops it.

## Sources

- [Roku Developer Program: "this error field"](https://forum.developer.roku.com/t/this-error-field/4790) (type mismatch warnings when setting a field)
- [Roku SDK docs archive: ifSGNodeField](https://sdkdocs-archive.roku.com/ifSGNodeField_1607884.html)
- [Roku SDK docs archive: Debugging SceneGraph Applications](https://sdkdocs-archive.roku.com/Debugging-SceneGraph-Applications_3736509.html)
