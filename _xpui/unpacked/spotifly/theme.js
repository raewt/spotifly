(function () {
  "use strict";

  var KEY = "spotifly-settings";
  var VER = 7;
  var DB_NAME = "spotifly";
  var STORE = "files";
  var WALLPAPER_HOST = "http://127.0.0.1:17654";
  var WALLPAPER_TOKEN = "spotifly-wallpaper-v1";
  var PRESETS = {
    cozy: { library: "#c9a6a0", main: "#efe4da", nowplaying: "#3e2b26", player: "#3e2b26", accent: "#b56a5c" },
    cocoa: { library: "#c4a882", main: "#f2e6d4", nowplaying: "#3c2e22", player: "#3c2e22", accent: "#c49a6c" },
    sage: { library: "#a8bba6", main: "#eef2e8", nowplaying: "#2a3328", player: "#2a3328", accent: "#6f8a68" },
    dusk: { library: "#3d2f2c", main: "#241c1a", nowplaying: "#1a1412", player: "#1a1412", accent: "#d4a090" },
    original: { library: "#000000", main: "#121212", nowplaying: "#121212", player: "#000000", accent: "#1ed760" }
  };

  var state = load();
  var reduced = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var wallUrl = "";
  var pinTimer = 0;
  var wallpaperDownloadTimer = 0;
  var wallpaperDownloadId = "";
  var wallpaperDownloadStarted = 0;

  function clamp(n, min, max, fallback) {
    n = +n;
    if (isNaN(n)) return fallback;
    return Math.max(min, Math.min(max, n));
  }

  function load() {
    var fallback = {
      v: VER,
      jelly: true,
      wallpaper: true,
      harmony: true,
      textAuto: true,
      tint: 28,
      blur: 6,
      wallpaperFit: "cover",
      wallpaperScale: 100,
      wallpaperX: 50,
      wallpaperY: 50,
      webQuality: 125,
      preset: "cozy",
      text: "#3a241c",
      textMuted: "#6f534b",
      textPlayer: "#f6ebdf",
      blocks: Object.assign({}, PRESETS.cozy)
    };
    try {
      var parsed = JSON.parse(localStorage.getItem(KEY) || "{}");
      var legacyRanges = !parsed.v || parsed.v < 5;
      return {
        v: VER,
        jelly: parsed.jelly !== false,
        wallpaper: parsed.wallpaper !== false,
        harmony: parsed.harmony !== false,
        textAuto: parsed.textAuto !== false,
        tint: legacyRanges ? 28 : clamp(parsed.tint, 8, 70, 28),
        blur: legacyRanges ? 6 : clamp(parsed.blur, 0, 32, 6),
        wallpaperFit: ["cover", "contain", "fill"].indexOf(parsed.wallpaperFit) >= 0 ? parsed.wallpaperFit : "cover",
        wallpaperScale: clamp(parsed.wallpaperScale, 70, 160, 100),
        wallpaperX: clamp(parsed.wallpaperX, 0, 100, 50),
        wallpaperY: clamp(parsed.wallpaperY, 0, 100, 50),
        webQuality: [100, 125, 150, 200].indexOf(+parsed.webQuality) >= 0 ? +parsed.webQuality : 125,
        preset: parsed.preset || "cozy",
        engineWallpaper: parsed.engineWallpaper && parsed.engineWallpaper.id ? parsed.engineWallpaper : null,
        text: parsed.text || "#3a241c",
        textMuted: parsed.textMuted || "#6f534b",
        textPlayer: parsed.textPlayer || "#f6ebdf",
        blocks: Object.assign({}, PRESETS.cozy, parsed.blocks || {})
      };
    } catch (e) {
      return fallback;
    }
  }

  function save() {
    try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) {}
  }

  function hexToRgb(hex) {
    var h = String(hex || "").replace("#", "");
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    var n = parseInt(h, 16);
    if (isNaN(n)) return { r: 58, g: 36, b: 28 };
    return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255 };
  }

  function rgbToHex(r, g, b) {
    function p(v) {
      v = Math.max(0, Math.min(255, Math.round(v)));
      return (v < 16 ? "0" : "") + v.toString(16);
    }
    return "#" + p(r) + p(g) + p(b);
  }

  function luminance(hex) {
    var c = hexToRgb(hex);
    function lin(v) {
      v /= 255;
      return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    }
    return 0.2126 * lin(c.r) + 0.7152 * lin(c.g) + 0.0722 * lin(c.b);
  }

  function inkFor(bg) { return luminance(bg) > 0.42 ? "#3a241c" : "#f6ebdf"; }
  function mutedFor(bg) { return luminance(bg) > 0.42 ? "#6f534b" : "#d4c2b8"; }

  function rgbToHsl(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    var max = Math.max(r, g, b), min = Math.min(r, g, b);
    var h = 0, s = 0, l = (max + min) / 2, d = max - min;
    if (d) {
      s = d / (1 - Math.abs(2 * l - 1));
      if (max === r) h = ((g - b) / d) % 6;
      else if (max === g) h = (b - r) / d + 2;
      else h = (r - g) / d + 4;
      h *= 60;
      if (h < 0) h += 360;
    }
    return { h: h, s: s, l: l };
  }

  function hslToRgb(h, s, l) {
    var c = (1 - Math.abs(2 * l - 1)) * s;
    var x = c * (1 - Math.abs(((h / 60) % 2) - 1));
    var m = l - c / 2;
    var r = 0, g = 0, b = 0;
    if (h < 60) { r = c; g = x; }
    else if (h < 120) { r = x; g = c; }
    else if (h < 180) { g = c; b = x; }
    else if (h < 240) { g = x; b = c; }
    else if (h < 300) { r = x; b = c; }
    else { r = c; b = x; }
    return { r: (r + m) * 255, g: (g + m) * 255, b: (b + m) * 255 };
  }

  function paletteFromKey(hex) {
    var rgb = hexToRgb(hex);
    var hsl = rgbToHsl(rgb.r, rgb.g, rgb.b);
    function mk(hh, ss, ll) {
      var c = hslToRgb(hh, Math.max(0, Math.min(1, ss)), Math.max(0.06, Math.min(0.96, ll)));
      return rgbToHex(c.r, c.g, c.b);
    }
    var h = hsl.h, s = Math.min(hsl.s, 0.34);
    var dark = mk(h, Math.min(s + 0.06, 0.36), 0.2);
    return {
      library: mk(h, Math.min(s + 0.04, 0.32), 0.7),
      main: mk(h, Math.min(s, 0.18), 0.9),
      nowplaying: dark,
      player: dark,
      accent: mk((h + 6) % 360, Math.min(s + 0.12, 0.42), 0.54)
    };
  }

  function openDb() {
    return new Promise(function (resolve, reject) {
      var req = indexedDB.open(DB_NAME, 1);
      req.onupgradeneeded = function () {
        req.result.createObjectStore(STORE);
      };
      req.onsuccess = function () { resolve(req.result); };
      req.onerror = function () { reject(req.error); };
    });
  }

  function idbSet(blob) {
    return openDb().then(function (db) {
      return new Promise(function (resolve, reject) {
        var tx = db.transaction(STORE, "readwrite");
        tx.objectStore(STORE).put(blob, "wallpaper");
        tx.oncomplete = function () { resolve(); };
        tx.onerror = function () { reject(tx.error); };
      });
    });
  }

  function idbGet() {
    return openDb().then(function (db) {
      return new Promise(function (resolve) {
        var tx = db.transaction(STORE, "readonly");
        var req = tx.objectStore(STORE).get("wallpaper");
        req.onsuccess = function () { resolve(req.result || null); };
        req.onerror = function () { resolve(null); };
      });
    });
  }

  function idbClear() {
    return openDb().then(function (db) {
      return new Promise(function (resolve) {
        var tx = db.transaction(STORE, "readwrite");
        tx.objectStore(STORE)["delete"]("wallpaper");
        tx.oncomplete = function () { resolve(); };
        tx.onerror = function () { resolve(); };
      });
    });
  }

  function isVideoBlob(blob) {
    if (!blob) return false;
    var type = String(blob.type || "").toLowerCase();
    if (type.indexOf("video/") === 0) return true;
    var name = String(blob.name || "").toLowerCase();
    return /\.(mp4|webm|mov)$/.test(name);
  }

  function wallpaperHost() {
    var wall = document.getElementById("sf-wallpaper");
    if (wall) return wall;
    wall = document.createElement("div");
    wall.id = "sf-wallpaper";
    if (document.body) document.body.insertBefore(wall, document.body.firstChild);
    return wall;
  }

  function stopWallpaperVideo() {
    var wall = document.getElementById("sf-wallpaper");
    var video = wall && wall.querySelector("video");
    if (video) {
      try { video.pause(); } catch (e) {}
      video.removeAttribute("src");
      try { video.load(); } catch (e2) {}
      video.remove();
    }
    var frame = wall && wall.querySelector("iframe");
    if (frame) {
      frame.src = "about:blank";
      frame.remove();
    }
    if (wall) {
      wall.querySelectorAll(".sf-wallpaper-stage").forEach(function (stage) { stage.remove(); });
    }
  }

  function wallpaperStage() {
    var wall = wallpaperHost();
    var stage = document.createElement("div");
    stage.className = "sf-wallpaper-stage";
    wall.appendChild(stage);
    return stage;
  }

  function wallpaperLayout() {
    return {
      fit: state.wallpaperFit || "cover",
      scale: clamp(state.wallpaperScale, 70, 160, 100),
      x: clamp(state.wallpaperX, 0, 100, 50),
      y: clamp(state.wallpaperY, 0, 100, 50),
      quality: clamp(state.webQuality, 100, 200, 125)
    };
  }

  function applyWallpaperLayout() {
    var wall = wallpaperHost();
    var layout = wallpaperLayout();
    wall.style.setProperty("--sf-wall-fit", layout.fit === "fill" ? "100% 100%" : layout.fit);
    wall.style.setProperty("--sf-wall-scale", layout.scale + "%");
    wall.style.setProperty("--sf-wall-x", layout.x + "%");
    wall.style.setProperty("--sf-wall-y", layout.y + "%");
    var video = wall.querySelector("video");
    if (video) {
      video.style.objectFit = layout.fit;
      video.style.objectPosition = layout.x + "% " + layout.y + "%";
    }
    var frame = wall.querySelector("iframe");
    if (frame) {
      var quality = frame.dataset.wallpaperType === "web" ? layout.quality / 100 : 1;
      frame.style.width = (quality * 100) + "%";
      frame.style.height = (quality * 100) + "%";
      frame.style.transform = "scale(" + (1 / quality) + ")";
      try { frame.contentWindow.postMessage({ type: "spotifly:layout", fit: layout.fit, x: layout.x, y: layout.y }, "*"); } catch (e) {}
    }
  }

  function wallpaperVideoShouldPlay() {
    return !!state.wallpaper &&
      state.preset !== "original" &&
      !document.hidden &&
      document.visibilityState !== "hidden";
  }

  function pauseWallpaperVideo() {
    var video = document.querySelector("#sf-wallpaper video");
    if (!video) return;
    try { video.pause(); } catch (e) {}
  }

  function suspendWallpaperRuntime() {
    pauseWallpaperVideo();
    var frame = document.querySelector("#sf-wallpaper iframe");
    if (frame && frame.contentWindow) {
      try { frame.contentWindow.postMessage({ type: "spotifly:visibility", visible: false }, "*"); } catch (e) {}
    }
    syncWallpaperHostActivity(false);
  }

  function syncWallpaperVideoPlayback() {
    var video = document.querySelector("#sf-wallpaper video");
    var shouldPlay = wallpaperVideoShouldPlay();
    if (video) {
      video.muted = true;
      video.defaultMuted = true;
      video.volume = 0;
      if (!shouldPlay) pauseWallpaperVideo();
      else video.play()["catch"](function () {});
    }
    var frame = document.querySelector("#sf-wallpaper iframe");
    if (frame && frame.contentWindow) {
      try { frame.contentWindow.postMessage({ type: "spotifly:visibility", visible: shouldPlay }, "*"); } catch (e) {}
      var layout = wallpaperLayout();
      try { frame.contentWindow.postMessage({ type: "spotifly:layout", fit: layout.fit, x: layout.x, y: layout.y }, "*"); } catch (e2) {}
    }
    syncWallpaperHostActivity(shouldPlay);
  }

  function hostRequest(path, options) {
    options = options || {};
    var headers = options.headers || {};
    headers["X-Spotifly-Token"] = WALLPAPER_TOKEN;
    options.headers = headers;
    return fetch(WALLPAPER_HOST + path, options);
  }

  function syncWallpaperHostActivity(visible) {
    if (!state.engineWallpaper) return;
    hostRequest("/api/activity", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        visible: !!visible,
        sceneId: state.engineWallpaper && state.engineWallpaper.type === "scene" ? state.engineWallpaper.id : null
      }),
      keepalive: true
    })["catch"](function () {});
  }

  function mountWallpaperVideo(url) {
    var root = document.documentElement;
    var wall = wallpaperHost();
    var stage = wallpaperStage();
    root.style.removeProperty("--sf-wall");
    wall.style.removeProperty("background-image");
    var video = document.createElement("video");
    video.setAttribute("muted", "");
    video.setAttribute("loop", "");
    video.setAttribute("playsinline", "");
    video.setAttribute("preload", "metadata");
    video.muted = true;
    video.defaultMuted = true;
    video.loop = true;
    video.autoplay = false;
    video.playsInline = true;
    video.controls = false;
    video.volume = 0;
    video.addEventListener("volumechange", function () {
      if (!video.muted || video.volume) {
        video.muted = true;
        video.volume = 0;
      }
    });
    video.addEventListener("error", function () {
      try { video.remove(); } catch (e) {}
    }, { once: true });
    stage.appendChild(video);
    applyWallpaperLayout();
    video.src = url;
    if (video.readyState >= 2) syncWallpaperVideoPlayback();
    else video.addEventListener("canplay", syncWallpaperVideoPlayback, { once: true });
  }

  function applyWallpaperUrl(url) {
    if (wallUrl && wallUrl.indexOf("blob:") === 0) URL.revokeObjectURL(wallUrl);
    wallUrl = url || "";
    var root = document.documentElement;
    stopWallpaperVideo();
    var wall = document.getElementById("sf-wallpaper");
    if (wall) wall.style.removeProperty("background-image");
    root.style.removeProperty("--sf-wall");
    if (url) {
      var stage = wallpaperStage();
      var image = document.createElement("div");
      image.className = "sf-wallpaper-image";
      image.style.backgroundImage = "url(\"" + url + "\")";
      stage.appendChild(image);
      applyWallpaperLayout();
    }
  }

  function applyWallpaperBlob(blob) {
    if (!blob) {
      applyWallpaperUrl("");
      return;
    }
    if (wallUrl && wallUrl.indexOf("blob:") === 0) URL.revokeObjectURL(wallUrl);
    wallUrl = URL.createObjectURL(blob);
    var root = document.documentElement;
    var wall = wallpaperHost();
    stopWallpaperVideo();
    if (isVideoBlob(blob)) {
      mountWallpaperVideo(wallUrl);
    } else {
      root.style.removeProperty("--sf-wall");
      var stage = wallpaperStage();
      var image = document.createElement("div");
      image.className = "sf-wallpaper-image";
      image.style.backgroundImage = "url(\"" + wallUrl + "\")";
      stage.appendChild(image);
      applyWallpaperLayout();
    }
  }

  function applyEngineWallpaper(project) {
    if (!project || !project.entry) return;
    if (wallUrl && wallUrl.indexOf("blob:") === 0) URL.revokeObjectURL(wallUrl);
    wallUrl = project.entry;
    stopWallpaperVideo();
    var root = document.documentElement;
    var wall = wallpaperHost();
    root.style.removeProperty("--sf-wall");
    wall.style.removeProperty("background-image");
    if (project.type === "video" || project.type === "web" || project.type === "scene") {
      var stage = wallpaperStage();
      var frame = document.createElement("iframe");
      frame.src = project.entry;
      frame.dataset.wallpaperType = project.type;
      frame.title = project.title || "Wallpaper Engine";
      frame.tabIndex = -1;
      frame.setAttribute("aria-hidden", "true");
      frame.setAttribute("sandbox", "allow-scripts allow-same-origin allow-forms");
      frame.addEventListener("load", function () { applyWallpaperLayout(); syncWallpaperVideoPlayback(); });
      frame.addEventListener("error", function () { try { frame.remove(); } catch (e) {} }, { once: true });
      stage.appendChild(frame);
    }
    applyWallpaperLayout();
    syncWallpaperVideoPlayback();
  }

  function loadWallpaper() {
    if (state.engineWallpaper && state.engineWallpaper.entry) {
      applyEngineWallpaper(state.engineWallpaper);
      return;
    }
    idbGet().then(function (blob) {
      if (blob) applyWallpaperBlob(blob);
    });
  }

  function openWallpaperBrowser() {
    var modal = document.getElementById("sf-we-modal");
    if (!modal) return;
    modal.classList.add("is-open");
    modal.setAttribute("aria-hidden", "false");
    if (window.__spotiflyWallpaperProjects) { renderWallpaperProjects(); scheduleWallpaperDownloadPoll(); }
    else loadWallpaperProjects(false);
  }

  function closeWallpaperBrowser() {
    var modal = document.getElementById("sf-we-modal");
    if (!modal) return;
    modal.classList.remove("is-open");
    modal.setAttribute("aria-hidden", "true");
    if (wallpaperDownloadTimer) {
      window.clearTimeout(wallpaperDownloadTimer);
      wallpaperDownloadTimer = 0;
    }
    window.setTimeout(function () {
      var grid = document.getElementById("sf-we-grid");
      if (grid && !modal.classList.contains("is-open")) grid.innerHTML = "";
    }, 240);
  }

  function loadWallpaperProjects(refresh) {
    var grid = document.getElementById("sf-we-grid");
    var status = document.getElementById("sf-we-status");
    if (!grid || !status) return;
    status.textContent = "Ищу обои в библиотеках Steam…";
    grid.innerHTML = "";
    hostRequest("/api/projects" + (refresh ? "?refresh=1" : ""))
      .then(function (response) {
        if (!response.ok) throw new Error("host");
        return response.json();
      })
      .then(function (data) {
        window.__spotiflyWallpaperProjects = data.projects || [];
        renderWallpaperProjects();
        scheduleWallpaperDownloadPoll();
      })
      ["catch"](function () {
        status.textContent = "Wallpaper Host не запущен. Открой Spotifly через обычный ярлык приложения.";
      });
  }

  function formatBytes(value) {
    value = Number(value) || 0;
    if (!value) return "0 МБ";
    var units = ["Б", "КБ", "МБ", "ГБ"];
    var unit = Math.min(units.length - 1, Math.floor(Math.log(value) / Math.log(1024)));
    return (value / Math.pow(1024, unit)).toFixed(unit > 1 ? 1 : 0) + " " + units[unit];
  }

  function scheduleWallpaperDownloadPoll() {
    if (wallpaperDownloadTimer) window.clearTimeout(wallpaperDownloadTimer);
    var modal = document.getElementById("sf-we-modal");
    if (!modal || !modal.classList.contains("is-open")) return;
    var projects = window.__spotiflyWallpaperProjects || [];
    var hasActive = projects.some(function (project) {
      return project.downloadState === "downloading" || project.downloadState === "pending";
    });
    if (!hasActive && !wallpaperDownloadId) return;
    wallpaperDownloadTimer = window.setTimeout(function () {
      hostRequest("/api/projects")
        .then(function (response) { if (!response.ok) throw new Error("host"); return response.json(); })
        .then(function (data) {
          window.__spotiflyWallpaperProjects = data.projects || [];
          if (wallpaperDownloadId) {
            var active = window.__spotiflyWallpaperProjects.find(function (project) { return project.id === wallpaperDownloadId; });
            if (!active || active.supported || active.downloadState === "cancelled" || active.downloadState === "unavailable" ||
                (Date.now() - wallpaperDownloadStarted > 120000 && active.downloadState === "subscribed")) {
              wallpaperDownloadId = "";
              wallpaperDownloadStarted = 0;
            }
          }
          renderWallpaperProjects();
          scheduleWallpaperDownloadPoll();
        })["catch"](scheduleWallpaperDownloadPoll);
    }, 800);
  }

  function changeWallpaperDownload(project, cancel) {
    if (cancel) {
      wallpaperDownloadId = "";
      wallpaperDownloadStarted = 0;
    } else {
      wallpaperDownloadId = project.id;
      wallpaperDownloadStarted = Date.now();
    }
    project.downloadState = cancel ? "cancelled" : "pending";
    renderWallpaperProjects();
    hostRequest("/api/download/" + encodeURIComponent(project.id) + (cancel ? "?cancel=1" : ""), { method: "POST" })
      .then(function (response) { if (!response.ok) throw new Error("download"); return response.json(); })
      .then(function (result) {
        if (!result.accepted) {
          project.downloadState = "error";
          wallpaperDownloadId = "";
          wallpaperDownloadStarted = 0;
        }
        renderWallpaperProjects();
        scheduleWallpaperDownloadPoll();
      })
      ["catch"](function () {
        project.downloadState = "error";
        wallpaperDownloadId = "";
        wallpaperDownloadStarted = 0;
        renderWallpaperProjects();
      });
  }

  function renderWallpaperProjects() {
    var projects = window.__spotiflyWallpaperProjects || [];
    var grid = document.getElementById("sf-we-grid");
    var status = document.getElementById("sf-we-status");
    var search = document.getElementById("sf-we-search");
    var allToggle = document.getElementById("sf-we-all");
    if (!grid || !status) return;
    var query = String(search && search.value || "").trim().toLowerCase();
    var showAll = !!(allToggle && allToggle.checked);
    var visible = projects.filter(function (project) {
      if (!showAll && !project.supported && !project.downloadable) return false;
      return !query || String(project.title || "").toLowerCase().indexOf(query) >= 0;
    });
    grid.innerHTML = "";
    visible.forEach(function (project) {
      var card = document.createElement("article");
      card.className = "sf-we-card" + (project.supported ? "" : " is-disabled") +
        (project.downloadable ? " is-downloadable" : "") +
        (project.downloadState === "downloading" || project.downloadState === "pending" ? " is-downloading" : "") +
        (state.engineWallpaper && state.engineWallpaper.id === project.id ? " is-selected" : "");
      var media = document.createElement("span");
      media.className = "sf-we-preview";
      if (project.preview) {
        var image = document.createElement("img");
        image.src = project.preview;
        image.alt = "";
        image.loading = "lazy";
        image.decoding = "async";
        media.appendChild(image);
      }
      var type = document.createElement("span");
      type.className = "sf-we-type";
      type.textContent = project.type === "web" ? (project.audio ? "WEB · AUDIO" : "WEB") :
        project.type === "video" ? "VIDEO" : project.type === "scene" ? "SCENE" :
        project.downloadState === "downloading" ? "ЗАГРУЗКА" : project.downloadState === "pending" ? "ОЖИДАНИЕ" :
        project.downloadState === "unavailable" ? "НЕДОСТУПНО В STEAM" : "В ОБЛАКЕ";
      media.appendChild(type);
      var title = document.createElement("span");
      title.className = "sf-we-title";
      title.textContent = project.title || ("Wallpaper " + project.id);
      card.appendChild(media);
      card.appendChild(title);
      if (project.supported) {
        card.tabIndex = 0;
        card.setAttribute("role", "button");
        card.addEventListener("click", function () {
          state.engineWallpaper = {
            id: project.id,
            title: project.title,
            type: project.type,
            entry: project.entry
          };
          state.wallpaper = true;
          save();
          applyEngineWallpaper(state.engineWallpaper);
          applyPalette();
          var toggle = document.getElementById("sf-wall-on");
          if (toggle) toggle.checked = true;
          closeWallpaperBrowser();
        });
        card.addEventListener("keydown", function (event) {
          if (event.key === "Enter" || event.key === " ") { event.preventDefault(); card.click(); }
        });
      } else if (project.downloadable) {
        var download = document.createElement("div");
        download.className = "sf-we-download";
        var total = Number(project.total) || 0;
        var downloaded = Number(project.downloaded) || 0;
        var percent = total > 0 ? Math.max(0, Math.min(100, downloaded / total * 100)) : 0;
        var progress = document.createElement("span");
        progress.className = "sf-we-progress";
        progress.innerHTML = '<i style="width:' + percent.toFixed(2) + '%"></i>';
        var detail = document.createElement("small");
        detail.textContent = project.downloadState === "cancelled" ? "Остановлено" :
          project.downloadState === "error" ? "Не удалось запустить" :
          (project.downloadState === "downloading" || project.downloadState === "pending") ?
            (formatBytes(downloaded) + " / " + formatBytes(total)) : formatBytes(total);
        var action = document.createElement("button");
        action.type = "button";
        var activeDownload = project.downloadState === "downloading" || project.downloadState === "pending";
        var anotherDownload = !!wallpaperDownloadId && wallpaperDownloadId !== project.id;
        action.disabled = anotherDownload;
        action.textContent = activeDownload ? "Отменить" : anotherDownload ? "Ожидает" : project.downloadState === "cancelled" ? "Продолжить" : "Скачать";
        action.addEventListener("click", function (event) { event.stopPropagation(); changeWallpaperDownload(project, activeDownload); });
        download.appendChild(progress);
        download.appendChild(detail);
        download.appendChild(action);
        card.appendChild(download);
      }
      grid.appendChild(card);
    });
    var supportedCount = projects.filter(function (project) { return project.supported; }).length;
    var cloudCount = projects.filter(function (project) { return project.downloadable; }).length;
    status.textContent = visible.length ?
      ("Найдено: " + projects.length + " · установлено: " + supportedCount + (cloudCount ? " · в облаке: " + cloudCount : "") + " · показано: " + visible.length) :
      (projects.length ? "Ничего не найдено" : "Локальные проекты Wallpaper Engine не найдены");
  }

  function mountWallpaperBrowser() {
    if (document.getElementById("sf-we-modal")) return;
    var modal = document.createElement("div");
    modal.id = "sf-we-modal";
    modal.setAttribute("aria-hidden", "true");
    modal.innerHTML =
      '<div class="sf-we-backdrop"></div>' +
      '<section class="sf-we-dialog" role="dialog" aria-modal="true" aria-label="Обои Wallpaper Engine">' +
      '<header><div><strong>Wallpaper Engine</strong><small>Локальная библиотека Workshop</small></div><button type="button" id="sf-we-close" aria-label="Закрыть">×</button></header>' +
      '<div class="sf-we-tools"><input id="sf-we-search" type="search" placeholder="Поиск по названию"><label><input id="sf-we-all" type="checkbox"> Показать повреждённые</label><button type="button" id="sf-we-refresh">Обновить</button></div>' +
      '<p id="sf-we-status"></p><div id="sf-we-grid"></div>' +
      '<footer>Video, Web и Scene работают внутри Spotifly без собственного звука. Scene используют установленный Wallpaper Engine.</footer>' +
      '</section>';
    document.body.appendChild(modal);
    modal.querySelector(".sf-we-backdrop").addEventListener("click", closeWallpaperBrowser);
    modal.querySelector("#sf-we-close").addEventListener("click", closeWallpaperBrowser);
    modal.querySelector("#sf-we-refresh").addEventListener("click", function () { loadWallpaperProjects(true); });
    modal.querySelector("#sf-we-search").addEventListener("input", renderWallpaperProjects);
    modal.querySelector("#sf-we-all").addEventListener("change", renderWallpaperProjects);
    document.addEventListener("keydown", function (event) {
      if (event.key === "Escape" && modal.classList.contains("is-open")) closeWallpaperBrowser();
    });
  }

  function restoreNativeWindowButtons() {
    try {
      var fake = document.getElementById("sf-win");
      if (fake) fake.remove();
      var fn = window.executeEsperantoCall;
      if (typeof fn !== "function") return;
      var enc = new TextEncoder();
      var service = enc.encode("spotify.desktop.update_ui_esperanto.proto.DesktopUpdateUi");
      var method = enc.encode("SetButtonsVisibility");
      var payload = new Uint8Array([8, 1]);
      var out = new Uint8Array(12 + service.byteLength + method.byteLength + payload.byteLength);
      var view = new DataView(out.buffer);
      var off = 0;
      function put(bytes) {
        view.setInt32(off, bytes.byteLength);
        out.set(bytes, off + 4);
        off += bytes.byteLength + 4;
      }
      put(service);
      put(method);
      put(payload);
      fn({ request: out.buffer, persistent: false, onSuccess: function () {}, onFailure: function () {} });
    } catch (e) {}
  }

  var overlayObs = null;
  function watchOverlays() {
    if (overlayObs) return;
    overlayObs = new ResizeObserver(function (entries) {
      var i;
      for (i = 0; i < entries.length; i++) {
        var el = entries[i].target;
        if (!el.classList.contains("WBFaUw_oOfN2m4aTxggt")) continue;
        el.classList.toggle("sf-overlaying", entries[i].contentRect.width >= 500);
      }
    });
    function attach() {
      var lib = document.querySelector(".WBFaUw_oOfN2m4aTxggt");
      if (lib) overlayObs.observe(lib);
    }
    attach();
    [800, 2500].forEach(function (ms) { window.setTimeout(attach, ms); });
  }

  function syncPawToHome() {
    var home = document.querySelector('[data-testid="home-button"]');
    var paw = document.getElementById("sf-open");
    if (!home || !paw) return;
    var cs = window.getComputedStyle(home);
    paw.style.setProperty("width", cs.width, "important");
    paw.style.setProperty("height", cs.height, "important");
    paw.style.setProperty("min-width", cs.width, "important");
    paw.style.setProperty("min-height", cs.height, "important");
    paw.style.setProperty("border-radius", cs.borderRadius || "50%", "important");
    paw.style.setProperty("background-color", cs.backgroundColor, "important");
    var root = document.getElementById("sf-root");
    if (root) {
      root.style.width = cs.width;
      root.style.height = cs.height;
    }
  }

  function applyPalette() {
    var root = document.documentElement;
    var original = state.preset === "original";
    state.tint = clamp(state.tint, 8, 70, 28);
    state.blur = clamp(state.blur, 0, 32, 6);
    root.classList.add("sf-theme");
    root.classList.toggle("sf-original", original);
    root.classList.toggle("sf-wallpaper", !!state.wallpaper && !original);
    root.style.setProperty("--sf-tint", state.tint + "%");
    root.style.setProperty("--sf-blur", state.blur + "px");
    root.style.setProperty("--panel-gap", "8px");
    if (original) return;
    var b = state.blocks;
    ["library", "main", "nowplaying", "player", "accent"].forEach(function (k) {
      root.style.setProperty("--sf-" + k, b[k]);
    });
    if (state.textAuto) {
      state.text = inkFor(b.main);
      state.textMuted = mutedFor(b.main);
      state.textPlayer = inkFor(b.player) === "#3a241c" ? "#3a241c" : "#f6ebdf";
    }
    root.style.setProperty("--sf-ink", state.text);
    root.style.setProperty("--sf-muted", state.textMuted);
    root.style.setProperty("--sf-paper", state.textPlayer);
    root.style.setProperty("--sf-muted-dark", mutedFor(b.player));
    window.requestAnimationFrame(syncPawToHome);
  }

  function patchTitle() {
    try {
      var desc = Object.getOwnPropertyDescriptor(Document.prototype, "title");
      if (!desc || !desc.set || desc.set._sf) return;
      var orig = desc.set;
      var wrapped = function (value) {
        orig.call(this, String(value || "").replace(/Spotify/g, "Spotifly").replace(/Spotishka/g, "Spotifly"));
      };
      wrapped._sf = true;
      Object.defineProperty(document, "title", { configurable: true, get: desc.get, set: wrapped });
      document.title = document.title;
    } catch (e) {}
  }

  function playerBar() {
    return document.querySelector(".f9pLH3HRZQxdDLzNqKjE") ||
      document.querySelector('[data-testid="now-playing-bar"]');
  }

  function ensureWave() {
    var fx = document.getElementById("sf-player-fx");
    if (fx) return fx;
    fx = document.createElement("div");
    fx.id = "sf-player-fx";
    fx.innerHTML = '<div class="sf-wave"></div>';
    document.body.appendChild(fx);
    return fx;
  }

  function slimePlayer(dir) {
    if (!state.jelly || reduced || state.preset === "original") return;
    window.requestAnimationFrame(function () {
      var bar = playerBar();
      if (!bar) return;
      bar.classList.remove("sf-player-next", "sf-player-prev", "sf-player-pause");
      void bar.offsetWidth;
      bar.classList.add(dir === "next" ? "sf-player-next" : dir === "prev" ? "sf-player-prev" : "sf-player-pause");
      window.setTimeout(function () {
        bar.classList.remove("sf-player-next", "sf-player-prev", "sf-player-pause");
      }, 560);

      if (dir === "pause") return;
      var fx = ensureWave();
      var r = bar.getBoundingClientRect();
      var radius = window.getComputedStyle(bar).borderRadius;
      fx.style.top = r.top + "px";
      fx.style.left = r.left + "px";
      fx.style.width = r.width + "px";
      fx.style.height = r.height + "px";
      fx.style.borderRadius = radius;
      fx.classList.remove("is-next", "is-prev");
      void fx.offsetWidth;
      fx.classList.add(dir === "next" ? "is-next" : "is-prev");
    });
  }

  function jellyButton(btn) {
    if (!state.jelly || reduced) return;
    btn.classList.remove("sf-jelly-btn");
    void btn.offsetWidth;
    btn.classList.add("sf-jelly-btn");
    window.setTimeout(function () { btn.classList.remove("sf-jelly-btn"); }, 650);
  }

  function inPlayer(el) {
    return !!(el && el.closest && el.closest('[data-testid="now-playing-bar"], [data-testid="player-controls"], .f9pLH3HRZQxdDLzNqKjE'));
  }

  function isPlayerControl(btn) {
    if (!inPlayer(btn)) return "";
    if (btn.closest('[data-testid="control-button-skip-forward"]')) return "next";
    if (btn.closest('[data-testid="control-button-skip-back"]')) return "prev";
    if (btn.closest('[data-testid="control-button-playpause"]')) return "pause";
    return "";
  }

  function onPointerDown(event) {
    if (!state.jelly || reduced || event.button !== 0) return;
    var t = event.target;
    if (!t || !t.closest || t.closest("#sf-root") || t.closest("#sf-panel") || t.closest("#sf-we-modal")) return;
    var btn = t.closest("button, [role='button']");
    if (!btn || btn.closest('[role="slider"]') || btn.tagName === "INPUT") return;
    if (isPlayerControl(btn)) return;
    jellyButton(btn);
  }

  function onClick(event) {
    if (!state.jelly || reduced || event.button) return;
    var t = event.target;
    if (!t || !t.closest) return;
    var btn = t.closest("button, [role='button']");
    if (!btn) return;
    var dir = isPlayerControl(btn);
    if (dir) slimePlayer(dir);
  }

  function onKeyDown(event) {
    if (event.code === "MediaTrackNext") slimePlayer("next");
    if (event.code === "MediaTrackPrevious") slimePlayer("prev");
    if (event.code === "MediaPlayPause") slimePlayer("pause");
  }

  function findHomeSlot() {
    var home = document.querySelector('[data-testid="home-button"]');
    if (!home) return null;
    var node = home;
    while (node.parentElement) {
      var parent = node.parentElement;
      if (parent.querySelector("input") && parent.querySelector('[data-testid="home-button"]')) {
        return { parent: parent, before: node };
      }
      node = parent;
    }
    return { parent: home.parentElement, before: home };
  }

  function placePanel() {
    var panel = document.getElementById("sf-panel");
    var btn = document.getElementById("sf-open");
    if (!panel || !btn || !panel.classList.contains("is-open")) return;
    var r = btn.getBoundingClientRect();
    var left = Math.max(8, Math.min(r.left, window.innerWidth - 316));
    panel.style.top = Math.round(r.bottom + 10) + "px";
    panel.style.left = Math.round(left) + "px";
  }

  function pinCustomizer() {
    var root = document.getElementById("sf-root");
    if (!root) return;
    var slot = findHomeSlot();
    if (!slot || !slot.parent) {
      root.classList.add("sf-floating");
      return;
    }
    root.classList.remove("sf-floating");
    if (root.parentElement !== slot.parent || root.nextElementSibling !== slot.before) {
      slot.parent.insertBefore(root, slot.before);
    }
    syncPawToHome();
    placePanel();
  }

  function schedulePin() {
    if (pinTimer) return;
    pinTimer = window.requestAnimationFrame(function () {
      pinTimer = 0;
      pinCustomizer();
    });
  }

  function watchNav() {
    var navObs = new MutationObserver(schedulePin);
    var attached = null;
    function attach() {
      var nav = document.getElementById("global-nav-bar") || document.querySelector('[data-testid="global-nav-bar"]');
      if (nav && nav !== attached) {
        if (attached) navObs.disconnect();
        attached = nav;
        navObs.observe(nav, { childList: true, subtree: true });
        pinCustomizer();
      }
    }
    var boot = new MutationObserver(attach);
    boot.observe(document.documentElement, { childList: true, subtree: true });
    attach();
    [200, 600, 1400, 3000].forEach(function (ms) {
      window.setTimeout(attach, ms);
    });
    window.setTimeout(function () { boot.disconnect(); }, 8000);
  }

  function mountUi() {
    try {
    if (!document.getElementById("sf-wallpaper")) {
      var wall = document.createElement("div");
      wall.id = "sf-wallpaper";
      document.body.insertBefore(wall, document.body.firstChild);
    }
    if (document.getElementById("sf-root")) {
      pinCustomizer();
      return;
    }
    var root = document.createElement("div");
    root.id = "sf-root";
    root.innerHTML =
      '<button type="button" class="sf-btn YEAFPNm87XbzS4sF5dDe rC9xwL4gaksmshIjHbNn" id="sf-open" aria-label="Тема Spotifly">' +
      '<img class="sf-paw" src="/spotifly/paw.png" alt="" draggable="false">' +
      "</button>";
    var panel = document.createElement("div");
    panel.id = "sf-panel";
    panel.innerHTML =
      "<h3>Spotifly</h3>" +
      '<div class="sf-presets" id="sf-presets"></div>' +
      row("library", "Медиатека") +
      row("main", "Главная") +
      row("nowplaying", "Сейчас играет") +
      row("player", "Плеер") +
      row("accent", "Акцент") +
      '<label class="sf-check"><span>Автоцвет текста</span><input id="sf-text-auto" type="checkbox"' + (state.textAuto ? " checked" : "") + "></label>" +
      '<label class="sf-row">Текст<input id="sf-text" type="color" value="' + state.text + '"></label>' +
      '<label class="sf-row">Приглушённый<input id="sf-text-muted" type="color" value="' + state.textMuted + '"></label>' +
      '<label class="sf-row">Текст плеера<input id="sf-text-player" type="color" value="' + state.textPlayer + '"></label>' +
      '<label class="sf-check"><span>Гармония</span><input id="sf-harmony" type="checkbox"' + (state.harmony ? " checked" : "") + "></label>" +
      '<label class="sf-check"><span>Желе плеера</span><input id="sf-jelly" type="checkbox"' + (state.jelly ? " checked" : "") + "></label>" +
      '<label class="sf-check"><span>Обои</span><input id="sf-wall-on" type="checkbox"' + (state.wallpaper ? " checked" : "") + "></label>" +
      '<label class="sf-row"><span>Тонировка</span><input id="sf-tint" type="range" min="8" max="70" value="' + state.tint + '"></label>' +
      '<label class="sf-row"><span>Блюр</span><input id="sf-blur" type="range" min="0" max="32" value="' + state.blur + '"></label>' +
      '<details class="sf-wall-layout"><summary>Положение обоев</summary>' +
      '<label class="sf-row"><span>Заполнение</span><select id="sf-wall-fit"><option value="cover">Заполнить</option><option value="contain">Вместить</option><option value="fill">Растянуть</option></select></label>' +
      '<label class="sf-row"><span>Масштаб</span><input id="sf-wall-scale" type="range" min="70" max="160" value="' + state.wallpaperScale + '"></label>' +
      '<label class="sf-row"><span>По горизонтали</span><input id="sf-wall-x" type="range" min="0" max="100" value="' + state.wallpaperX + '"></label>' +
      '<label class="sf-row"><span>По вертикали</span><input id="sf-wall-y" type="range" min="0" max="100" value="' + state.wallpaperY + '"></label>' +
      '<label class="sf-row"><span>Качество WEB</span><select id="sf-web-quality"><option value="100">Обычное</option><option value="125">Высокое</option><option value="150">Очень высокое</option><option value="200">Максимум</option></select></label>' +
      '</details>' +
      '<button type="button" class="sf-we-open" id="sf-we-open">Выбрать из Wallpaper Engine</button>' +
      '<div class="sf-actions">' +
      '<label>Фото / видео<input id="sf-file" type="file" accept="image/*,video/mp4,video/webm,video/quicktime,.gif,.mp4,.webm,.mov" hidden></label>' +
      '<button type="button" id="sf-wall-reset">Сброс обоев</button>' +
      "</div>";
    document.body.appendChild(root);
    document.body.appendChild(panel);
    mountWallpaperBrowser();
    renderPresets();
    syncColors();
    pinCustomizer();

    root.querySelector("#sf-open").addEventListener("click", function (e) {
      e.stopPropagation();
      panel.classList.toggle("is-open");
      placePanel();
    });
    panel.addEventListener("click", function (e) { e.stopPropagation(); });
    panel.querySelector("#sf-harmony").addEventListener("change", function () { state.harmony = this.checked; save(); });
    panel.querySelector("#sf-jelly").addEventListener("change", function () { state.jelly = this.checked; save(); });
    panel.querySelector("#sf-text-auto").addEventListener("change", function () {
      state.textAuto = this.checked;
      save();
      applyPalette();
      syncColors();
    });
    panel.querySelector("#sf-text").addEventListener("input", function () {
      state.textAuto = false;
      state.text = this.value;
      panel.querySelector("#sf-text-auto").checked = false;
      save();
      applyPalette();
    });
    panel.querySelector("#sf-text-muted").addEventListener("input", function () {
      state.textAuto = false;
      state.textMuted = this.value;
      panel.querySelector("#sf-text-auto").checked = false;
      save();
      applyPalette();
    });
    panel.querySelector("#sf-text-player").addEventListener("input", function () {
      state.textAuto = false;
      state.textPlayer = this.value;
      panel.querySelector("#sf-text-auto").checked = false;
      save();
      applyPalette();
    });
    panel.querySelector("#sf-wall-on").addEventListener("change", function () {
      state.wallpaper = this.checked;
      save();
      applyPalette();
      syncWallpaperVideoPlayback();
    });
    panel.querySelector("#sf-tint").addEventListener("input", function () {
      state.tint = clamp(this.value, 8, 70, 28);
      save();
      applyPalette();
    });
    panel.querySelector("#sf-blur").addEventListener("input", function () {
      state.blur = clamp(this.value, 0, 32, 6);
      save();
      applyPalette();
    });
    panel.querySelector("#sf-wall-fit").value = state.wallpaperFit;
    panel.querySelector("#sf-web-quality").value = String(state.webQuality);
    ["fit", "scale", "x", "y", "quality"].forEach(function (key) {
      var ids = { fit: "#sf-wall-fit", scale: "#sf-wall-scale", x: "#sf-wall-x", y: "#sf-wall-y", quality: "#sf-web-quality" };
      var eventName = key === "fit" || key === "quality" ? "change" : "input";
      panel.querySelector(ids[key]).addEventListener(eventName, function () {
        if (key === "fit") state.wallpaperFit = this.value;
        if (key === "scale") state.wallpaperScale = clamp(this.value, 70, 160, 100);
        if (key === "x") state.wallpaperX = clamp(this.value, 0, 100, 50);
        if (key === "y") state.wallpaperY = clamp(this.value, 0, 100, 50);
        if (key === "quality") state.webQuality = clamp(this.value, 100, 200, 125);
        save();
        applyWallpaperLayout();
      });
    });
    panel.querySelector("#sf-we-open").addEventListener("click", openWallpaperBrowser);
    panel.querySelector("#sf-file").addEventListener("change", function () {
      var file = this.files && this.files[0];
      if (!file) return;
      idbSet(file).then(function () {
        syncWallpaperHostActivity(false);
        state.engineWallpaper = null;
        applyWallpaperBlob(file);
        state.wallpaper = true;
        save();
        applyPalette();
        panel.querySelector("#sf-wall-on").checked = true;
      });
    });
    panel.querySelector("#sf-wall-reset").addEventListener("click", function () {
      idbClear().then(function () {
        syncWallpaperHostActivity(false);
        state.engineWallpaper = null;
        save();
        applyWallpaperUrl("");
      });
    });
    panel.querySelectorAll("input[data-block]").forEach(function (input) {
      input.addEventListener("input", function () {
        var key = input.getAttribute("data-block");
        if (state.harmony && key !== "accent") {
          state.blocks = paletteFromKey(input.value);
          state.blocks[key] = input.value;
          if (key === "nowplaying") state.blocks.player = input.value;
          if (key === "player") state.blocks.nowplaying = input.value;
        } else {
          state.blocks[key] = input.value;
        }
        state.preset = "custom";
        save();
        applyPalette();
        syncColors();
        renderPresets();
      });
    });
    document.addEventListener("click", function (e) {
      if (panel.classList.contains("is-open") && !panel.contains(e.target) && !root.contains(e.target)) {
        panel.classList.remove("is-open");
      }
    });
    window.addEventListener("resize", placePanel);
    document.addEventListener("visibilitychange", function () {
      syncWallpaperVideoPlayback();
    });
    window.addEventListener("pagehide", suspendWallpaperRuntime);
    window.addEventListener("pageshow", syncWallpaperVideoPlayback);
    document.addEventListener("freeze", suspendWallpaperRuntime);
    document.addEventListener("resume", syncWallpaperVideoPlayback);
    restoreNativeWindowButtons();
    syncPawToHome();
    [400, 1600].forEach(function (ms) {
      window.setTimeout(function () {
        restoreNativeWindowButtons();
        syncPawToHome();
      }, ms);
    });
    watchNav();
    watchOverlays();
    } catch (e) {}
  }

  function row(key, label) {
    return '<label class="sf-row">' + label + '<input type="color" data-block="' + key + '" value="' + (state.blocks[key] || "#000000") + '"></label>';
  }

  function renderPresets() {
    var host = document.getElementById("sf-presets");
    if (!host) return;
    host.innerHTML = "";
    Object.keys(PRESETS).forEach(function (name) {
      var p = PRESETS[name];
      var btn = document.createElement("button");
      btn.type = "button";
      btn.className = "sf-preset" + (state.preset === name ? " is-active" : "");
      btn.title = name;
      btn.innerHTML = "<span style='background:" + p.library + "'></span><span style='background:" + p.main + "'></span><span style='background:" + p.nowplaying + "'></span><span style='background:" + p.accent + "'></span>";
      btn.addEventListener("click", function () {
        state.preset = name;
        state.blocks = Object.assign({}, p);
        save();
        applyPalette();
        syncWallpaperVideoPlayback();
        syncColors();
        renderPresets();
      });
      host.appendChild(btn);
    });
  }

  function syncColors() {
    document.querySelectorAll("#sf-panel input[data-block]").forEach(function (input) {
      var key = input.getAttribute("data-block");
      if (state.blocks[key]) input.value = state.blocks[key];
    });
    var tint = document.getElementById("sf-tint");
    var blur = document.getElementById("sf-blur");
    var text = document.getElementById("sf-text");
    var muted = document.getElementById("sf-text-muted");
    var player = document.getElementById("sf-text-player");
    var auto = document.getElementById("sf-text-auto");
    if (tint) tint.value = String(state.tint);
    if (blur) blur.value = String(state.blur);
    if (text) text.value = state.text;
    if (muted) muted.value = state.textMuted;
    if (player) player.value = state.textPlayer;
    if (auto) auto.checked = !!state.textAuto;
  }

  try {
    applyPalette();
    save();
    patchTitle();
    loadWallpaper();
    if (document.body) mountUi();
    else document.addEventListener("DOMContentLoaded", function () { try { mountUi(); } catch (e) {} });
    document.addEventListener("pointerdown", onPointerDown, true);
    document.addEventListener("click", onClick, false);
    document.addEventListener("keydown", onKeyDown, true);
  } catch (e) {}
})();
