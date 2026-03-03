// Shared overlay client utilities
// initOverlayClient(section, handlers)
// handlers: { onStats: fn(data), onSettings: fn(data), onOpen?: fn(), onClose?: fn() }
(function (global) {
    function hasRealMetrics(arr) {
        if (!arr || !Array.isArray(arr)) return false;
        for (var i = 0; i < arr.length; i++) {
            var raw = arr[i];
            var v = String(raw || "").trim();
            if (!v) continue;
            if (v === "unknown") continue;
            v = v.replace(/\/h$/i, "");
            var numeric = v.replace(/[,_\s]/g, "");
            if (/^-?\d+(?:\.\d+)?$/.test(numeric)) {
                try {
                    var dv = parseFloat(numeric);
                    if (!isNaN(dv) && dv !== 0) return true;
                    else continue;
                } catch {
                    return true;
                }
            } else {
                if (/[0-9]/.test(numeric)) return true;
            }
        }
        return false;
    }

    function initOverlayClient(section, handlers) {
        handlers = handlers || {};
        var ws = null;
        var reconnectMs = 2000;
        // Debug query param removed. Always operate in non-debug mode in production overlays.
        var debug = false;
        // qp is an optional query-param helper that may not be present in all contexts (e.g., webview overlays).
        // Guard its usage to avoid ReferenceErrors when overlays are opened directly.
        var host =
            location.host ||
            (typeof qp !== "undefined" && qp && qp.get ? qp.get("host") : null);
        var port =
            typeof qp !== "undefined" && qp && qp.get ? qp.get("port") : null;
        if (!host && port) host = "127.0.0.1:" + port;
        if (!host) host = "127.0.0.1:8080";
        var protocol = location.protocol === "https:" ? "wss:" : "ws:";
        var wsUrl = protocol + "//" + host + "/ws";

        var closed = false;

        function connect() {
            try {
                ws = new WebSocket(wsUrl);
                global.overlayWs = ws;
            } catch (e) {
                // fallback
                ws = new WebSocket("ws://127.0.0.1:8080/ws");
                global.overlayWs = ws;
            }

            ws.onopen = function () {
                try {
                    ws.send(
                        JSON.stringify({ type: "register", section: section })
                    );
                } catch (e) {}
                if (debug) console.debug("[overlay-common] ws.onopen", wsUrl);
                if (handlers.onOpen) handlers.onOpen();
            };

            ws.onmessage = function (e) {
                try {
                    var msg = null;
                    if (typeof e.data === "string") msg = JSON.parse(e.data);
                    else msg = e.data;
                    if (debug)
                        console.debug("[overlay-common] ws.onmessage", msg);

                    // Standard wrapped message: { type: 'stats'|'damage'|'settings', data: ... }
                    if (
                        msg &&
                        (msg.type === "stats" ||
                            msg.type === "damage" ||
                            msg.type === "repair")
                    ) {
                        if (handlers.onStats) handlers.onStats(msg.data);
                    } else if (msg && msg.type === "settings") {
                        // Settings messages may be targeted to a specific section (e.g., 'dashboard' or 'damage').
                        // If msg.section is present, only forward to handlers for the matching registered section.
                        var targetSection = null;
                        try {
                            targetSection =
                                msg.section ||
                                (msg.data &&
                                    (msg.data.section ||
                                        msg.data.targetSection ||
                                        msg.data.scope));
                        } catch (e) {
                            targetSection = null;
                        }
                        if (
                            !targetSection ||
                            String(targetSection) === String(section)
                        ) {
                            if (handlers.onSettings)
                                handlers.onSettings(msg.data);
                        } else {
                            // ignore settings not intended for this section
                        }

                        // Tolerate servers that send the payload object directly
                    } else if (
                        msg &&
                        msg.metrics &&
                        Array.isArray(msg.metrics)
                    ) {
                        // If the server sends the raw metrics object (no wrapper), forward it
                        if (handlers.onStats) handlers.onStats(msg);

                        // Also tolerate older format where message is directly the array or string payload
                    } else if (msg && Array.isArray(msg)) {
                        if (handlers.onStats) handlers.onStats(msg);
                    } else if (
                        typeof msg === "string" &&
                        msg.indexOf("|") !== -1
                    ) {
                        // legacy pipe-delimited string
                        if (handlers.onStats) handlers.onStats(msg);
                    } else if (handlers.onMessage) {
                        handlers.onMessage(msg);
                    }
                } catch (err) {
                    if (debug)
                        console.error(
                            "[overlay-common] ws.onmessage parse error",
                            err,
                            e.data
                        );
                }
            };

            ws.onclose = function () {
                if (handlers.onClose) handlers.onClose();
                if (!closed) setTimeout(connect, reconnectMs);
            };

            ws.onerror = function () {
                try {
                    ws.close();
                } catch (e) {}
            };
        }

        connect();

        return {
            section: section,
            send: function (obj) {
                try {
                    if (ws && ws.readyState === 1) ws.send(JSON.stringify(obj));
                } catch (e) {}
            },
            close: function () {
                closed = true;
                try {
                    if (ws) ws.close();
                } catch (e) {}
            },
            hasRealMetrics: hasRealMetrics,
        };
    }

    global.initOverlayClient = initOverlayClient;
    // Shared theme applier: updates CSS variables so overlays using overlay-common.css react
    function applyOverlayTheme(theme) {
        try {
            if (!theme) return;
            if (theme === "Light" || theme === "White") {
                document.documentElement.style.setProperty(
                    "--text-on-surface",
                    "#000"
                );
                document.documentElement.style.setProperty(
                    "--muted",
                    "#555d60"
                );
                document.documentElement.style.setProperty(
                    "--accent-blue",
                    "#2c9bd6"
                );
                document.documentElement.style.setProperty(
                    "--overlay-surface",
                    "#ffffff"
                );
                // On light themes we prefer icons to be shown without a visible white surface
                document.documentElement.style.setProperty(
                    "--icon-surface",
                    "transparent"
                );
                document.documentElement.style.setProperty(
                    "--text-primary",
                    "#222"
                );
            } else {
                document.documentElement.style.setProperty(
                    "--text-on-surface",
                    "#ffffff"
                );
                document.documentElement.style.setProperty(
                    "--muted",
                    "#b0c4d4"
                );
                document.documentElement.style.setProperty(
                    "--accent-blue",
                    "#2c9bd6"
                );
                document.documentElement.style.setProperty(
                    "--overlay-surface",
                    "#26292b"
                );
                // Dark theme: use a subtle light surface behind icons so they stand out
                document.documentElement.style.setProperty(
                    "--icon-surface",
                    "rgba(255,255,255,0.03)"
                );
                document.documentElement.style.setProperty(
                    "--text-primary",
                    "#b0c4d4"
                );
            }
        } catch (e) {
            // ignore
        }
    }
    global.applyOverlayTheme = applyOverlayTheme;

    // Attempt to remove near-uniform backgrounds from PNG icons by drawing to a canvas
    // and alpha-ing pixels similar to the corner color. This is best-effort and will
    // silently fail when CORS or other restrictions prevent canvas readback.
    function removeImageBackground(img, tolerance) {
        try {
            if (!img || !img.naturalWidth || !img.naturalHeight) return false;
            tolerance = typeof tolerance === "number" ? tolerance : 20;
            var w = img.naturalWidth,
                h = img.naturalHeight;
            var canvas = document.createElement("canvas");
            canvas.width = w;
            canvas.height = h;
            var ctx = canvas.getContext("2d");
            ctx.drawImage(img, 0, 0, w, h);
            try {
                var id = ctx.getImageData(0, 0, w, h);
            } catch (e) {
                // CORS / tainted canvas - cannot proceed
                return false;
            }
            var data = id.data;
            // sample corner pixels to choose background color
            function sample(x, y) {
                var idx = (y * w + x) * 4;
                return [data[idx], data[idx + 1], data[idx + 2], data[idx + 3]];
            }
            var corners = [
                sample(1, 1),
                sample(w - 2, 1),
                sample(1, h - 2),
                sample(w - 2, h - 2),
            ];
            // choose majority/average color
            var r = 0,
                g = 0,
                b = 0,
                a = 0;
            corners.forEach(function (c) {
                r += c[0];
                g += c[1];
                b += c[2];
                a += c[3];
            });
            r = Math.round(r / corners.length);
            g = Math.round(g / corners.length);
            b = Math.round(b / corners.length);
            a = Math.round(a / corners.length);
            // if corner is already transparent, abort
            if (a < 250) return false;
            // iterate pixels and make similar colors transparent
            for (var i = 0; i < data.length; i += 4) {
                var dr = Math.abs(data[i] - r);
                var dg = Math.abs(data[i + 1] - g);
                var db = Math.abs(data[i + 2] - b);
                if (dr <= tolerance && dg <= tolerance && db <= tolerance) {
                    data[i + 3] = 0; // alpha
                }
            }
            ctx.putImageData(id, 0, 0);
            // replace image src with canvas data URL
            try {
                img.src = canvas.toDataURL("image/png");
                return true;
            } catch (e) {
                return false;
            }
        } catch (e) {
            return false;
        }
    }
    global.removeImageBackground = removeImageBackground;
})(window || this);
