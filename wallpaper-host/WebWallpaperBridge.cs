namespace Spotifly.WallpaperHost;

internal static class WebWallpaperBridge
{
    public const string Script = """
(function () {
  "use strict";
  var audioListeners = [];
  var socket = null;
  var mediaToResume = new Set();
  var contexts = [];
  var active = true;

  function forceMute(media) {
    if (!media) return;
    try { media.muted = true; } catch (e) {}
    try { media.defaultMuted = true; } catch (e) {}
    try { media.volume = 0; } catch (e) {}
  }

  function muteMediaTree(root) {
    if (!root || !root.querySelectorAll) return;
    root.querySelectorAll("audio,video").forEach(forceMute);
  }

  var nativePlay = window.HTMLMediaElement && HTMLMediaElement.prototype.play;
  if (nativePlay) {
    HTMLMediaElement.prototype.play = function () {
      forceMute(this);
      if (!active) {
        mediaToResume.add(this);
        return Promise.resolve();
      }
      return nativePlay.apply(this, arguments);
    };
  }

  if (window.AudioNode && AudioNode.prototype.connect) {
    var nativeConnect = AudioNode.prototype.connect;
    AudioNode.prototype.connect = function (target) {
      if (window.AudioDestinationNode && target instanceof AudioDestinationNode) {
        try {
          var gate = target.context.createGain();
          gate.gain.value = 0;
          nativeConnect.call(gate, target);
          var args = Array.prototype.slice.call(arguments);
          args[0] = gate;
          return nativeConnect.apply(this, args);
        } catch (e) {}
      }
      return nativeConnect.apply(this, arguments);
    };
  }

  ["AudioContext", "webkitAudioContext"].forEach(function (name) {
    var NativeContext = window[name];
    if (!NativeContext) return;
    function MutedContext() {
      var context = Reflect.construct(NativeContext, arguments, NativeContext);
      contexts.push(context);
      return context;
    }
    MutedContext.prototype = NativeContext.prototype;
    try { Object.setPrototypeOf(MutedContext, NativeContext); } catch (e) {}
    window[name] = MutedContext;
  });

  function connectAudio() {
    if (socket || !audioListeners.length) return;
    socket = new WebSocket("ws://127.0.0.1:17654/audio?token=spotifly-wallpaper-v1");
    socket.onmessage = function (event) {
      if (!active) return;
      try {
        var values = JSON.parse(event.data);
        audioListeners.slice().forEach(function (listener) {
          try { listener(values); } catch (e) {}
        });
      } catch (e) {}
    };
    socket.onclose = function () {
      socket = null;
      if (active && audioListeners.length) window.setTimeout(connectAudio, 1000);
    };
    socket.onerror = function () {
      try { socket.close(); } catch (e) {}
    };
  }

  window.wallpaperRegisterAudioListener = function (listener) {
    if (typeof listener === "function" && audioListeners.indexOf(listener) < 0) {
      audioListeners.push(listener);
      connectAudio();
    }
  };

  function applyDefaultProperties() {
    var listener = window.wallpaperPropertyListener;
    var source = window.__spotiflyProjectProperties || {};
    if (!listener || typeof listener.applyUserProperties !== "function") return;
    var payload = {};
    Object.keys(source).forEach(function (key) { payload[key] = { value: source[key] }; });
    try { listener.applyUserProperties(payload); } catch (e) {}
  }

  function setActive(next) {
    active = !!next;
    document.querySelectorAll("audio,video").forEach(function (media) {
      forceMute(media);
      if (!active && !media.paused) {
        mediaToResume.add(media);
        try { media.pause(); } catch (e) {}
      }
    });
    contexts.forEach(function (context) {
      try {
        if (active && context.state === "suspended") context.resume();
        if (!active && context.state === "running") context.suspend();
      } catch (e) {}
    });
    if (active) {
      mediaToResume.forEach(function (media) {
        forceMute(media);
        try { nativePlay.call(media).catch(function () {}); } catch (e) {}
      });
      mediaToResume.clear();
      connectAudio();
    }
  }

  window.addEventListener("message", function (event) {
    if (event.data && event.data.type === "spotifly:visibility") setActive(event.data.visible);
  });
  document.addEventListener("visibilitychange", function () { setActive(!document.hidden); });
  document.addEventListener("DOMContentLoaded", function () {
    muteMediaTree(document);
    applyDefaultProperties();
    new MutationObserver(function (entries) {
      entries.forEach(function (entry) {
        entry.addedNodes.forEach(function (node) {
          if (node.tagName === "AUDIO" || node.tagName === "VIDEO") forceMute(node);
          muteMediaTree(node);
        });
      });
    }).observe(document.documentElement, { childList: true, subtree: true });
  });
  window.setTimeout(applyDefaultProperties, 250);
})();
""";
}
