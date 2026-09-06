// ============================================================
// Scroll management: scroll position memory/restore (/mods-specific + global URL-level), popstate handling
// ============================================================
// scrollTop assignments are ineffective/get reset. Strip the transform first, then poll until the list has really grown before placing.
// scrollTop assignments are ineffective/get reset. Strip the transform first, then poll until the list has really grown before placing.
// v0.72.0: Rewritten as "readiness gating + at-bottom flag recorded on leave" -
// (1) The old "y > limit" branch guessed on return that the user had been at the bottom; during the transition while
//     lazy-loaded covers (<img loading=lazy>) grew the list, it fired spuriously: it placed at a too-small limit and
//     finished, then stopped at the wrong spot once row heights expanded. Whether we were at the bottom is now decided
//     by the atBottom flag getScrollState recorded on leave - no more guessing: target = atBottom ? limit : min(y, limit).
// (2) Readiness gating: only considered ready once the list has rendered (limit>0) and scrollHeight stays unchanged for
//     ~4 consecutive rounds; never touch the scroll during the gradual growth phase of lazy loading/data rendering.
// (3) After placement, if the height keeps changing (late images) and the user has not interacted, keep re-placing;
//     wheel/touchstart/pointerdown always yield and finish immediately.
// v0.72.4: Hide the row list during return-restore and reveal it in the same frame as placement - eliminates the "list flashes at the top for a frame".
// The marker goes on MainLayout's .jg-main (that element persists across navigation; Blazor does not rewrite its class on page changes).
// CSS: .jg-restore-pending .jg-modrows { visibility: hidden; }
window.junigridJs.markScrollRestorePending = function (sel) {
    var el = document.querySelector(sel);
    if (el) el.classList.add('jg-restore-pending');
};
window.junigridJs.cancelScrollRestore = function (sel) {
    var el = document.querySelector(sel);
    if (el) el.classList.remove('jg-restore-pending');
};

window.junigridJs.setScrollWhenReady = function (sel, y, atBottom, report) {
    var el = document.querySelector(sel);
    if (!el) return;
    // v0.72.2: Diagnostic report - on finish, report back to C# once with (target, final scrollTop, rounds, reason)
    var reportFn = report && report.invokeMethodAsync
        ? function (t, f, tr, r) { try { report.invokeMethodAsync('Report', t, f, tr, r); } catch (e) { } }
        : function () { };
    var revealIfPending = function () { el.classList.remove('jg-restore-pending'); };
    var tries = 0, done = 0, lastH = -1, userTouched = false;
    var onUser = function () { userTouched = true; };
    el.addEventListener('wheel', onUser, { passive: true });
    el.addEventListener('touchstart', onUser, { passive: true });
    el.addEventListener('pointerdown', onUser, { passive: true });
    var finish = function () {
        el.removeEventListener('wheel', onUser);
        el.removeEventListener('touchstart', onUser);
        el.removeEventListener('pointerdown', onUser);
    };
    var tick = function () {
        // Keep suppressing the entrance-animation transform on every round (CSS keyframes would put it back, so it must be pressed repeatedly)
        el.classList.remove('jg-page-enter');
        if (el.style.transform !== 'none') el.style.transform = 'none';

        if (userTouched) { revealIfPending(); reportFn(y, el.scrollTop, tries, 'user-touched'); finish(); return; }   // user scrolled manually; yield immediately
        tries++;
        var limit = el.scrollHeight - el.clientHeight;
        if (limit < 0) limit = 0;
        var h = el.scrollHeight;
        // The real row list is visible (during the skeleton phase .jg-modrows is display:none)
        var rows = document.querySelector('.jg-modrows');
        var rowsShown = rows && rows.offsetHeight > 0;

        // v0.72.3: Place on the first frame instead of waiting for a stable height - the old "stable for 4 rounds before
        // acting" left the list parked at the top for 1-2s and then teleporting (the user saw a "top first, then jump" flash).
        // Now it re-places every round: as the height grows with later image loads it re-places along, imperceptibly;
        // finishing still requires an exact placement and a stable height.
        if (rowsShown && limit > 0) {
            var target = atBottom ? limit : Math.min(y, limit);
            el.scrollTop = target;
            var placed = Math.abs(el.scrollTop - target) <= 1;
            var heightStable = (h === lastH);
            // v0.72.4: Reveal the row list in the same frame as the first successful placement - the top state was never painted
            if (placed) revealIfPending();
            done = (placed && heightStable) ? done + 1 : 0;
            if (done >= 16) { reportFn(target, el.scrollTop, tries, 'placed-stable'); finish(); return; }   // stable for about 1.5s; done
        } else {
            done = 0;
        }
        lastH = h;
        if (tries >= 300) {           // fallback: after ~30s still not ready, take the currently reachable max instead of unconditionally jumping
            // v0.72.1: When limit===0 (the list never rendered any height), never place at 0 - that would mean "back to top"
            var fbTarget = limit > 0 ? Math.min(y, limit) : el.scrollTop;
            if (limit > 0) el.scrollTop = fbTarget;
            revealIfPending();
            reportFn(fbTarget, el.scrollTop, tries, 'timeout');
            finish();
            return;
        }
        setTimeout(tick, 90);
    };
    tick();
};

// v0.72.0: Take a scroll snapshot before leaving the list page - scrollTop + whether at the bottom (no more guessing on restore).
// Bottom tolerance is 4px, covering sub-pixel rounding.
window.junigridJs.getScrollState = function (sel) {
    var el = document.querySelector(sel);
    if (!el) return { y: 0, atBottom: false };
    var limit = el.scrollHeight - el.clientHeight;
    return { y: el.scrollTop, atBottom: limit > 0 && el.scrollTop >= limit - 4 };
};

// v0.71.2: Write the scroll position to sessionStorage twice (same key format as scrollSpy, double insurance on return restore).
// v0.72.0: atBottom is stored under a separate key - the original key holds a plain number that scrollSpy reads with parseFloat, so it must not change.
window.junigridJs.saveScrollKey = function (key, y, atBottom) {
    try {
        sessionStorage.setItem("jg-scroll:" + key, String(y));
        sessionStorage.setItem("jg-scroll:" + key + ":ab", atBottom ? "1" : "0");
    } catch (e) { }
};
// --- v1.06.8: Global scroll management (fresh navigation resets to top + back restores the previous position) ---
// .jg-main is the scroll container shared across pages - this module solves two things:
//  (1) Fresh navigation (clicking a link/nav icon/programmatic navigation) -> reset scroll to zero: previously nothing
//      reset it, so entering Nexus from the middle of the Mod list opened directly at the bottom (the position carried over);
//  (2) Back/forward (popstate) -> return to the position from when you left: at the moment of leaving, the scrollTop is
//      saved into sessionStorage (keyed by URL); on return, poll until the new page content has grown, then place.
// Key anti-pollution: during page transitions (skeleton rendering; the browser clamping scrollTop triggers scroll events)
// __jgScrollLock blocks all scroll writes - otherwise the clamp-induced scroll events would write 0 into the saved state,
// turning "restore the original position" into "restore to top" (one root cause of the old "close downloads page, land at list top" bug).
// /mods has its own dedicated restore system; skip this one during restore to avoid double placement.
(function () {
    var KEY = 'jg:urlscroll';
    function loadMap() { try { return JSON.parse(sessionStorage.getItem(KEY) || '{}'); } catch (e) { return {}; } }
    function saveMap(m) { try { sessionStorage.setItem(KEY, JSON.stringify(m)); } catch (e) { } }
    function urlKey() { return location.pathname + location.search; }
    function isMods(u) { return u === '/mods' || u.indexOf('/mods?') === 0 || u.indexOf('/mods/') === 0; }
    function main() { return document.querySelector('.jg-main'); }

    // Transition lock: for 700ms after navigation, all scroll writes are silent (skeleton/clamp period)
    function lock() {
        window.__jgScrollLock = true;
        clearTimeout(window.__jgScrollLockTimer);
        window.__jgScrollLockTimer = setTimeout(function () { window.__jgScrollLock = false; }, 700);
    }

    // (1) Fresh navigation: save the old page's position -> clear the target page's saved state -> reset scroll to zero
    // vNext: /nexus is a "memory page" - leaving via forward navigation and coming back must stop where you left:
    // its saved state is no longer cleared, and after render the position is restored from it (restoreFor).
    // /mods uses the page component's own dedicated restore system (restoreFor skips /mods), so its saved state is unaffected.
    // Other pages keep "fresh navigation = back to top".
    var origPush = history.pushState.bind(history);
    history.pushState = function (s, t, u) {
        var targetPath = null;
        try {
            var el = main();
            var m = loadMap();
            // Before leaving: save the current position under the [old URL] (that is what gets restored on return)
            if (el) m[urlKey()] = el.scrollTop;
            var target = new URL(u, location.href);
            targetPath = target.pathname + target.search;
            if (targetPath !== '/nexus') delete m[targetPath];
            saveMap(m);
        } catch (e) { }
        var r = origPush(s, t, u);
        lock();
        var el2 = null;
        try {
            el2 = main();
            if (el2) {
                el2.classList.remove('jg-restore-pending');   // clear any unfinished "restore pending" hiding from the previous round
                el2.scrollTop = 0;
            }
        } catch (e) { }
        if (targetPath === '/nexus') {
            // vNext: memory page with a saved scroll position -> attach the "restore pending" hiding [before] the new page
            // renders (.jg-main persists across navigation, so nexus content is hidden the moment it renders and the
            // "paint the top first" frame can never be painted); reveal it in the same frame restoreFor places successfully
            // (same idea as the /mods v0.72.4 dedicated system)
            try {
                var sy = loadMap()['/nexus'];
                if (sy && sy > 1 && el2) el2.classList.add('jg-restore-pending');
            } catch (e) { }
            setTimeout(restoreFor, 150);
        }
        return r;
    };

    // (2) Back/forward: set the "back navigation" flag (used by the /mods fallback restore) + lock + queue restore
    // vNext: returning to /nexus with a saved state -> this listener registers before Blazor's router, so the hiding
    // marker is attached synchronously: the new nexus content is hidden from its first frame and revealed in the same
    // frame restoreFor places (eliminating the "top first, then middle" flash)
    window.addEventListener('popstate', function () {
        try { window.__jgBackNav = true; } catch (e) { }
        lock();
        try {
            if (urlKey() === '/nexus') {
                var sy = loadMap()['/nexus'];
                if (sy && sy > 1) { var eln = main(); if (eln) eln.classList.add('jg-restore-pending'); }
            }
        } catch (e) { }
        setTimeout(restoreFor, 120);
    });
        window.junigridJs.readScrollKey = function (key) {
        try {
            var v = parseFloat(sessionStorage.getItem('jg-scroll:' + key));
            return isNaN(v) ? 0 : v;
        } catch (e) { return 0; }
    };

    // Regular scroll capture (no writes during the transition lock)
    function bindCapture() {
        var el = main();
        if (!el) { setTimeout(bindCapture, 400); return; }
        if (el.__gscrollBound) return;
        el.__gscrollBound = true;
        var pending = false;
        el.addEventListener('scroll', function () {
            if (pending) return;
            pending = true;
            requestAnimationFrame(function () {
                pending = false;
                if (window.__jgScrollLock) return;
                var m = loadMap();
                m[urlKey()] = el.scrollTop;
                saveMap(m);
            });
        }, { passive: true });
    }
    bindCapture();

    // vNext: Write the given value (or the current actual position) into the current URL's scroll state.
    // Called after a manual refresh (clicking the current page's icon in the top bar) resets to top - without this write
    // the saved state still holds the pre-refresh position, and leaving and returning would restore the old position
    // instead of the refreshed top.
    window.junigridJs.saveCurrentUrlScroll = function (y) {
        try {
            var el = main();
            var m = loadMap();
            m[urlKey()] = typeof y === 'number' ? y : (el ? el.scrollTop : 0);
            saveMap(m);
        } catch (e) { }
    };

    // (3) Restore: poll for up to ~4s, place as soon as content grows; user scrolling yields immediately
    // vNext: works with the "restore pending" hiding attached at navigation time - success / giving up / user takeover,
    // all three endings reveal the page in the same frame: the top state is never painted, so the "top first, then jump"
    // flash frame cannot exist.
    // restoreSeq token: during rapid successive navigations the older poll silently yields, so the two rounds never overwrite each other's scrollTop.
    var restoreSeq = 0;
    function restoreFor() {
        var url = urlKey();
        if (isMods(url)) return;   // handled by the /mods-specific system
        var el = main();
        if (!el) return;
        var y = loadMap()[url];
        if (!y || y < 1) { el.classList.remove('jg-restore-pending'); return; }   // nothing to restore: also make sure no hiding is left behind
        var my = ++restoreSeq;
        var tries = 0, lastH = -1, stable = 0, userTouched = false;
        var reveal = function () { el.classList.remove('jg-restore-pending'); };
        var onUser = function () { userTouched = true; };
        el.addEventListener('wheel', onUser, { passive: true });
        el.addEventListener('touchstart', onUser, { passive: true });
        el.addEventListener('pointerdown', onUser, { passive: true });
        var timer = null;
        var tick = function () {
            tries++;
            if (my !== restoreSeq) { cleanup(); return; }   // superseded by a newer restore: exit silently, leave the new round's hiding marker alone
            if (userTouched || tries > 45 || !el.isConnected) { reveal(); cleanup(); return; }
            // Suppress the page entrance animation's transform (it makes scrollTop assignments ineffective)
            el.classList.remove('jg-page-enter');
            if (el.style.transform !== 'none') el.style.transform = 'none';
            var h = el.scrollHeight;
            var limit = h - el.clientHeight;
            stable = (h === lastH) ? stable + 1 : 0;   // rounds with a stable height (auto-resets while the skeleton/images are still growing it)
            lastH = h;
            if (limit > 0) {
                el.scrollTop = y;
                if (Math.abs(el.scrollTop - y) <= 2) { reveal(); cleanup(); return; }
            }
            // The height has been stable for several rounds but y is still unreachable (content is shorter than when we
            // left) -> finish at the reachable position instead of keeping the page hidden forever (while the height is
            // still changing during loading, stable resets to 0, so this cannot misfire)
            if (stable >= 8) { reveal(); cleanup(); return; }
            timer = setTimeout(tick, 90);
        };
        tick();   // run the first round immediately (setInterval would wait another 90ms first, needlessly lengthening the blank period)
        function cleanup() {
            clearTimeout(timer);
            el.removeEventListener('wheel', onUser);
            el.removeEventListener('touchstart', onUser);
            el.removeEventListener('pointerdown', onUser);
        }
    }
})();

