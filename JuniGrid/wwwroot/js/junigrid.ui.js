// ============================================================
// General UI interactions: toast, cursor tilt, pixel dissolve (PixelSwap/launch button hover),
// scrollSpy, saves/avatar/update/author bubble tooltips, focus helpers
// ============================================================
// ------------------ Global toast (black bg / white text by default; kind="err" red bg / white text; auto-dismisses after 2.6s) ------------------
// Attached directly to document.body to dodge the pitfall where transform/filter ancestors inside the Blazor
// component tree break position:fixed. No stacking of the same toast: if the previous one is still around,
// remove it before showing the new one.
(function () {
    window.junigridJs = window.junigridJs || {};
    var current = null;
    var hideTimer = null;

    window.junigridJs.toast = function (msg, kind) {
        try {
            if (current) {
                if (hideTimer) clearTimeout(hideTimer);
                if (current.parentNode) current.parentNode.removeChild(current);
            }
            var el = document.createElement('div');
            el.className = 'jg-toast-live' + (kind === 'err' ? ' err' : '');
            el.textContent = msg;
            document.body.appendChild(el);
            current = el;
            if (window.gsap) {
                gsap.fromTo(el, { y: -14, opacity: 0, scale: 0.96 },
                    { y: 0, opacity: 1, scale: 1, duration: 0.28, ease: 'back.out(2)' });
            }
            hideTimer = setTimeout(function () {
                if (window.gsap) {
                    gsap.to(el, { y: -10, opacity: 0, duration: 0.3, ease: 'power2.in',
                        onComplete: function () {
                            if (el.parentNode) el.parentNode.removeChild(el);
                            if (current === el) current = null;
                        } });
                } else {
                    el.style.transition = 'opacity .4s ease';
                    el.style.opacity = '0';
                    setTimeout(function () {
                        if (el.parentNode) el.parentNode.removeChild(el);
                        if (current === el) current = null;
                    }, 450);
                }
            }, 2300);
        } catch (e) { /* toast failure must not affect the main flow */ }
    };

    // v0.67.0: The spring pill has been removed; nav item tooltips are handled uniformly by the .jg-cursor-tip below
})();

// Cursor-driven perspective tilt (GSAP quickTo; same effect as demos.gsap.com's cursor-driven perspective tilt)
window.junigridJs.tiltPerspective = function (selector, opts) {
    opts = opts || {};
    if (!window.gsap) return;
    var el = document.querySelector(selector);
    if (!el) return;

    gsap.set(el, { transformPerspective: 650, transformStyle: 'preserve-3d', willChange: 'transform' });

    var outerRX = gsap.quickTo(el, 'rotationX', { ease: 'power3', duration: opts.duration || 0.35 });
    var outerRY = gsap.quickTo(el, 'rotationY', { ease: 'power3', duration: opts.duration || 0.35 });
    var innerX = gsap.quickTo(el, 'x', { ease: 'power3', duration: opts.duration || 0.35 });
    var innerY = gsap.quickTo(el, 'y', { ease: 'power3', duration: opts.duration || 0.35 });

    // In non-idle states such as launching/running, turn off the 3D tilt (button disabled or in .running)
    function busy() {
        return el.disabled || !!el.closest('.jg-launch-row.running');
    }
    function onMove(e) {
        if (busy()) { onLeave(); return; }
        var r = el.getBoundingClientRect();
        var nx = (e.clientX - r.left) / r.width;      // 0..1 relative to the button itself
        var ny = (e.clientY - r.top) / r.height;
        outerRX(gsap.utils.interpolate(10, -10, ny));
        outerRY(gsap.utils.interpolate(-10, 10, nx));
        innerX(gsap.utils.interpolate(-6, 6, nx));
        innerY(gsap.utils.interpolate(-6, 6, ny));
    }
    function onLeave() {
        outerRX(0); outerRY(0); innerX(0); innerY(0);
    }

    el.addEventListener('pointermove', onMove);
    el.addEventListener('pointerleave', onLeave);
};


// ---- PixelSwap pixel dissolve: the second-stage content is revealed/collapsed as individual pixels. (Pure JS, WAAPI) ----
// pixelSwap(btn, maskEl, activate, opts): uses maskEl as the second state, revealed (enter) or collapsed (leave) via grid pixels.
(function () {
    if (typeof document === 'undefined') return;

    var clamp = function (v, a, b) { return v < a ? a : (v > b ? b : v); };
    var noise = function (s) { var v = Math.sin(s * 127.1 + 311.7) * 43758.5453; return v - Math.floor(v); };
    var MAXP = 240;

    function buildGrid(w, h, size, gap, randomness) {
        var cols = Math.max(1, Math.ceil((w + gap) / (size + gap)));
        var rows = Math.max(1, Math.ceil((h + gap) / (size + gap)));
        if (cols * rows > 240) {
            size = Math.ceil(size * Math.sqrt((cols * rows) / 240));
            cols = Math.max(1, Math.ceil((w + gap) / (size + gap)));
            rows = Math.max(1, Math.ceil((h + gap) / (size + gap)));
        }
        var stride = size + gap;
        var ox = (w - (cols * stride - gap)) / 2;
        var oy = (h - (rows * stride - gap)) / 2;
        var mix = clamp(randomness, 0, 1);
        var pos = [], i = 0;
        for (var r = 0; r < rows; r++) for (var c = 0; c < cols; c++) {
            var x = cols <= 1 ? 0.5 : c / (cols - 1);
            var y = rows <= 1 ? 0.5 : r / (rows - 1);
            var base = (x + y) / 2;
            var rst = noise(i + 1);
            pos.push({ left: ox + c * stride, top: oy + r * stride, off: base * (1 - mix) + rst * mix });
            i++;
        }
        return { pos: pos, size: size };
    }

    window.junigridJs.pixelSwap = function (btn, mask, activate, opts) {
        opts = opts || {};
        if (!btn) return;
        // Clear the old grid
        var old = btn.querySelector('.px-grid');
        var oldAnims = old ? old.__anims : null;
        if (oldAnims) oldAnims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
        if (old) { old.parentNode && old.parentNode.removeChild(old); }

        var w = Math.max(20, btn.clientWidth);
        var h = Math.max(20, btn.clientHeight);
        var gg = opts.gap || 0;
        var grid = buildGrid(w, h, opts.pixelSize || Math.round(Math.max(8, w / 13)), gg, opts.randomness || 0.2);
        btn.style.position = 'relative';

        var ms = Math.max(180, opts.duration || 420);
        var pixMs = clamp(opts.pixelDuration || 250, 60, ms);
        var spread = Math.max(0, ms - pixMs);
        var s0 = opts.pixelScale || 0.3;

        // Build keyframes (scale-up reveal)
        var kf = [];
        for (var s = 0; s <= 10; s++) {
            var p = s / 10, t = p;
            var sc = s0 + (1 - s0) * t;
            kf.push({ offset: p, opacity: t, transform: 'scale(' + sc + ')' });
        }

        // If activate=false (collapse): reverse shrink + fade out (scale 1→s0, opacity 1→0)
        var outKf = [];
        for (var q = 0; q <= 10; q++) {
            var pr = q / 10;
            var sc2 = 1 + (s0 - 1) * pr;
            outKf.push({ offset: pr, opacity: 1 - pr, transform: 'scale(' + sc2 + ')' });
        }

        var gridEl = document.createElement('div');
        gridEl.className = 'px-grid';
        gridEl.style.cssText = 'position:absolute;inset:0;z-index:6;pointer-events:none;overflow:hidden;';
        btn.appendChild(gridEl);

        var anims = [];
        grid.pos.forEach(function (p) {
            var px = document.createElement('div');
            px.style.cssText = 'position:absolute;left:' + p.left + 'px;top:' + p.top + 'px;width:' + grid.size + 'px;height:' + grid.size + 'px;border-radius:' + (opts.pixelRadius || 3) + '%;overflow:hidden;';

            // Inside each pixel, place a windowed copy of the mask
            var win = document.createElement('div');
            win.style.cssText = 'width:100%;height:100%;';
            var clone = mask.cloneNode(true);
            clone.style.cssText = 'position:absolute;left:' + (-p.left) + 'px;top:' + (-p.top) + 'px;width:' + w + 'px;height:' + h + 'px;transform-origin:' + (p.left + grid.size / 2) + 'px ' + (p.top + grid.size / 2) + 'px;';
            win.appendChild(clone);
            px.appendChild(win);
            gridEl.appendChild(px);
            var timing = { duration: pixMs, delay: p.off * spread, easing: 'linear', fill: 'both' };
            try { anims.push(px.animate(activate ? kf : outKf, timing)); } catch (e) {}
        });
        gridEl.__anims = anims;

        // When not activating (collapsing): remove the grid once the animation ends; when activating, keep it to show the second
        // state - the later leave triggers the collapse cleanup
        if (!activate) {
            setTimeout(function () {
                if (gridEl.parentNode) gridEl.parentNode.removeChild(gridEl);
            }, Math.max(ms, 600));
        }
    };

    // Hover binding: on enter show the white "Click to launch!" mask; on leave revert the button to its original look
    window.junigridJs.launchHover = function (sel) {
        var btn = sel ? document.querySelector(sel) : document.getElementById('launch-btn');
        if (!btn) return;
        var mask;   // cached second-state content
        var show = false;
        function buildMask() {
            var m = document.createElement('div');
            m.className = 'px-launch-mask';
            var txt = document.createElement('span');
            txt.className = 'px-launch-txt';
            txt.textContent = 'Click to launch!';
            m.appendChild(txt);
            return m;
        }
        // While launching/running (disabled or .running/.launching), skip the pixel dissolve
        function busy() {
            return btn.disabled || !!btn.closest('.jg-launch-row.running') || !!btn.closest('.jg-launch-row.launching');
        }
        function clearGrids() {
            btn.querySelectorAll('.px-grid').forEach(function (grid) {
                if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
                if (grid.parentNode) grid.parentNode.removeChild(grid);
            });
        }
        // Whether the width transition (320ms back to full width when stopping the game/canceling a launch) is in progress
        function widthTransitioning() {
            var anims;
            try { anims = btn.getAnimations(); } catch (e) { return false; }
            for (var i = 0; i < anims.length; i++) {
                var a = anims[i];
                if (a && typeof CSSTransition !== 'undefined' && a instanceof CSSTransition
                    && a.transitionProperty === 'width' && a.playState === 'running') return true;
            }
            return false;
        }
        btn.addEventListener('mouseenter', function () {
            if (busy()) {
                // Non-idle (launching/running): clear any leftover mask too, so the original button text always shows
                clearGrids();
                show = false;
                return;
            }
            if (show) return;
            show = true;   // claim first to prevent rapid enter/leave races from building the mask twice
            // Wait one frame before building: a width transition may have started this very frame (running/launching class
            // just toggled), so measuring now would miss a chunk; hover also has no effect while a width animation runs (by design)
            requestAnimationFrame(function () {
                if (!show || busy() || widthTransitioning() || !btn.matches(':hover')) { show = false; return; }
                if (!mask) mask = buildMask();
                window.junigridJs.pixelSwap(btn, mask, true);
            });
        });
        btn.addEventListener('mouseleave', function () {
            // While launching/running: skip the pixel "collapse" animation; reset and clear leftovers directly, avoiding a
            // flash of reversed motion when the mouse moves away
            if (busy()) {
                show = false;
                clearGrids();
                return;
            }
            if (!show) return;
            show = false;
            // If the mouse left before the rAF, the grid was never built and no collapse animation is needed
            if (!btn.querySelector('.px-grid')) return;
            if (mask) window.junigridJs.pixelSwap(btn, mask, false);
        });
    };
})();
(function () {
    window.junigridJs = window.junigridJs || {};
    var tracked = null, trackedKey = null, ticking = false;

    window.junigridJs.scrollSpy = function (selector, key, restore) {
        var el = document.querySelector(selector);
        if (!el) return;
        // Restore the previous position first (returning to the detail page goes back to the original scroll height). Double rAF waits for content to render steadily.
        if (restore !== false) try {
            var saved = sessionStorage.getItem('jg-scroll:' + key);
            if (saved !== null) {
                var y = parseFloat(saved);
                if (!isNaN(y) && y > 0) {
                    requestAnimationFrame(function () {
                        requestAnimationFrame(function () { el.scrollTop = y; });
                    });
                }
            }
        } catch (e) { }
        if (tracked === el && trackedKey === key) return;   // already bound; do not attach twice
        tracked = el; trackedKey = key;
        // v1.06.8: Double gating - the listener sits on the cross-page shared .jg-main and outlives component disposal:
        // (1) only write while still on the page URL bound at attach time (otherwise scrolling the downloads page would write
        //     the downloads position into modslist and the list could not return to its spot); (2) do not write during page transitions (__jgScrollLock).
        var pagePath = location.pathname + location.search;
        el.addEventListener('scroll', function () {
            if (ticking) return;
            ticking = true;
            requestAnimationFrame(function () {
                ticking = false;
                if (window.__jgScrollLock) return;
                if (location.pathname + location.search !== pagePath) return;
                try { sessionStorage.setItem('jg-scroll:' + trackedKey, String(el.scrollTop)); } catch (e) { }
            });
        }, { passive: true });
    };
})();


// ------------------ Profile dropdown (same elastic open/close as the GSAP easeReverse UI interactions demo) ------------------
(function () {
    window.junigridJs = window.junigridJs || {};

    window.junigridJs.profileDropdown = function (wrapSel, open) {
        var wrap = document.querySelector(wrapSel);
        if (!wrap) return;
        var menu = wrap.querySelector('.jg-profile-menu');
        var arrow = wrap.querySelector('.jg-sort-arrow');
        var items = wrap.querySelectorAll('.jg-profile-item');
        if (!menu || !window.gsap) { wrap.classList.toggle('open', open); return; }

        // v1.1.2: The profile dropdown now syncs the external overlay (paired by data-dd key, see the comment inside dropdownToggle) -
        // previously the overlay never got .open, so clicking outside could not close it (pre-existing bug)
        document.querySelectorAll('.jg-dd-overlay').forEach(function (o) {
            o.classList.toggle('open', !!open && o.dataset.dd === wrap.dataset.dd);
        });

        gsap.killTweensOf([menu, arrow]);
        if (open) {
            wrap.classList.add('open');
            var tl = gsap.timeline();
            tl.to(arrow, { rotation: 180, duration: 0.7, ease: 'elastic.out(1.2, 0.32)' }, 0)
              .fromTo(menu,
                  { autoAlpha: 0, yPercent: -22, scale: 0.72, transformOrigin: 'top center' },
                  { autoAlpha: 1, yPercent: 0, scale: 1, duration: 0.7, ease: 'elastic.out(1.2, 0.32)' }, 0)
              .from(items, { opacity: 0, x: -16, duration: 0.32, ease: 'back.out(2.6)', stagger: 0.05 }, 0.08);
        } else {
            // Exit uses a timeScale speedup + smooth ease-out (the intent of easeReverse/timeScale in the demo)
            var tl2 = gsap.timeline({
                onComplete: function () {
                    wrap.classList.remove('open');
                    gsap.set(menu, { autoAlpha: 0 });
                }
            });
            tl2.to(arrow, { rotation: 0, duration: 0.28, ease: 'power2.inOut' }, 0)
               .to(menu, { autoAlpha: 0, yPercent: -14, scale: 0.86, duration: 0.24, ease: 'power2.in' }, 0);
        }
    };
})();

// ------------------ GSAP elastic tooltip for the "?" help button ------------------
(function () {
    window.junigridJs = window.junigridJs || {};
    var bound = {};
})();


// ------------------ data-tip mouse-following pill tooltip (matches the nav bar) ------------------
(function () {
    window.junigridJs = window.junigridJs || {};
    var tip = null, curTarget = null;
    function ensure() {
        if (tip) return tip;
        tip = document.createElement('div');
        tip.className = 'jg-cursor-tip';
        document.body.appendChild(tip);
        return tip;
    }
    function show(t, x, y) {
        var el = ensure();
        el.textContent = t;
        el.style.opacity = '1';
        el.style.visibility = 'visible';
        move(x, y);
    }
    function move(x, y) {
        if (!tip) return;
        var w = tip.offsetWidth, h = tip.offsetHeight;
        var px = x + 14, py = y - h - 10;
        if (px + w + 8 > window.innerWidth) px = x - w - 14;
        if (py < 8) py = y + 18;
        tip.style.left = Math.round(px) + 'px';
        tip.style.top = Math.round(py) + 'px';
    }
    function hide() {
        if (!tip) return;
        tip.style.opacity = '0';
        tip.style.visibility = 'hidden';
    }
    document.addEventListener('mouseover', function (e) {
        var t = e.target.closest ? e.target.closest('[data-tip]') : null;
        if (t) { curTarget = t; show(t.getAttribute('data-tip'), e.clientX, e.clientY); }
        else if (curTarget) { curTarget = null; hide(); }
    });
    document.addEventListener('mousemove', function (e) {
        if (curTarget) move(e.clientX, e.clientY);
    }, { passive: true });
    document.addEventListener('mousedown', hide, true);
})();

// ------------------ v1.x: GSAP elastic tooltip for the settings page "?" help mark ------------------
// Springs in on hover (elastic), vanishes instantly on leave (no reverse animation); event delegation means Blazor re-renders need no rebinding
(function () {
    function bubbleOf(wrap) { return wrap.querySelector('.jg-help-tip-bubble'); }
    function btnOf(wrap) { return wrap.querySelector('.jg-help-tip-btn'); }
    function close(wrap) {
        if (wrap._tipTl) { wrap._tipTl.kill(); wrap._tipTl = null; }
        var b = bubbleOf(wrap), btn = btnOf(wrap);
        if (b) gsap.set(b, { autoAlpha: 0, y: 14, scale: 0.4, xPercent: -50 });
        if (btn) gsap.set(btn, { scale: 1 });
    }
    document.addEventListener('mouseover', function (e) {
        var wrap = e.target.closest ? e.target.closest('.jg-help-tip') : null;
        if (!wrap) return;
        var bubble = bubbleOf(wrap), btn = btnOf(wrap);
        if (!bubble) return;
        if (wrap._tipTl) wrap._tipTl.kill();
        wrap._tipTl = gsap.timeline({ paused: true })
            .to(bubble, {
                autoAlpha: 1, y: 0, scale: 1, duration: 1,
                ease: 'elastic.out(1.2, 0.3)'
            }, 0)
            .to(btn, {
                scale: 1.3, duration: 0.8,
                ease: 'elastic.out(1.2, 0.3)'
            }, 0);
        wrap._tipTl.timeScale(1).play();
    });
    document.addEventListener('mouseout', function (e) {
        var wrap = e.target.closest ? e.target.closest('.jg-help-tip') : null;
        if (!wrap) return;
        // Do not close while still moving inside the "?" mark/bubble
        if (e.relatedTarget && wrap.contains(e.relatedTarget)) return;
        close(wrap);   // requirement: close immediately on leave, no GSAP reverse animation
    });
})();



// v0.70.1: User avatar card - mirrors the easeReverse source: avatar elastic scale-up + bubble pop-out
// v1.08.0: Hover auto open/close removed - on the way to the bubble the mouse sweeps over mod cards below, and the close
// countdown starts the moment the avatar is left; while the open animation runs the bubble's hit area is still tiny
// (it starts at scale 0.4), so "the card was open yet closed by itself" happened intermittently.
// Now fully manual: click the avatar to open, click again to close; click anywhere outside the card to collapse;
// clicks inside the card (view profile/log out) do not close it.
// v1.08.1: Avatar hover animation kept - elastic scale-up on hover / restore on leave, feedback only; it never toggles the card.
window.junigridJs.userTipInit = function (wrapId, bubbleId) {
    var wrap = document.getElementById(wrapId);
    var bubble = document.getElementById(bubbleId);
    if (!wrap || !bubble || wrap.__tipBound) return;
    wrap.__tipBound = true;
    var avatar = wrap.querySelector(".jg-user-tip-avatar");
    if (typeof gsap === "undefined") { wrap.classList.add("jg-user-tip-nogsap"); return; }
    gsap.set(bubble, { autoAlpha: 0, y: 14, scale: 0.4, transformOrigin: "top right" });
    gsap.set(avatar, { scale: 1, transformOrigin: "center center" });
    // v1.08.1: The open timeline only drives the bubble - avatar scaling is split out for hover, so the two no longer fight each other
    var tl = gsap.timeline({ paused: true })
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 1.0, ease: "elastic.out(1.2, 0.3)" }, 0);

    // Hover animation kept: elastic scale-up on avatar hover, quick restore on leave (feedback only, never toggles the card)
    // v1.08.2: Look up the current avatar element each time - once avatar data arrives, Blazor swaps the initial-letter fallback
    // div for an img, so the element captured at bind time is detached from the DOM (why "the Y avatar animated but the real one did not")
    function avatarEl() { return wrap.querySelector(".jg-user-tip-avatar"); }
    function avatarScale(v, quick) {
        var el = avatarEl();
        if (!el) return;
        gsap.killTweensOf(el);
        gsap.to(el, { scale: v, transformOrigin: "center center",
            duration: quick ? 0.35 : 0.9, ease: quick ? "power2.out" : "elastic.out(1.2, 0.3)" });
    }
    wrap.addEventListener("mouseenter", function () { if (!isOpen) avatarScale(1.15); });
    wrap.addEventListener("mouseleave", function () { if (!isOpen) avatarScale(1, true); });

    var isOpen = false;
    function setOpen(v) {
        if (v === isOpen) return;
        isOpen = v;
        // The bubble is pointer-events:none by default (only released with .open, see app.css) - the click toggle must stay in
        // sync, otherwise buttons in an open card cannot be clicked
        wrap.classList.toggle("open", v);
        if (v) { avatarScale(1.15); tl.timeScale(1).play(); return; }
        // Collapse keeps the existing convention: no reverse animation, snap back instantly
        tl.pause(0);
        gsap.set(bubble, { autoAlpha: 0, y: 14, scale: 0.4 });
        var el = avatarEl();
        if (el) { gsap.killTweensOf(el); gsap.set(el, { scale: 1, transformOrigin: "center center" }); }
    }
    // Bound to wrap instead of the avatar element itself: once avatar data arrives the img/fallback siblings are swapped, so binding wrap keeps the listener
    wrap.addEventListener("click", function (e) {
        if (bubble.contains(e.target)) return;
        e.stopPropagation();
        setOpen(!isOpen);
    });
    document.addEventListener("click", function (e) {
        if (isOpen && !wrap.contains(e.target)) setOpen(false);
    });
};

// v1.0.17: Titlebar self-update button hover bubble - same as the easeReverse demo "?" bubble:
// elastic spring-in; no reverse animation on leave, snaps back instantly (same convention as userTipInit's collapse).
// Accepts an ElementReference (element object) or an id string.
window.junigridJs.updTipInit = function (wrap, bubble) {
    if (typeof wrap === "string") wrap = document.getElementById(wrap);
    if (typeof bubble === "string") bubble = document.getElementById(bubble);
    if (!wrap || !bubble || wrap.__updTipBound) return;
    wrap.__updTipBound = true;
    if (typeof gsap === "undefined") { wrap.classList.add("jg-upd-tip-nogsap"); return; }
    // Centering uses xPercent:-50 managed by GSAP - CSS translateX(-50%) would be overridden by GSAP's transform
    gsap.set(bubble, { autoAlpha: 0, xPercent: -50, y: -14, scale: 0.4, transformOrigin: "top center" });
    var tl = gsap.timeline({ paused: true })
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 1.0, ease: "elastic.out(1.2, 0.3)" }, 0);
    wrap.addEventListener("mouseenter", function () { tl.timeScale(1).play(); });
    wrap.addEventListener("mouseleave", function () {
        tl.pause(0);
        gsap.set(bubble, { autoAlpha: 0, xPercent: -50, y: -14, scale: 0.4 });
    });
};

// v1.04.0: Focus any element (used to refocus the search box after its X button clears the text)
window.junigridJs.focusElement = function (sel) {
    var el = typeof sel === "string" ? document.querySelector(sel) : sel;
    if (el) { try { el.focus(); } catch (e) { } }
};

// v1.04.0: Detail page "by <author>" - blur highlight (dark #3d3d3d / light #ffffff sweep) + hover avatar preview bubble.
// Same as the easeReverse demo: elastic spring-in + fast reverse exit (exit timeScale 2.5x).
window.junigridJs.authorTipInit = function (wrapId, bubbleId) {
    var wrap = document.getElementById(wrapId);
    var bubble = document.getElementById(bubbleId);
    if (!wrap || !bubble) return;
    var nameBtn = wrap.querySelector(".jg-author-name");
    var hl = wrap.querySelector(".jg-author-hl");
    if (typeof gsap === "undefined") { wrap.classList.add("jg-author-nogsap"); return; }
    if (wrap.__authorBound) return;   // already bound: do not rebind, and do not interrupt a running hover animation
    wrap.__authorBound = true;

    gsap.set(hl, { scaleX: 0, transformOrigin: "left center" });
    // v1.05.0: xPercent:-50 centers the bubble horizontally right above the author name (the arrow points at the name instead of drifting onto the cover)
    gsap.set(bubble, { autoAlpha: 0, y: 10, scale: 0.5, xPercent: -50, transformOrigin: "bottom center" });

    // Hover timeline: highlight sweep + bubble elastic pop-out
    var tl = gsap.timeline({ paused: true })
        .to(hl, { scaleX: 1, duration: 0.55, ease: "back.out(1.7)", easeReverse: "power2.out" }, 0)
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 0.9, ease: "elastic.out(1.2, 0.3)", easeReverse: "power3.in" }, 0.08);

    // On first render the highlight auto-sweeps once (blur-highlight load effect), then resets to zero and waits for hover
    gsap.timeline({ delay: 0.35 })
        .to(hl, { scaleX: 1, duration: 0.6, ease: "back.out(1.7)" })
        .to(hl, {
            scaleX: 0, transformOrigin: "right center", duration: 0.35, ease: "power2.in",
            onComplete: function () { gsap.set(hl, { transformOrigin: "left center" }); }
        }, "+=0.9");

    var closeTimer = null;
    function openTl() { if (closeTimer) { clearTimeout(closeTimer); closeTimer = null; } tl.timeScale(1).play(); }
    function closeTl() {
        if (closeTimer) clearTimeout(closeTimer);
        closeTimer = setTimeout(function () {
            // v1.06.3: Cancel the closing animation - pause(0) rewinds the timeline to its start instantly (bubble/highlight
            // return to their initial state); the elastic pop-out animation used when opening is unaffected
            tl.pause(0);
        }, 160);
    }
    wrap.addEventListener("mouseenter", openTl);
    wrap.addEventListener("mouseleave", closeTl);
    bubble.addEventListener("mouseenter", openTl);
    bubble.addEventListener("mouseleave", closeTl);
};
window.junigridJs.setScroll = function (sel, y) { var el = document.querySelector(sel); if (el) el.scrollTop = y; };

