# Live wallpaper runtime

## Current state

Spotifly supports images, GIF, MP4 and WebM. Video is always muted and is
paused whenever the Spotify document becomes hidden or frozen. Playback only
resumes when the window becomes visible and wallpaper mode is enabled.

## Wallpaper Engine compatibility

Wallpaper Engine projects are described by `project.json`. Spotifly can support
these project types in stages:

- `video`: import the file referenced by `project.json` and use the existing
  muted video renderer.
- `web`: run the referenced HTML project in an isolated wallpaper frame and
  provide compatibility shims for Wallpaper Engine's JavaScript APIs.
- `scene`: not directly compatible. Scene projects need Wallpaper Engine's own
  renderer and downloaded projects are commonly packed as `scene.pkg`.
- `application`: not embedded for security and performance reasons.

The import flow should only use Wallpaper Engine projects already installed by
the user. It should not download or redistribute Workshop content.

## Audio-reactive web wallpapers

Wallpaper Engine web wallpapers register a callback through
`window.wallpaperRegisterAudioListener`. Spotifly can provide the same function.

The required bridge consists of:

1. A small native helper captures the Spotifly process audio through WASAPI
   process loopback.
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

Workshop web wallpapers are third-party code. They must run in an isolated
frame with no Spotify tokens, cookies or privileged APIs. Local files should be
served through a restricted virtual filesystem. Network access should be off by
default and exposed only as an explicit user option.

## Performance targets

- 30 FPS by default, optional 60 FPS.
- No audio capture, decoding or animation while minimized.
- One animation frame per spectrum update at most.
- Release object URLs, decoders and audio capture sessions when changing a
  wallpaper.
- Fall back to the current static wallpaper when a project fails to load.
