# Live wallpaper runtime

Status: implemented for installed Wallpaper Engine `video` and `web` projects.

## Current state

Spotifly supports images, GIF, MP4 and WebM. Video is always muted and is
paused whenever the Spotify document becomes hidden or frozen. Playback only
resumes when the window becomes visible and wallpaper mode is enabled.

## Wallpaper Engine compatibility

Wallpaper Engine projects are described by `project.json`. Spotifly can support
these project types in stages:

- `video`: convert H.264 MP4 once to a cached VP9/WebM file, then serve it with
  HTTP range support to the existing muted video renderer. Conversion exposes
  progress and cancellation and never runs during normal playback.
- `web`: run the referenced HTML project in an isolated wallpaper frame and
  provide compatibility shims for Wallpaper Engine's JavaScript APIs.
- `scene`: intentionally hidden. Scene projects need Wallpaper Engine's own
  renderer and downloaded projects are commonly packed as `scene.pkg`. Window
  capture/MJPEG was tested and removed because it caused UI stalls and leaked
  Wallpaper Engine windows.
- `application`: not embedded for security and performance reasons.

The import flow should only use Wallpaper Engine projects already installed by
the user. It should not download or redistribute Workshop content.

## Audio-reactive web wallpapers

Wallpaper Engine web wallpapers register a callback through
`window.wallpaperRegisterAudioListener`. Spotifly can provide the same function.

The required bridge consists of:

1. A self-contained helper captures the Windows output mix through WASAPI
   loopback. Process-only capture is a possible later enhancement, but the
   current method works across supported Windows 10 and 11 builds.
2. The helper computes 64 frequency bins for the left channel and 64 for the
   right channel.
3. It sends the 128 normalized values to the wallpaper runtime about 30 times
   per second.
4. The runtime calls the listener registered by the wallpaper.

The capture helper and wallpaper animation loop must suspend while Spotifly is
hidden, minimized or wallpaper mode is disabled.

## Muting imported wallpapers

Imported wallpaper audio must never reach the output device. The web runtime
will mute all HTML media elements, watch dynamically-created media elements and
block wallpaper-owned audio playback. The captured Spotify spectrum is data
only and is never played back by the wallpaper runtime.

## Security boundary

Workshop web wallpapers are third-party code. They run in a sandboxed frame
with no Spotify tokens, cookies or privileged APIs. Files are served only from
the selected project's directory by a loopback-only HTTP host. Existing web
wallpapers retain network access because a number of Workshop projects depend
on remote data.

## Performance targets

- Audio data is sent at about 30 updates per second.
- No audio capture, decoding or animation while minimized.
- No Wallpaper Engine process, hidden capture window or MJPEG stream.
- Video playback reads only the prepared WebM cache.
- One animation frame per spectrum update at most.
- Release object URLs, decoders and audio capture sessions when changing a
  wallpaper.
- Preserve the current static/custom wallpaper path when Wallpaper Host is not
  available.
