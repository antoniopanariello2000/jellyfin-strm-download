// Strm Download plugin: redirects the native "Download" button in Jellyfin
// Web to a .strm-aware endpoint that resolves .strm files to their remote
// target instead of downloading the tiny .strm text file itself.
(function () {
    "use strict";

    var DOWNLOAD_PATH_RE = /\/Items\/[0-9a-fA-F-]{32,36}\/Download\b/i;
    var PREFIX = "/Plugins/StrmDownload";

    function rewrite(url) {
        if (typeof url !== "string") {
            return url;
        }

        if (url.indexOf(PREFIX) !== -1 || !DOWNLOAD_PATH_RE.test(url)) {
            return url;
        }

        return url.replace(DOWNLOAD_PATH_RE, function (match) {
            return PREFIX + match;
        });
    }

    // 1. fetch()
    if (window.fetch) {
        var originalFetch = window.fetch.bind(window);
        window.fetch = function (input, init) {
            if (typeof input === "string") {
                input = rewrite(input);
            } else if (input && typeof input.url === "string" && DOWNLOAD_PATH_RE.test(input.url)) {
                input = new Request(rewrite(input.url), input);
            }

            return originalFetch(input, init);
        };
    }

    // 2. XMLHttpRequest
    var originalOpen = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
        var args = Array.prototype.slice.call(arguments);
        args[1] = rewrite(url);
        return originalOpen.apply(this, args);
    };

    // 3. Direct navigation via <a href> (the most common way browsers trigger
    // the native "Save file" dialog for a download).
    document.addEventListener(
        "click",
        function (event) {
            var anchor = event.target && event.target.closest ? event.target.closest("a[href]") : null;
            if (!anchor || !DOWNLOAD_PATH_RE.test(anchor.href)) {
                return;
            }

            var rewritten = rewrite(anchor.href);
            if (rewritten === anchor.href) {
                return;
            }

            event.preventDefault();
            window.location.href = rewritten;
        },
        true
    );

    // 4. window.open (used by some Jellyfin Web versions to trigger downloads).
    var originalWindowOpen = window.open.bind(window);
    window.open = function (url, target, features) {
        return originalWindowOpen(rewrite(url), target, features);
    };
})();
