// JuniGrid — JS interop helpers, callable from Razor via IJSRuntime.
window.junigridJs = {
    popAnimate(selector) {
        if (!window.gsap) return;
        window.gsap.fromTo(
            selector,
            { scale: 1.0 },
            { scale: 1.08, duration: 0.18, yoyo: true, repeat: 1, ease: 'power2.inOut' }
        );
    },
    // Top bar selected highlight thumb: translates .jg-topnav-thumb onto the current .active item (sliding/horizontal toggle)
    placeNavThumb() {
        const nav = document.querySelector('.jg-topnav');
        const thumb = document.querySelector('.jg-topnav-thumb');
        const active = nav && nav.querySelector('.jg-topnav-item.active');
        if (!thumb || !active) return;
        thumb.style.width = active.offsetWidth + 'px';
        thumb.style.left = active.offsetLeft + 'px';
    },
    // v0.33.0: sort dropdown - both open/close clear the initial state, no leftover white blocks
    // v1.05.0: with .jg-dd-right on the wrap the menu right-aligns (dropdowns flush with the window's right edge no longer overflow); origin top right
    // v1.07.0: (1) the outer overlay .jg-dd-overlay is flipped in the same frame as the menu inside this function - the overlay used to rely on Blazor
    //            re-rendering and lagged one beat behind JS; during the "menu open, overlay missing" window, hover/clicks passed through to the mod cards below;
    //          (2) the animation applies to the .jg-sort-menu-in visual inner layer (Nexus page) while the outer box keeps its final rectangle,
    //            so the hit area is the final position from the first open frame; pages without the inner layer (Logs/Mods) automatically fall back to animating the menu itself
    dropdownToggle(wrapSel, open) {
        const wrap = document.querySelector(wrapSel);
        if (!wrap) return;
        document.querySelectorAll('.jg-dd-overlay').forEach(o => o.classList.toggle('open', !!open));
        const menu  = wrap.querySelector('.jg-sort-menu');
        const arrow = wrap.querySelector('.jg-sort-arrow');
        const items = wrap.querySelectorAll('.jg-sort-item');
        if (!menu) return;
        const vis = menu.querySelector(':scope > .jg-sort-menu-in') || menu;
        const fromRight = wrap.classList.contains('jg-dd-right');
        const originY = fromRight ? 'top right' : 'top left';

        // Fallback: without gsap, .open + CSS handles the toggle, avoiding leftover white blocks
        if (!window.gsap) {
            wrap.classList.toggle('open', !!open);
            return;
        }
        const hasER = parseFloat(gsap.version) >= 3.13;
        const er = v => hasER ? v : undefined;

        if (wrap.__ddTl) { wrap.__ddTl.kill(); wrap.__ddTl = null; }
        gsap.killTweensOf([vis, menu, arrow, items]);

        if (open) {
            wrap.classList.add('open');
            gsap.set(arrow, { rotation: 0 });
            gsap.set(vis,  { autoAlpha: 0, y: -10, scale: 0.92, transformOrigin: originY });
            gsap.set(items, { opacity: 0, x: -14 });
            const tl = gsap.timeline();
            tl.to(arrow, { rotation: 180, duration: 0.5, ease: 'back.out(2)', easeReverse: er('power2.inOut') }, 0)
              .to(vis,   { autoAlpha: 1, y: 0, scale: 1, duration: 0.45, ease: 'back.out(1.7)', easeReverse: er('power3.out') }, 0)
              .to(items, { opacity: 1, x: 0, duration: 0.28, ease: 'back.out(2)', easeReverse: er('power2.out'), stagger: 0.05 }, 0.08);
            wrap.__ddTl = tl;
        } else {
            const tl = gsap.timeline({
                onComplete() {
                    // Key: clear inline styles + remove the open class only after the animation fully ends, eliminating white blocks
                    gsap.set([vis, arrow, items], { clearProps: 'all' });
                    wrap.classList.remove('open');
                }
            });
            tl.to(items, { opacity: 0, x: -8, duration: 0.16, ease: 'power2.in', stagger: 0.03 }, 0)
              .to(vis,   { autoAlpha: 0, y: -10, scale: 0.92, duration: 0.22, ease: 'power2.out' }, 0)
              .to(arrow, { rotation: 0, duration: 0.28, ease: 'power2.inOut' }, 0);
            wrap.__ddTl = tl;
        }
    },

    // v0.33.0: expanding search - width in/out; on close clearProps:'width' hands control back to the CSS 40px
    searchToggle(sel, open) {
        const wrap = document.querySelector(sel);
        if (!wrap) return;
        const field = wrap.querySelector('.jg-search-field');
        const input = wrap.querySelector('.jg-search-input');
        if (!field) return;

        if (!window.gsap) {
            wrap.classList.toggle('open', !!open);
            return;
        }
        const hasER = parseFloat(gsap.version) >= 3.13;
        const er = v => hasER ? v : undefined;

        if (wrap.__srTl) { wrap.__srTl.kill(); wrap.__srTl = null; }
        gsap.killTweensOf([wrap, field]);

        if (open) {
            wrap.classList.add('open');
            gsap.set(wrap,  { width: 40 });
            gsap.set(field, { autoAlpha: 0 });
            const tl = gsap.timeline();
            tl.to(wrap,  { width: 220, duration: 0.48, ease: 'back.out(1.6)', easeReverse: er('power2.out') }, 0)
              .to(field, { autoAlpha: 1, duration: 0.24, ease: 'power2.out' }, 0.1);
            wrap.__srTl = tl;
            if (input) setTimeout(() => { try { input.focus(); } catch (e) {} }, 260);
        } else {
            const tl = gsap.timeline({
                onComplete() {
                    // Handed back to CSS: width is defined by .jg-search-x (without .open) as 40px
                    gsap.set([wrap, field], { clearProps: 'all' });
                    wrap.classList.remove('open');
                }
            });
            tl.to(field, { autoAlpha: 0, duration: 0.16, ease: 'power2.in' }, 0)
              .to(wrap,  { width: 40, duration: 0.32, ease: 'power2.out' }, 0.05);
            wrap.__srTl = tl;
        }
    },

    // On state switches like launch/close, immediately clear any leftover pixel-dissolve mask (.px-grid) and tilt transform on the launch button,
    // otherwise the leftover white "Click to launch!" covers the new button text (e.g. "Launching...").
    clearLaunchFx(selector) {
        const btn = document.querySelector(selector);
        if (!btn) return;
        btn.querySelectorAll('.px-grid').forEach(function (grid) {
            if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
            if (grid.parentNode) grid.parentNode.removeChild(grid);
        });
        if (window.gsap) window.gsap.killTweensOf(btn);
        if (window.gsap) window.gsap.set(btn, { clearProps: 'transform' });
    },
    // v0.31.0: PCL-style page entrance - applies .jg-page-enter to <main.jg-main> to trigger the CSS keyframes
    playPageEnter() {
        const el = document.querySelector('.jg-main');
        if (!el) return;
        el.classList.remove('jg-page-enter');
        // Force one reflow, then add it back, to retrigger the animation
        // eslint-disable-next-line no-unused-expressions
        void el.offsetWidth;
        el.classList.add('jg-page-enter');
        // Cleared after 900ms to avoid clashing with later interaction animations (longest child delay 290 + duration 420 ~= 710)
        clearTimeout(el.__peTimer);
        el.__peTimer = setTimeout(() => el.classList.remove('jg-page-enter'), 900);
    },
    scrollToBottom(selector) {
        const el = document.querySelector(selector);
        if (el) el.scrollTop = el.scrollHeight;
    },
    // Custom titlebar drag: forward mousedown to .NET which calls Window.DragMove().
    // (CSS -webkit-app-region is unreliable inside WebView2, so we do it manually.)
    enableWindowDrag(el, dotNetRef) {
        if (!el) return;
        el.addEventListener('mousedown', e => {
            if (e.button !== 0) return;              // left button only
            dotNetRef.invokeMethodAsync('BeginDrag');
        });
        el.addEventListener('dblclick', () => {
            dotNetRef.invokeMethodAsync('ToggleMaximize');
        });
    }
};

// ---- Added in v0.5.0 ----
// v0.39.0: Pixel Reveal - the login success card is covered by a pixel curtain;
// pixel blocks dissolve left to right with random jitter, revealing the avatar/nickname/welcome message below
window.junigridJs.pixelReveal = function (canvasSel) {
    const canvas = document.querySelector(canvasSel);
    if (!canvas) return;
    const card = canvas.parentElement;
    const dpr = window.devicePixelRatio || 1;
    const w = card.clientWidth, h = card.clientHeight;
    canvas.width = w * dpr; canvas.height = h * dpr;
    canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
    const ctx = canvas.getContext('2d');
    ctx.scale(dpr, dpr);

    const cell = 14;
    const cols = Math.ceil(w / cell), rows = Math.ceil(h / cell);
    const dark = matchMedia('(prefers-color-scheme: dark)').matches;
    const bg = dark ? '#1c1e1c' : '#161616';

    // Each pixel's reveal moment: normalized x + random jitter -> 0..1 range
    const reveal = [];
    for (let r = 0; r < rows; r++) {
        reveal[r] = [];
        for (let c2 = 0; c2 < cols; c2++) {
            reveal[r][c2] = (c2 / cols) * 0.72 + Math.random() * 0.28;
        }
    }

    const DURATION = 1100; // ms
    const t0 = performance.now();
    function frame(now) {
        const t = Math.min((now - t0) / DURATION, 1);
        // ease: power2.out - the tail reveals content faster
        const p = 1 - (1 - t) * (1 - t);
        ctx.clearRect(0, 0, w, h);
        for (let r = 0; r < rows; r++) {
            for (let c2 = 0; c2 < cols; c2++) {
                const rt = reveal[r][c2];
                if (p < rt) {
                    // Not revealed: solid pixel
                    ctx.fillStyle = bg;
                    ctx.fillRect(c2 * cell, r * cell, cell, cell);
                } else if (p < rt + 0.10) {
                    // Reveal edge: pixel shrinks and fades out
                    const k = (p - rt) / 0.10;
                    const sz = cell * (1 - k);
                    ctx.fillStyle = bg;
                    ctx.globalAlpha = 1 - k;
                    ctx.fillRect(c2 * cell + (cell - sz) / 2, r * cell + (cell - sz) / 2, sz, sz);
                    ctx.globalAlpha = 1;
                }
            }
        }
        if (t < 1) requestAnimationFrame(frame);
        else canvas.remove();
    }
    requestAnimationFrame(frame);
};

// v0.36.0: AnimatedList scroll effect (Blazor port of React Bits AnimatedList)
// - Rows scale 0.7->1 + opacity 0->1 (0.2s) at 50% viewport entry; collapse when leaving the viewport
// - Top/bottom gradient masks of the scroll container fade with scroll position
window.junigridJs.animatedListInit = function (scrollSel, listSel) {
    const scroller = document.querySelector(scrollSel);
    const list = document.querySelector(listSel);
    if (!scroller || !list) return;

    // -- Row entrance animation: IntersectionObserver, threshold ~=0.5, collapses when leaving the viewport (triggerOnce:false) --
    if (!list.__alObs) {
        list.__alObs = new IntersectionObserver(entries => {
            for (const e of entries) {
                const el = e.target;
                if (e.intersectionRatio >= 0.5) {
                    el.style.opacity = '1';
                    el.style.transform = 'scale(1)';
                } else {
                    el.style.opacity = '0';
                    el.style.transform = 'scale(0.7)';
                }
            }
        }, { root: scroller, threshold: [0, 0.5, 1] });
    }
    list.querySelectorAll('[data-al]').forEach(el => {
        if (el.__alBound) return;
        el.__alBound = true;
        // Initial state: collapsed before entering the viewport (only for ones not currently in it; ones in view expand immediately to avoid a flash)
        el.style.transition = 'opacity .2s ease, transform .2s ease';
        el.style.transformOrigin = 'center center';
        const r = el.getBoundingClientRect();
        const sr = scroller.getBoundingClientRect();
        const visible = r.top < sr.bottom && r.bottom > sr.top;
        if (!visible) { el.style.opacity = '0'; el.style.transform = 'scale(0.7)'; }
        list.__alObs.observe(el);
    });

    // -- Top/bottom gradient masks --
    if (!scroller.__alGrad) {
        scroller.__alGrad = true;
        const pos = getComputedStyle(scroller).position;
        if (pos === 'static') scroller.style.position = 'relative';
        const top = document.createElement('div');
        const bot = document.createElement('div');
        top.className = 'jg-al-gradient jg-al-gradient-top';
        bot.className = 'jg-al-gradient jg-al-gradient-bottom';
        scroller.appendChild(top);
        scroller.appendChild(bot);
        const onScroll = () => {
            const st = scroller.scrollTop;
            const sh = scroller.scrollHeight;
            const ch = scroller.clientHeight;
            top.style.opacity = Math.min(st / 50, 1);
            const bottomDist = sh - (st + ch);
            bot.style.opacity = sh <= ch ? 0 : Math.min(bottomDist / 50, 1);
        };
        scroller.addEventListener('scroll', onScroll, { passive: true });
        onScroll();
    }
};
// v0.35.0: real-time nav thumb sync - route changes/resizes/refresh all reposition immediately (double rAF waits for layout to settle)
window.junigridJs.placeNavThumb = function () {
    const nav = document.querySelector('.jg-topnav');
    const thumb = document.querySelector('.jg-topnav-thumb');
    if (!nav || !thumb) return;
    const place = () => {
        const active = nav.querySelector('.jg-topnav-item.active');
        if (!active) { thumb.style.width = '0px'; return; }
        const nr = nav.getBoundingClientRect();
        const r = active.getBoundingClientRect();
        thumb.style.left = (r.left - nr.left) + 'px';
        thumb.style.width = r.width + 'px';
    };
    // Double rAF: measure only after Blazor moves .active to the target item and layout reflow completes
    requestAnimationFrame(() => requestAnimationFrame(place));
};
// The thumb follows width changes from zoom/font loading in real time (no misalignment; the transition smooths it)
(function () {
    if (window.__navThumbBound) return; window.__navThumbBound = true;
    let raf = 0;
    const re = () => { cancelAnimationFrame(raf); raf = requestAnimationFrame(() => window.junigridJs.placeNavThumb()); };
    window.addEventListener('resize', re);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(re);
})();
window.junigridJs.setMaximized = function (isMax) {
    document.body.classList.toggle('jg-max', !!isMax);
};
window.junigridJs.restored = function () {
    document.body.classList.remove('jg-minimizing');
};
window.junigridJs.animateMinimize = function () {
    document.body.classList.add('jg-minimizing');
};

// ============================================================
// Startup animation: centered logo -> slide left -> JuniGrid wordmark stroke draw + fill -> fade out -> UI springs up from the bottom
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};
    var _splashDone = false;   // Animation finished playing
    var _uiReady = false;      // Blazor UI mounted

    function el(id) { return document.getElementById(id); }

    // Fallback: if gsap isn't loaded / elements are missing, let the UI through immediately (never deadlocks the app)
    window.junigridJs.splashInit = function () {
        // v0.19.0: the front-end splash has degenerated into an empty shell (display:none); the logo is shown by the WPF SplashWindow.
        // This only hides the shell until Blazor finishes mounting, so the main UI doesn't flash early.
        document.body.classList.add('jg-booting');
    };

    // v0.20.0: wait until Blazor's first frame is truly stable (two rAF frames + 100ms) before notifying WPF.
    // This avoids seeing the dark fallback (#app background) instead of the light theme while the main window fades in.
    window.junigridJs.splashUiReadyWhenStable = function () {
        function stable() {
            requestAnimationFrame(function () {
                requestAnimationFrame(function () {
                    setTimeout(function () { window.junigridJs.splashUiReady(); }, 100);
                });
            });
        }
        // Also wait for the shell DOM to appear (Blazor may have mounted but layout isn't computed yet)
        if (document.querySelector('#app .jg-shell')) stable();
        else setTimeout(function () { window.junigridJs.splashUiReadyWhenStable(); }, 30);
    };

    window.junigridJs.splashUiReady = function () {
        _uiReady = true;
        // v0.19.0: the transparent startup animation is fully handled by the WPF SplashWindow; the front end only does two things:
        // 1) notify the WPF host (SplashWindow / App) with ui-ready, which fades out the splash and shows the main window;
        // 2) immediately release jg-booting to let .jg-shell through - otherwise if the animation path above exits early
        //    on a missing element and never clears jg-booting, the whole main UI stays opacity:0/visibility:hidden,
        //    appearing as a "black main screen".
        document.body.classList.remove('jg-booting');
        try {
            if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
                window.chrome.webview.postMessage('ui-ready');
            }
        } catch (e) { /* ignore */ }
        maybeReveal();
    };

    function buildWordmark() {
        var splash = el('jg-splash');
        var logo = el('jg-splash-logo');
        var svg = el('jg-splash-word');
        if (!splash || !svg || !window.gsap) { hideSplash(); return; }

        // The logo appears immediately, not waiting for font measurement - the splash must show at once
        gsap.fromTo(logo, { opacity: 0, scale: 0.75 }, { opacity: 1, scale: 1, duration: 0.45 });

        var text = 'JuniGrid';
        var fs = Math.round(Math.max(72, Math.min(window.innerWidth, window.innerHeight) * 0.11));
        var dash = Math.max(fs * 7, 200);

        // Stroke text + fill text (for clipping)
        var NS = 'http://www.w3.org/2000/svg';
        var strokeText = mkText(true);
        var fillText = mkText(false);

        function mkText(isStroke) {
            var t = document.createElementNS(NS, 'text');
            t.setAttribute('x', 0); t.setAttribute('y', 0);
            t.setAttribute('fill', isStroke ? 'none' : '#4def7b');
            t.setAttribute('stroke', isStroke ? '#F8FAFC' : 'none');
            t.setAttribute('stroke-width', '1.4');
            t.setAttribute('stroke-linejoin', 'round');
            t.setAttribute('stroke-linecap', 'round');
            t.setAttribute('font-size', fs);
            t.setAttribute('font-weight', '800');
            t.setAttribute('letter-spacing', '-3px');
            for (var i = 0; i < text.length; i++) {
                var ts = document.createElementNS(NS, 'tspan');
                ts.textContent = text[i];
                if (isStroke) ts.setAttribute('data-draw', '1');  // Only stroke glyphs take part in the per-glyph draw
                t.appendChild(ts);
            }
            return t;
        }

        svg.appendChild(strokeText);
        svg.appendChild(fillText);

        // Measure glyph widths and start once fonts are loaded; if fonts.ready never fires (font blocked/offline),
        // force-start after 1.4s with estimates so the animation never hangs on the dark cover.
        var ran = false;
        function measureAndPlay() {
            var bbox;
            try { bbox = strokeText.getBBox(); } catch (e) { bbox = null; }
            if (!bbox || !bbox.width) { hideSplash(); return; }
            var pad = Math.max(1.4, fs * 0.1);
            var vx = bbox.x - pad, vy = bbox.y - pad,
                vw = bbox.width + pad * 2, vh = bbox.height + pad * 2;
            svg.setAttribute('viewBox', vx + ' ' + vy + ' ' + vw + ' ' + vh);
            svg.style.height = 'clamp(56px, 9.5vmin, 104px)';

            // Clip piece of the fill text (mask reveals left->right)
            var defs = document.createElementNS(NS, 'defs');
            var cp = document.createElementNS(NS, 'clipPath');
            cp.setAttribute('id', 'jgs-wipe');
            var rect = document.createElementNS(NS, 'rect');
            rect.id = 'jgs-wipeRect';
            rect.setAttribute('x', vx); rect.setAttribute('y', vy);
            rect.setAttribute('width', '0'); rect.setAttribute('height', vh);
            cp.appendChild(rect); defs.appendChild(cp);
            svg.insertBefore(defs, svg.firstChild);
            fillText.setAttribute('clip-path', 'url(#jgs-wipe)');

            playSplash(splash, logo, svg, vx, vy, vw, vh, fs, dash);
        }

        function start() {
            if (ran) return;
            ran = true;
            measureAndPlay();
        }
        document.fonts.ready.then(start).catch(start);
        setTimeout(start, 1400);
    }

    function playSplash(splash, logo, svg, vx, vy, vw, vh, fs, dash) {
        var strokes = svg.querySelectorAll('[data-draw]');
        var wipeRect = el('jgs-wipeRect');
        if (!strokes.length) { hideSplash(); return; }

        // Stroke initial state: the whole dash is hidden behind, then drawn segment by segment from 0
        gsap.set(strokes, { strokeDasharray: dash, strokeDashoffset: dash });
        gsap.set(wipeRect, { attr: { width: 0 } });
        // The text starts fully transparent - not shown until the logo animation completes
        gsap.set(svg, { opacity: 0 });

        // So that when the "logo+wordmark" group is centered, the logo sits exactly over the group's center;
        // at startup the logo rests at screen center, then slides left (restX) to its slot during the animation.
        var stage = el('jg-splash-stage');
        var gap = stage ? (parseFloat(getComputedStyle(stage).gap) || 14) : 14;
        var restX = (vw + gap) + vx * 0;   // vx already includes padding; the extra offset = word width + gap
        gsap.set(logo, { x: restX / 2 });

        // Logo slide duration (normal pacing)
        var slideDur = 0.9;

        var tl = gsap.timeline({
            defaults: { ease: 'power2.out' },
            onComplete: function () { _splashDone = true; maybeReveal(); }
        });

        // 0) The logo appears immediately via buildWordmark -> rests 0.5s at center -> slides left into place
        //    Fix: it used to wait only 0.05s and looked like it "ran the moment it appeared"; now it pauses 0.5s before moving
        var startDelay = 0.5;
        tl.to(logo, { x: 0, duration: slideDur, ease: 'linear' }, startDelay);
        // 1) Once the logo is in place, the text fades in, then strokes draw + fill
        tl.to(svg, { opacity: 1, duration: 0.35 }, startDelay + slideDur);
        tl.to(strokes, { strokeDashoffset: 0, duration: 1.5, ease: 'power2.inOut', stagger: 0.03 }, startDelay + slideDur + 0.25);
        tl.to(wipeRect, { attr: { width: vw }, duration: 0.9, ease: 'power2.inOut' }, startDelay + slideDur + 0.9);
        // Tail pause: ensure the stroke (1.5s) and fill (0.9s) fully finish before fading out, so the main UI doesn't show early (screenshot 3 bug fix)
        tl.to({}, { duration: 0.8 });
    }

    function maybeReveal() {
        if (!(_splashDone && _uiReady)) return;
        if (!window.gsap) { hideSplash(); return; }
        // Release the booting state: make the app shell visible. Remove .jg-shell's opacity:0!important first;
        // then gsap sets opacity:0 and slides in the same frame - no white flash.
        document.body.classList.remove('jg-booting');
        var splash = el('jg-splash'); if (!splash) return;
        var shell = document.querySelector('#app .jg-shell');
        if (shell) {
            gsap.fromTo(shell, { y: 36, opacity: 0 }, { y: 0, opacity: 1, duration: 0.5, ease: 'power2.out' });
        }
        gsap.to(splash, {
            opacity: 0, duration: 0.55, ease: 'power2.inOut',
            onComplete: function () {
                splash.classList.add('hidden');
                if (splash.parentNode) splash.parentNode.removeChild(splash);
            }
        });
    }

    function hideSplash() {
        document.body.classList.remove('jg-booting');   // Fallback: don't keep the shell hidden forever
        var splash = el('jg-splash');
        if (splash) { splash.classList.add('hidden'); if (splash.parentNode) splash.parentNode.removeChild(splash); }
    }
})();

// ---- The top bar selected block adapts to window size ----
// Resizing redistributes the top bar flex items, changing the .active item's offsetLeft/offsetWidth,
// but Blazor doesn't re-render on resize -> the thumb's left/width stay stale and misalign.
// Listen for resize, re-read the real geometry, and reposition.
window.addEventListener('resize', function () {
    if (window.junigridJs && typeof window.junigridJs.placeNavThumb === 'function')
        window.junigridJs.placeNavThumb();
});

// ------------------ Global toast (black bg/white text by default; kind="err" red bg/white text; auto-dismiss 2.6s) ------------------
// Attached directly to document.body to avoid Blazor component-tree transform/filter ancestors breaking position:fixed.
// Same-kind toasts don't stack: if one is showing, remove it before showing the new one.
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
        } catch (e) { /* toast failure doesn't affect the main flow */ }
    };

    // v0.67.0: the spring pill was removed; nav item tooltips are uniformly handled by the .jg-cursor-tip below
})();

// Cursor-driven perspective tilt (GSAP quickTo, same effect as demos.gsap.com's cursor-driven perspective tilt)
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

    // Disable the 3D tilt in non-idle states like launching/running (button disabled or in .running)
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


// ---- PixelSwap pixel dissolve: second-stage content revealed/collapsed pixel by pixel. (Pure JS, WAAPI) ----
// pixelSwap(btn, maskEl, activate, opts): uses maskEl as the second state, revealed (entering) or collapsed (leaving) as a grid of pixels.
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

        // Generate keyframes (zoom reveal)
        var kf = [];
        for (var s = 0; s <= 10; s++) {
            var p = s / 10, t = p;
            var sc = s0 + (1 - s0) * t;
            kf.push({ offset: p, opacity: t, transform: 'scale(' + sc + ')' });
        }

        // If activate=false (collapse): reverse shrink + fade out (scale 1->s0, opacity 1->0)
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

            // Windowed copy of the mask inside each pixel
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

        // Inactive (collapsing): remove the grid when the animation ends; when active it stays as the second state, and a later leave triggers the collapse cleanup
        if (!activate) {
            setTimeout(function () {
                if (gridEl.parentNode) gridEl.parentNode.removeChild(gridEl);
            }, Math.max(ms, 600));
        }
    };

    // Hover binding: enter shows the "Click to launch!" white mask; leave collapses back to the original button
    window.junigridJs.launchHover = function (sel) {
        var btn = sel ? document.querySelector(sel) : document.getElementById('launch-btn');
        if (!btn) return;
        var mask;   // Cached second-state content
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
        // While launching/running (disabled or .running), no pixel dissolve
        function busy() {
            return btn.disabled || !!btn.closest('.jg-launch-row.running');
        }
        btn.addEventListener('mouseenter', function () {
            if (busy()) {
                // Non-idle (launching/running): clear any leftover mask too, so the original button text shows
                btn.querySelectorAll('.px-grid').forEach(function (grid) {
                    if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
                    if (grid.parentNode) grid.parentNode.removeChild(grid);
                });
                show = false;
                return;
            }
            if (show) return;
            show = true;
            if (!mask) mask = buildMask();
            window.junigridJs.pixelSwap(btn, mask, true);
        });
        btn.addEventListener('mouseleave', function () {
            // Launching/running: skip the pixel "collapse" animation; reset and clear leftovers directly to avoid flashing the reverse animation on mouse-out
            if (busy()) {
                show = false;
                btn.querySelectorAll('.px-grid').forEach(function (grid) {
                    if (grid.__anims) grid.__anims.forEach(function (a) { try { a.cancel(); } catch (e) {} });
                    if (grid.parentNode) grid.parentNode.removeChild(grid);
                });
                return;
            }
            if (!show) return;
            show = false;
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
        // Restore the last position first (returning from the detail page goes back to the original scroll height). Double rAF waits for rendering to settle.
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
        if (tracked === el && trackedKey === key) return;   // Already bound; don't listen twice
        tracked = el; trackedKey = key;
        // v1.06.8: double gating - the listener sits on the page-shared .jg-main and survives component disposal:
        // (1) write only on the URL bound at attach time (otherwise scrolling on the downloads page writes that position into modslist,
        //    and returning to the list can't restore the original position); (2) don't write during page transitions (__jgScrollLock).
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


// ------------------ Profile dropdown (same elastic open/close as the GSAP easeReverse UI interactions) ------------------
(function () {
    window.junigridJs = window.junigridJs || {};

    window.junigridJs.profileDropdown = function (wrapSel, open) {
        var wrap = document.querySelector(wrapSel);
        if (!wrap) return;
        var menu = wrap.querySelector('.jg-profile-menu');
        var arrow = wrap.querySelector('.jg-sort-arrow');
        var items = wrap.querySelectorAll('.jg-profile-item');
        if (!menu || !window.gsap) { wrap.classList.toggle('open', open); return; }

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
            // Exit uses timeScale speed-up + smooth ease-out (the intent of easeReverse/timeScale in the demo)
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

// ------------------ GSAP elastic tooltip for the question-mark help button ------------------
(function () {
    window.junigridJs = window.junigridJs || {};
    var bound = {};
})();


// ------------------ data-tip cursor-following pill tooltip (consistent with the nav bar) ------------------
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



// ============ v0.59.0: mod detail page image click zoom (with an in-place placeholder to prevent collapse) ============
// Click an image -> leave a same-size placeholder in place (keeps the box) -> move the image to the modal center and zoom simply;
// close -> put the image back and remove the placeholder.
(function () {
    if (!window.junigridJs) window.junigridJs = {};

    function initZoom() {
        var modal = document.getElementById('jgFlipModal');
        if (!modal) return;
        var content = modal.querySelector('.jg-flip-content');
        var overlay = modal.querySelector('.jg-flip-overlay');
        var openEl = null, openParent = null, openNext = null, placeholder = null;

        function close() {
            if (!openEl) return;
            var el = openEl;
            modal.classList.remove('open');
            el.classList.remove('jg-zoom-open');
            // Put back in the original position (located via the placeholder)
            if (placeholder && placeholder.parentNode) {
                placeholder.parentNode.insertBefore(el, placeholder);
                placeholder.remove();
            } else if (openNext && openNext.parentNode === openParent) {
                openParent.insertBefore(el, openNext);
            } else {
                openParent.appendChild(el);
            }
            openEl = null; placeholder = null;
        }

        function open(el) {
            openParent = el.parentNode;
            openNext = el.nextSibling;
            // Create a placeholder: holds the original box size to prevent collapse
            var rect = el.getBoundingClientRect();
            placeholder = document.createElement('div');
            placeholder.className = 'jg-zoom-placeholder';
            placeholder.style.width = rect.width + 'px';
            placeholder.style.height = rect.height + 'px';
            // Inherit margins so vertical spacing stays consistent
            var cs = window.getComputedStyle(el);
            placeholder.style.margin = cs.margin;
            placeholder.style.display = cs.display === 'inline' ? 'inline-block' : cs.display;
            openParent.insertBefore(placeholder, el);
            // Move the image to the modal center
            content.appendChild(el);
            el.classList.add('jg-zoom-open');
            openEl = el;
            modal.classList.add('open');
        }

        function bind(el) {
            if (el.__zoomBound) return;
            el.__zoomBound = true;
            el.addEventListener('click', function () {
                var car = el.closest && el.closest('.jg-carousel');
                if (car && car.__justDragged) return;   // Don't zoom on release after a drag
                if (openEl === el) close();
                else if (!openEl) open(el);
            });
        }

        document.querySelectorAll('.jg-desc-with-imgs img, .jg-flip-cover, .jg-carousel-img').forEach(bind);
        if (!overlay.__zoomBound) {
            overlay.__zoomBound = true;
            overlay.addEventListener('click', close);
        }
    }

    window.junigridJs.modDetailInit = function () {
        requestAnimationFrame(function () {
            requestAnimationFrame(initZoom);
        });
    };
})();


// ============ v0.69.0: detail page parallax image carousel (wheel horizontal scroll + drag; scrolling paused while zoomed) ============
(function () {
    if (!window.junigridJs) window.junigridJs = {};
    })();

// v0.69.9: accordion on-demand scrolling - after expanding, measure the real content height; add .jg-acc-scroll to .jg-acc only if >360px
window.junigridJs = window.junigridJs || {};
window.junigridJs.accMeasureScroll = function () {
    var accs = document.querySelectorAll(".jg-acc.open");
    for (var i = 0; i < accs.length; i++) {
        var acc = accs[i];
        var inner = acc.querySelector(".jg-acc-inner");
        if (!inner) continue;
        // Header + real content height (scrollHeight ignores max-height)
        if (inner.scrollHeight > 360) acc.classList.add("jg-acc-scroll");
        else acc.classList.remove("jg-acc-scroll");
    }
    var closed = document.querySelectorAll(".jg-acc:not(.open)");
    for (var j = 0; j < closed.length; j++) closed[j].classList.remove("jg-acc-scroll");
};

// v0.70.0: accordion GSAP elastic animation (after the easeReverse demo) -
// Expand: elastic arrow rotation + elastic panel expansion + back.out staggered row entrance;
// Collapse: fast power easing (≈2.5x exit speed), then measure the scrollbar on demand.
window.junigridJs = window.junigridJs || {};
window.junigridJs.accAnimate = function (id, opening) {
    var acc = document.getElementById(id);
    if (!acc) return;
    var inner = acc.querySelector(".jg-acc-inner");
    var arrow = acc.querySelector(".jg-acc-arrow");
    if (!inner) return;
    if (typeof gsap === "undefined") {           // Without GSAP, degrade to direct show/hide
        inner.style.height = opening ? "" : "52px";
        if (window.junigridJs.accMeasureScroll) window.junigridJs.accMeasureScroll();
        return;
    }
    if (acc.__tl) { acc.__tl.kill(); acc.__tl = null; }
    var rows = acc.querySelectorAll(".jg-acc-body .jg-acc-row, .jg-acc-body .jg-req-table, .jg-acc-body > span, .jg-acc-body > div");
    if (opening) {
        var target = Math.min(inner.scrollHeight, 360);
        acc.__tl = gsap.timeline({
            onComplete: function () {
                gsap.set(inner, { clearProps: "height" });
                if (window.junigridJs.accMeasureScroll) window.junigridJs.accMeasureScroll();
            }
        })
        .to(arrow, { rotation: 180, duration: 0.9, ease: "elastic.out(1.2,0.3)" }, 0)
        .fromTo(inner, { height: 52 }, { height: target, duration: 1.0, ease: "elastic.out(1.2,0.45)" }, 0)
        .from(rows, { opacity: 0, x: -18, duration: 0.45, ease: "back.out(2.5)", stagger: 0.05, clearProps: "opacity,transform" }, 0.12);
    } else {
        acc.__tl = gsap.timeline({
            onComplete: function () {
                gsap.set(inner, { clearProps: "height" });
                if (window.junigridJs.accMeasureScroll) window.junigridJs.accMeasureScroll();
            }
        })
        .to(arrow, { rotation: 0, duration: 0.4, ease: "power2.inOut" }, 0)
        .to(inner, { height: 52, duration: 0.45, ease: "power3.out" }, 0);
    }
};

// v0.70.1: user avatar card - same as the easeReverse source: elastic avatar zoom + bubble pop
// v1.08.0: hover auto-toggle removed - while moving toward the bubble the mouse crosses the mod cards below, and leaving the avatar starts the close countdown,
// and while opening, the bubble's hit area is small (starts at scale 0.4), so "the card was open but closed itself" happened probabilistically.
// Now fully manual: click the avatar to open, click again to close; clicking anywhere outside collapses it; clicking inside (View profile/Log out) doesn't.
// v1.08.1: avatar hover animation kept - elastic zoom on hover / restore on leave, feedback only, doesn't toggle the card.
window.junigridJs.userTipInit = function (wrapId, bubbleId) {
    var wrap = document.getElementById(wrapId);
    var bubble = document.getElementById(bubbleId);
    if (!wrap || !bubble || wrap.__tipBound) return;
    wrap.__tipBound = true;
    var avatar = wrap.querySelector(".jg-user-tip-avatar");
    if (typeof gsap === "undefined") { wrap.classList.add("jg-user-tip-nogsap"); return; }
    gsap.set(bubble, { autoAlpha: 0, y: 14, scale: 0.4, transformOrigin: "top right" });
    gsap.set(avatar, { scale: 1, transformOrigin: "center center" });
    // v1.08.1: the open timeline only drives the bubble - avatar scaling is separate for hover, so the two no longer fight
    var tl = gsap.timeline({ paused: true })
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 1.0, ease: "elastic.out(1.2, 0.3)" }, 0);

    // Hover animation kept: elastic zoom on hover, quick restore on leave (feedback only, no card toggling)
    // v1.08.2: look up the current avatar element each time - once avatar data arrives Blazor swaps the initial-letter fallback div for an img,
    // so the old element captured at bind time is detached (why "the fallback avatar animated but the real one didn't")
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
        // The bubble is pointer-events:none by default (released only with .open, see app.css) - click toggling must stay in sync, otherwise buttons can't be clicked while the card is open
        wrap.classList.toggle("open", v);
        if (v) { avatarScale(1.15); tl.timeScale(1).play(); return; }
        // Collapse follows the existing convention: no reverse animation, snap back instantly
        tl.pause(0);
        gsap.set(bubble, { autoAlpha: 0, y: 14, scale: 0.4 });
        var el = avatarEl();
        if (el) { gsap.killTweensOf(el); gsap.set(el, { scale: 1, transformOrigin: "center center" }); }
    }
    // Bound on wrap rather than the avatar element itself: img/fallback siblings swap when avatar data arrives; binding on wrap keeps the listeners
    wrap.addEventListener("click", function (e) {
        if (bubble.contains(e.target)) return;
        e.stopPropagation();
        setOpen(!isOpen);
    });
    document.addEventListener("click", function (e) {
        if (isOpen && !wrap.contains(e.target)) setOpen(false);
    });
};

// v1.04.0: focus any element (used to refocus the search box after the X clears it)
window.junigridJs.focusElement = function (sel) {
    var el = typeof sel === "string" ? document.querySelector(sel) : sel;
    if (el) { try { el.focus(); } catch (e) { } }
};

// v1.04.0: detail page "by" author name - blur highlight (dark #3d3d3d / light #ffffff sweep) + hover avatar preview bubble.
// Same as the easeReverse demo: elastic entrance + fast reverse exit (exit timeScale 2.5x).
window.junigridJs.authorTipInit = function (wrapId, bubbleId) {
    var wrap = document.getElementById(wrapId);
    var bubble = document.getElementById(bubbleId);
    if (!wrap || !bubble) return;
    var nameBtn = wrap.querySelector(".jg-author-name");
    var hl = wrap.querySelector(".jg-author-hl");
    if (typeof gsap === "undefined") { wrap.classList.add("jg-author-nogsap"); return; }
    if (wrap.__authorBound) return;   // Already bound: don't rebind and don't interrupt an in-flight hover animation
    wrap.__authorBound = true;

    gsap.set(hl, { scaleX: 0, transformOrigin: "left center" });
    // v1.05.0: xPercent:-50 centers the bubble horizontally above the author name (the arrow points at the name, not off onto the cover)
    gsap.set(bubble, { autoAlpha: 0, y: 10, scale: 0.5, xPercent: -50, transformOrigin: "bottom center" });

    // Hover timeline: highlight sweep + elastic bubble pop
    var tl = gsap.timeline({ paused: true })
        .to(hl, { scaleX: 1, duration: 0.55, ease: "back.out(1.7)", easeReverse: "power2.out" }, 0)
        .to(bubble, { autoAlpha: 1, y: 0, scale: 1, duration: 0.9, ease: "elastic.out(1.2, 0.3)", easeReverse: "power3.in" }, 0.08);

    // On first render the highlight auto-sweeps once (blur-highlight load effect), then returns to zero awaiting hover
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
            // v1.06.3: close animation removed - pause(0) instantly rewinds the timeline (bubble/highlight return to their initial state);
            // the open elastic pop animation is unaffected
            tl.pause(0);
        }, 160);
    }
    wrap.addEventListener("mouseenter", openTl);
    wrap.addEventListener("mouseleave", closeTl);
    bubble.addEventListener("mouseenter", openTl);
    bubble.addEventListener("mouseleave", closeTl);
};
window.junigridJs.setScroll = function (sel, y) { var el = document.querySelector(sel); if (el) el.scrollTop = y; };

// v0.71.1: wait until the container's scrollHeight is sufficient before setting scrollTop (so it isn't clamped back to the top while images haven't loaded)
// v0.71.6: resistant to double interference - (1) update checking sets the list display:none (skeleton-phase scrollHeight collapses to 0)
// (2) on return, playPageEnter puts a transform entrance animation on .jg-main; transform makes
// scrollTop settings invalid/reset. Remove the transform first, then poll until the list's real height is in place.
// v0.72.0: rewritten as "readiness gating + at-bottom flag taken on leave" -
// (1) The old "y > limit" branch guessed on return that the user had been at the bottom; during the transition when lazy-loaded covers
//    (<img loading=lazy>) grow the list, it misfired: it landed on a too-small limit and finished, then stopped at the wrong place after rows grew.
//    Now whether it was at the bottom is decided by the atBottom recorded by getScrollState on leave - no more guessing:
//    target = atBottom ? limit : min(y, limit).
// (2) Readiness gating: the list is ready only when rendered (limit>0) and scrollHeight stays unchanged for ~4 consecutive rounds;
//    never touch it during the gradual growth from lazy loading/data rendering.
// (3) After placement, if the height keeps changing (late images) and the user hasn't moved, keep re-placing;
//    wheel/touchstart/pointerdown always yield and finish.
// v0.72.4: hide the row list during return-restore and reveal it the same frame as placement - removes the "list flashes at the top first" frame.
// The marker is set on MainLayout's .jg-main (that element persists across navigation; the class isn't rewritten by Blazor on page change).
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
    // v0.72.2: diagnostic report - on finish, report (target, final scrollTop, rounds, reason) back to C# once
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
        // Keep suppressing the entrance animation transform every round (the CSS keyframes reapply it; it must be suppressed repeatedly)
        el.classList.remove('jg-page-enter');
        if (el.style.transform !== 'none') el.style.transform = 'none';

        if (userTouched) { revealIfPending(); reportFn(y, el.scrollTop, tries, 'user-touched'); finish(); return; }   // User scrolled manually; yield immediately
        tries++;
        var limit = el.scrollHeight - el.clientHeight;
        if (limit < 0) limit = 0;
        var h = el.scrollHeight;
        // The real row list is showing (during the skeleton phase .jg-modrows is display:none)
        var rows = document.querySelector('.jg-modrows');
        var rowsShown = rows && rows.offsetHeight > 0;

        // v0.72.3: place on the first frame instead of waiting for a stable height - the old "stable for 4 rounds first" left the list at the top
        // for 1-2s then teleported (users saw a "top first, then jump" flash). Now it re-places every round:
        // it follows later height growth from image loading imperceptibly; finishing still requires precise placement and a stable height.
        if (rowsShown && limit > 0) {
            var target = atBottom ? limit : Math.min(y, limit);
            el.scrollTop = target;
            var placed = Math.abs(el.scrollTop - target) <= 1;
            var heightStable = (h === lastH);
            // v0.72.4: reveal the row list the same frame as the first successful placement - the top state was never painted
            if (placed) revealIfPending();
            done = (placed && heightStable) ? done + 1 : 0;
            if (done >= 16) { reportFn(target, el.scrollTop, tries, 'placed-stable'); finish(); return; }   // Stable for ~1.5s, done
        } else {
            done = 0;
        }
        lastH = h;
        if (tries >= 300) {           // Fallback: after ~30s still not ready, take the current reachable max instead of unconditionally jumping to the top
            // v0.72.1: when limit===0 (the list never got any height) never place at 0 - that means "back to top"
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

// v0.72.0: take a scroll snapshot before leaving the list page - scrollTop + whether at the bottom (no more guessing on restore).
// Bottom tolerance 4px to cover sub-pixel rounding.
window.junigridJs.getScrollState = function (sel) {
    var el = document.querySelector(sel);
    if (!el) return { y: 0, atBottom: false };
    var limit = el.scrollHeight - el.clientHeight;
    return { y: el.scrollTop, atBottom: limit > 0 && el.scrollTop >= limit - 4 };
};

// v0.71.2: double-write the scroll position to sessionStorage (same key format as scrollSpy, double insurance on return).
// v0.72.0: atBottom is stored under a separate key - the original key is a plain number format that scrollSpy reads with parseFloat; don't touch it.
window.junigridJs.saveScrollKey = function (key, y, atBottom) {
    try {
        sessionStorage.setItem("jg-scroll:" + key, String(y));
        sessionStorage.setItem("jg-scroll:" + key + ":ab", atBottom ? "1" : "0");
    } catch (e) { }
};

// --- v0.74.0: Nexus search island (GSAP easeReverse: back.out(2) expand / power2.out collapse) ---
// v1.06.2: restore the magnifier button (right next to the Nexus logo; clicking expands the input rightward); without a button, degrade to always-expanded mode.
junigridJs.searchIslandInit = function (islandId, btnId, inputId) {
    var island = document.getElementById(islandId);
    if (!island || island.dataset.islandBound) return;
    island.dataset.islandBound = "1";
    var field = island.querySelector('.jg-island-field');
    var input = document.getElementById(inputId);
    var btn = document.getElementById(btnId);
    var isOpen = false;
    // No button (always-expanded mode): only bind search history show/hide (bindHistory is a function declaration, hoisted).
    if (!btn) { bindHistory(); return; }

    if (typeof gsap === 'undefined') { // No GSAP: degrade to class toggling
        btn.addEventListener('click', function () {
            isOpen = !isOpen;
            if (!isOpen && input && input.value && input.value.trim().length > 0) { isOpen = true; return; } // v1.01.0: don't collapse when there's content
            island.classList.toggle('open', isOpen);
            if (isOpen && input) input.focus();
        });
        bindHistory();
        return;
    }
    // easeReverse needs GSAP 3.13+; older versions automatically fall back to symmetric easing
    var erOK = parseFloat(gsap.version || '0') >= 3.13;
    gsap.set(island, { width: 40 });
    gsap.set(field, { autoAlpha: 0, width: 0 });
    var tl = gsap.timeline({ paused: true })
        .to(island, { width: 300, duration: 0.7, ease: 'back.out(2)', easeReverse: erOK ? 'power2.out' : undefined }, 0)
        .to(field, { autoAlpha: 1, width: 244, duration: 0.35, ease: 'power2.out', easeReverse: erOK ? 'power2.in' : undefined }, 0.18);

    function hasContent() { return !!(input && input.value && input.value.trim().length > 0); }
    function toggle(force) {
        isOpen = (typeof force === 'boolean') ? force : !isOpen;
        if (!isOpen && hasContent()) return; // v1.01.0: don't allow closing with content, to avoid losing input
        btn.setAttribute('aria-expanded', isOpen);
        if (isOpen) {
            tl.timeScale(1).play();
            setTimeout(function () { if (input) input.focus(); }, 380);
        } else {
            tl.timeScale(1.5).reverse(); // Collapse slightly faster, to feel responsive
        }
    }
    btn.addEventListener('click', function (e) { e.stopPropagation(); toggle(); });
    document.addEventListener('click', function (e) { if (isOpen && !island.contains(e.target)) toggle(false); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && isOpen) { toggle(false); btn.focus(); } });
    bindHistory();   // v1.06.2: after restoring the button-mode expand logic, the history panel binding must be reconnected (since v1.05.4 only called on the no-button path)

    // --- v1.05.1: search history panel show/hide - fully driven by JS.
    // Tested: focus events triggered by JS input.focus() never reach Blazor (@onfocus never fires),
    // so the panel's visibility no longer goes through C# state; instead it toggles the outer container's .history-open class.
    // v1.05.4: extracted into bindHistory(); the no-search-button always-expanded mode binds it too. ---
    function bindHistory() {
    var histWrap = island.closest('.jg-island-wrap');
    if (histWrap && !island.dataset.histBound) {
        island.dataset.histBound = '1';
        var hideTimer = null;
        function showHist() { if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; } histWrap.classList.add('history-open'); }
        function hideHist() { histWrap.classList.remove('history-open'); }
        function hideHistSoon() { if (hideTimer) clearTimeout(hideTimer); hideTimer = setTimeout(hideHist, 220); }
        input.addEventListener('focus', showHist);
        input.addEventListener('click', showHist);
        input.addEventListener('blur', hideHistSoon);
        input.addEventListener('keydown', function (e) { if (e.key === 'Escape') hideHist(); });
        // Keep the panel open while the mouse is over it (it won't flicker closed when focus moved to it)
        histWrap.addEventListener('mouseover', function (e) {
            if (e.target.closest && e.target.closest('.jg-search-history')) { if (hideTimer) clearTimeout(hideTimer); }
        });
        // Clicking a history row / clearing history -> collapse after the action completes (deleting a single row doesn't collapse, for consecutive deletes)
        histWrap.addEventListener('click', function (e) {
            if (!e.target.closest) return;
            if (e.target.closest('.jg-search-history-row') || e.target.closest('.jg-search-history-clearall')) hideHist();
        });
    }
    } // bindHistory()
};

// v0.93.0: detail page back - go back to the origin page (the Blazor Router listens for popstate and takes over navigation)
window.junigridJs.goBack = function () {
    if (window.history.length > 1) window.history.back();
    else window.location.href = "/mods";
};

// v0.93.0: DepthText pointer parallax + idle auto-orbit (slim port of the original React Bits logic)
window.junigridJs.depthTextInit = function (el, tilt) {
    if (!el || el.__dtInit) return; el.__dtInit = true;
    var stage = el.querySelector(".depth-text__stage");
    if (!stage) return;
    var base = { x: -tilt * 0.32, y: tilt * 0.42 };
    var cur = { x: base.x, y: base.y }, tgt = { x: base.x, y: base.y };
    var t0 = performance.now();
    function loop(now) {
        if (!el.__dtHover) {
            var o = ((now - t0) / 1000) * 0.35 * Math.PI * 2;
            tgt.x = base.x + Math.sin(o) * tilt * 0.18;
            tgt.y = base.y + Math.cos(o * 0.85) * tilt * 0.18;
        }
        cur.x += (tgt.x - cur.x) * 0.14;
        cur.y += (tgt.y - cur.y) * 0.14;
        stage.style.transform = "rotateX(" + cur.x.toFixed(3) + "deg) rotateY(" + cur.y.toFixed(3) + "deg)";
        requestAnimationFrame(loop);
    }
    el.addEventListener("pointermove", function (ev) {
        var rc = el.getBoundingClientRect(); if (!rc.width || !rc.height) return;
        el.__dtHover = true;
        var x = Math.max(-1, Math.min(1, (ev.clientX - (rc.left + rc.width / 2)) / (rc.width * 0.8)));
        var y = Math.max(-1, Math.min(1, (ev.clientY - (rc.top + rc.height / 2)) / (rc.height * 0.8)));
        tgt.x = base.x - y * tilt; tgt.y = base.y + x * tilt;
    });
    el.addEventListener("pointerleave", function () { el.__dtHover = false; tgt.x = base.x; tgt.y = base.y; });
    requestAnimationFrame(loop);
};

/* --- v1.00.0: Grainient background (React Bits port, native WebGL2, no ogl dependency) --- */
window.junigridJs = window.junigridJs || {};
(function () {
    var VERT = "#version 300 es\nin vec2 position;\nvoid main() { gl_Position = vec4(position, 0.0, 1.0); }\n";
    var FRAG = `#version 300 es
precision highp float;
uniform vec2 iResolution;
uniform float iTime;
uniform float uTimeSpeed;
uniform float uColorBalance;
uniform float uWarpStrength;
uniform float uWarpFrequency;
uniform float uWarpSpeed;
uniform float uWarpAmplitude;
uniform float uBlendAngle;
uniform float uBlendSoftness;
uniform float uRotationAmount;
uniform float uNoiseScale;
uniform float uGrainAmount;
uniform float uGrainScale;
uniform float uGrainAnimated;
uniform float uContrast;
uniform float uGamma;
uniform float uSaturation;
uniform vec2 uCenterOffset;
uniform float uZoom;
uniform vec3 uColor1;
uniform vec3 uColor2;
uniform vec3 uColor3;
uniform float uLightMode;
out vec4 fragColor;
#define S(a,b,t) smoothstep(a,b,t)
mat2 Rot(float a){float s=sin(a),c=cos(a);return mat2(c,-s,s,c);} 
vec2 hash(vec2 p){p=vec2(dot(p,vec2(2127.1,81.17)),dot(p,vec2(1269.5,283.37)));return fract(sin(p)*43758.5453);} 
float noise(vec2 p){vec2 i=floor(p),f=fract(p),u=f*f*(3.0-2.0*f);float n=mix(mix(dot(-1.0+2.0*hash(i+vec2(0.0,0.0)),f-vec2(0.0,0.0)),dot(-1.0+2.0*hash(i+vec2(1.0,0.0)),f-vec2(1.0,0.0)),u.x),mix(mix(dot(-1.0+2.0*hash(i+vec2(0.0,1.0)),f-vec2(0.0,1.0)),dot(-1.0+2.0*hash(i+vec2(1.0,1.0)),f-vec2(1.0,1.0)),u.x),u.y);return 0.5+0.5*n;}
void mainImage(out vec4 o, vec2 C){
  float t=iTime*uTimeSpeed;
  vec2 uv=C/iResolution.xy;
  float ratio=iResolution.x/iResolution.y;
  vec2 tuv=uv-0.5+uCenterOffset;
  tuv/=max(uZoom,0.001);

  float degree=noise(vec2(t*0.1,tuv.x*tuv.y)*uNoiseScale);
  tuv.y*=1.0/ratio;
  tuv*=Rot(radians((degree-0.5)*uRotationAmount+180.0));
  tuv.y*=ratio;

  float frequency=uWarpFrequency;
  float ws=max(uWarpStrength,0.001);
  float amplitude=uWarpAmplitude/ws;
  float warpTime=t*uWarpSpeed;
  tuv.x+=sin(tuv.y*frequency+warpTime)/amplitude;
  tuv.y+=sin(tuv.x*(frequency*1.5)+warpTime)/(amplitude*0.5);

  vec3 colLav=uColor1;
  vec3 colOrg=uColor2;
  vec3 colDark=uColor3;
  float b=uColorBalance;
  float s=max(uBlendSoftness,0.0);
  mat2 blendRot=Rot(radians(uBlendAngle));
  float blendX=(tuv*blendRot).x;
  float edge0=-0.3-b-s;
  float edge1=0.2-b+s;
  float v0=0.5-b+s;
  float v1=-0.3-b-s;
  vec3 layer1=mix(colDark,colOrg,S(edge0,edge1,blendX));
  vec3 layer2=mix(colOrg,colLav,S(edge0,edge1,blendX));
  vec3 col=mix(layer1,layer2,S(v0,v1,tuv.y));

  vec2 grainUv=uv*max(uGrainScale,0.001);
  if(uGrainAnimated>0.5){grainUv+=vec2(iTime*0.05);} 
  float grain=fract(sin(dot(grainUv,vec2(12.9898,78.233)))*43758.5453);
  col+=(grain-0.5)*uGrainAmount;

  col=(col-0.5)*uContrast+0.5;
  float luma=dot(col,vec3(0.2126,0.7152,0.0722));
  col=mix(vec3(luma),col,uSaturation);
  col=pow(max(col,0.0),vec3(1.0/max(uGamma,0.001)));
  col=clamp(col,0.0,1.0);
  if(uLightMode>0.5){
    float energy=max(max(col.r,col.g),col.b);
    vec3 hue=col/max(energy,0.001);
    float chroma=length(col-vec3(dot(col,vec3(0.333333))));
    float coverage=clamp(0.12+chroma*1.15+energy*0.18,0.0,0.88);
    col=mix(vec3(1.0),clamp(hue*0.58+col*0.18,0.0,1.0),coverage);
  }

  o=vec4(col,1.0);
}
void main(){
  vec4 o=vec4(0.0);
  mainImage(o,gl_FragCoord.xy);
  fragColor=o;
}
`;
    function hexToRgb(h) {
        var r = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(h);
        if (!r) return [1, 1, 1];
        return [parseInt(r[1], 16) / 255, parseInt(r[2], 16) / 255, parseInt(r[3], 16) / 255];
    }
    var states = new WeakMap();
    window.junigridJs.grainientInit = function (sel, opts) {
        var el = document.querySelector(sel);
        if (!el || states.has(el)) return;
        var o = Object.assign({
            color1: '#FFFFFF', color2: '#FB923C', color3: '#F5F5DC',
            timeSpeed: 0.25, colorBalance: 0.0, warpStrength: 1.0, warpFrequency: 5.0,
            warpSpeed: 2.0, warpAmplitude: 50.0, blendAngle: 0.0, blendSoftness: 0.05,
            rotationAmount: 500.0, noiseScale: 2.0, grainAmount: 0.1, grainScale: 2.0,
            grainAnimated: false, contrast: 1.5, gamma: 1.0, saturation: 1.0,
            centerX: 0.0, centerY: 0.0, zoom: 0.9
        }, opts || {});
        var canvas = document.createElement('canvas');
        canvas.style.cssText = 'width:100%;height:100%;display:block;';
        el.appendChild(canvas);
        var gl = canvas.getContext('webgl2', { alpha: true, antialias: false });
        if (!gl) { try { el.removeChild(canvas); } catch (e) { } return; }
        function sh(type, src) {
            var s = gl.createShader(type);
            gl.shaderSource(s, src); gl.compileShader(s);
            return s;
        }
        var prog = gl.createProgram();
        gl.attachShader(prog, sh(gl.VERTEX_SHADER, VERT));
        gl.attachShader(prog, sh(gl.FRAGMENT_SHADER, FRAG));
        gl.linkProgram(prog);
        if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) { try { el.removeChild(canvas); } catch (e) { } return; }
        gl.useProgram(prog);
        var buf = gl.createBuffer();
        gl.bindBuffer(gl.ARRAY_BUFFER, buf);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
        var loc = gl.getAttribLocation(prog, 'position');
        gl.enableVertexAttribArray(loc);
        gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);
        var U = {};
        ['iTime', 'iResolution', 'uTimeSpeed', 'uColorBalance', 'uWarpStrength', 'uWarpFrequency',
         'uWarpSpeed', 'uWarpAmplitude', 'uBlendAngle', 'uBlendSoftness', 'uRotationAmount', 'uNoiseScale',
         'uGrainAmount', 'uGrainScale', 'uGrainAnimated', 'uContrast', 'uGamma', 'uSaturation',
         'uCenterOffset', 'uZoom', 'uColor1', 'uColor2', 'uColor3', 'uLightMode'].forEach(function (n) {
            U[n] = gl.getUniformLocation(prog, n);
        });
        var c1 = hexToRgb(o.color1), c2 = hexToRgb(o.color2), c3 = hexToRgb(o.color3);
        gl.uniform1f(U.uTimeSpeed, o.timeSpeed);
        gl.uniform1f(U.uColorBalance, o.colorBalance);
        gl.uniform1f(U.uWarpStrength, o.warpStrength);
        gl.uniform1f(U.uWarpFrequency, o.warpFrequency);
        gl.uniform1f(U.uWarpSpeed, o.warpSpeed);
        gl.uniform1f(U.uWarpAmplitude, o.warpAmplitude);
        gl.uniform1f(U.uBlendAngle, o.blendAngle);
        gl.uniform1f(U.uBlendSoftness, o.blendSoftness);
        gl.uniform1f(U.uRotationAmount, o.rotationAmount);
        gl.uniform1f(U.uNoiseScale, o.noiseScale);
        gl.uniform1f(U.uGrainAmount, o.grainAmount);
        gl.uniform1f(U.uGrainScale, o.grainScale);
        gl.uniform1f(U.uGrainAnimated, o.grainAnimated ? 1 : 0);
        gl.uniform1f(U.uContrast, o.contrast);
        gl.uniform1f(U.uGamma, o.gamma);
        gl.uniform1f(U.uSaturation, o.saturation);
        gl.uniform2f(U.uCenterOffset, o.centerX, o.centerY);
        gl.uniform1f(U.uZoom, o.zoom);
        gl.uniform3f(U.uColor1, c1[0], c1[1], c1[2]);
        gl.uniform3f(U.uColor2, c2[0], c2[1], c2[2]);
        gl.uniform3f(U.uColor3, c3[0], c3[1], c3[2]);
        gl.uniform1f(U.uLightMode, 0);
        var dpr = Math.min(window.devicePixelRatio || 1, 2);
        function resize() {
            var r = el.getBoundingClientRect();
            var w = Math.max(1, Math.floor(r.width * dpr));
            var h = Math.max(1, Math.floor(r.height * dpr));
            if (canvas.width !== w || canvas.height !== h) {
                canvas.width = w; canvas.height = h;
                gl.viewport(0, 0, w, h);
            }
            gl.uniform2f(U.iResolution, w, h);
        }
        var ro = new ResizeObserver(resize);
        ro.observe(el);
        resize();
        var raf = 0, t0 = performance.now();
        function loop(t) {
            if (!el.isConnected) { ro.disconnect(); states.delete(el); return; }  // Element removed by Blazor -> clean up
            gl.uniform1f(U.iTime, (t - t0) * 0.001);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            raf = requestAnimationFrame(loop);
        }
        raf = requestAnimationFrame(loop);
        states.set(el, { stop: function () { cancelAnimationFrame(raf); ro.disconnect(); } });
    };
    })();




// --- v1.06.7: task dock cursor perspective tilt (same as the gsap cursor-driven-perspective-tilt demo) ---
// The outer rotationX/Y follow the cursor via quickTo; the inner text shifts slightly the opposite way for parallax; resets on leave.
junigridJs.taskDockTilt = function (sel) {
    var el = document.querySelector(sel);
    if (!el || !window.gsap || el.__tiltBound) return;
    el.__tiltBound = true;
    gsap.set(el, { transformPerspective: 650, transformStyle: 'preserve-3d' });
    var inner = el.querySelector('.jg-taskdock-body');
    var outerRX = gsap.quickTo(el, 'rotationX', { ease: 'power3', duration: 0.35 });
    var outerRY = gsap.quickTo(el, 'rotationY', { ease: 'power3', duration: 0.35 });
    var innerX = inner ? gsap.quickTo(inner, 'x', { ease: 'power3', duration: 0.35 }) : null;
    var innerY = inner ? gsap.quickTo(inner, 'y', { ease: 'power3', duration: 0.35 }) : null;
    el.addEventListener('pointermove', function (e) {
        if (el.__dragging) {   // Tilt disabled while dragging, so transforms don't disturb drag positioning
            outerRX(0); outerRY(0);
            if (innerX) innerX(0);
            if (innerY) innerY(0);
            return;
        }
        var r = el.getBoundingClientRect();
        if (!r.width || !r.height) return;
        var nx = (e.clientX - r.left) / r.width;
        var ny = (e.clientY - r.top) / r.height;
        outerRX(gsap.utils.interpolate(10, -10, ny));
        outerRY(gsap.utils.interpolate(-10, 10, nx));
        if (innerX) innerX(gsap.utils.interpolate(-4, 4, nx));
        if (innerY) innerY(gsap.utils.interpolate(-4, 4, ny));
    });
    el.addEventListener('pointerleave', function () {
        outerRX(0); outerRY(0);
        if (innerX) innerX(0);
        if (innerY) innerY(0);
    });
};

// --- v1.07: smooth TaskDock pill <-> circle morph (look of the gsap smooth-morph demo) ---
// When everything is done the pill shrinks into a 56px circle, the text fades, and a white checkmark pops; with new tasks it expands back to a pill.
// The morph itself = eased width/height/padding (border-radius stays 999px; equal width/height naturally makes a circle),
// with power3.inOut for the smooth-morph "jelly" feel. The text stays in the DOM, only opacity animates,
// which keeps Blazor re-renders from replacing nodes and gives content to measure the pill's natural size after collapsing.
junigridJs.taskDockMorph = function (sel, done, animate) {
    var el = document.querySelector(sel);
    if (!el) return;
    if (el.__morphTl) { el.__morphTl.kill(); el.__morphTl = null; }
    var body = el.querySelector('.jg-taskdock-body');
    var check = el.querySelector('.jg-taskdock-check');
    if (!window.gsap) {
        // No-gsap fallback: switch to the final state directly, relying on the CSS transition
        el.classList.toggle('done', !!done);
        return;
    }
    var SIZE = 56;
    if (done) {
        el.classList.add('done');
        if (!animate) {
            gsap.set(el, { width: SIZE, minWidth: SIZE, height: SIZE, minHeight: SIZE, paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0 });
            gsap.set(body, { opacity: 0, scale: 0.6 });
            gsap.set(check, { opacity: 1, scale: 1, rotation: 0 });
            return;
        }
        var tl = gsap.timeline();
        tl.set(body, { opacity: 0 })   // Text vanishes instantly, no fade - done means it just becomes the checkmark
          .to(el, { width: SIZE, minWidth: SIZE, height: SIZE, minHeight: SIZE,
                    paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0,
                    duration: 0.55, ease: 'power3.inOut' }, 0)
          .fromTo(check, { opacity: 0, scale: 0.4, rotation: -30 },
                         { opacity: 1, scale: 1, rotation: 0, duration: 0.45, ease: 'back.out(2.2)' }, 0.28);
        el.__morphTl = tl;
    } else {
        el.classList.remove('done');
        if (!animate) { gsap.set(body, { opacity: 1, scale: 1 }); gsap.set(check, { opacity: 0 }); return; }
        // While collapsed, inline styles override the natural size - remove them, measure the real pill size once, then expand from the circle
        var props = ['width', 'min-width', 'height', 'min-height', 'padding-top', 'padding-bottom', 'padding-left', 'padding-right'];
        var saved = props.map(function (p) { return [p, el.style.getPropertyValue(p), el.style.getPropertyPriority(p)]; });
        props.forEach(function (p) { el.style.removeProperty(p); });
        var w = el.offsetWidth, h = el.offsetHeight;
        var cs = getComputedStyle(el);
        var padT = parseFloat(cs.paddingTop) || 10, padB = parseFloat(cs.paddingBottom) || 10;
        var padL = parseFloat(cs.paddingLeft) || 22, padR = parseFloat(cs.paddingRight) || 22;
        saved.forEach(function (s) { el.style.setProperty(s[0], s[1], s[2]); });
        var back = gsap.timeline({
            onComplete: function () {
                props.forEach(function (p) { el.style.removeProperty(p); });
                el.__morphTl = null;
            }
        });
        back.to(check, { opacity: 0, scale: 0.4, duration: 0.18, ease: 'power2.in' }, 0)
            .fromTo(el, { width: SIZE, minWidth: SIZE, height: SIZE, minHeight: SIZE,
                          paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0 },
                       { width: w, minWidth: w, height: h, minHeight: h,
                         paddingTop: padT, paddingBottom: padB, paddingLeft: padL, paddingRight: padR,
                         duration: 0.55, ease: 'power3.inOut' }, 0)
            .to(body, { opacity: 1, scale: 1, duration: 0.35, ease: 'back.out(1.6)' }, 0.3);
        el.__morphTl = back;
    }
};

// --- v1.06.8: task card "download info" dropdown (same as the gsap easeReverse UI interactions Dropdown) ---
// Elastic arrow rotation + elastic panel height 0->auto expansion + staggered info rows; collapse with easeReverse 2.5x.
// Every click kills the old timeline and rebuilds from the current DOM - during downloads Blazor re-renders frequently and may replace nodes,
// and a cached timeline would point at old nodes, causing "opens but won't close".
junigridJs.taskDrop = function (panelSel, arrowSel, open) {
    var panel = document.querySelector(panelSel);
    var arrow = document.querySelector(arrowSel);
    if (!panel) return;
    if (panel.__tl) { panel.__tl.kill(); panel.__tl = null; }
    if (!window.gsap) {
        panel.style.visibility = open ? 'visible' : 'hidden';
        panel.style.opacity = open ? '1' : '0';
        panel.style.height = open ? 'auto' : '0px';
        if (arrow) arrow.style.transform = open ? 'rotate(180deg)' : 'none';
        return;
    }
    if (open) {
        panel.classList.add('open');
        // v1.07: clear the inline styles after the animation; the steady state is handled by CSS .open (Blazor re-renders don't lose it)
        panel.__tl = gsap.timeline({
            onComplete: function () {
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
                panel.__tl = null;
            }
        })
            .to(arrow, { rotation: 180, duration: 0.9, ease: 'elastic.out(1.2, 0.3)', easeReverse: 'power2.inOut' }, 0)
            .fromTo(panel,
                { height: 0, autoAlpha: 0 },
                { height: 'auto', autoAlpha: 1, duration: 1, ease: 'elastic.out(1.2, 0.3)', easeReverse: 'power3.out' }, 0)
            .from(panel.querySelectorAll('.jg-taskdrop-item'), {
                opacity: 0, x: -20, duration: 0.5,
                ease: 'back.out(3)', easeReverse: 'power2.out', stagger: 0.05
            }, 0.12);
    } else {
        panel.__tl = gsap.timeline({
            onComplete: function () {
                panel.classList.remove('open');
                panel.style.height = '';
                panel.style.visibility = '';
                panel.style.opacity = '';
            }
        })
            .to(arrow, { rotation: 0, duration: 0.4, ease: 'power2.inOut' }, 0)
            .to(panel, { height: 0, autoAlpha: 0, duration: 0.4, ease: 'power2.in' }, 0);
    }
};

// --- v1.06.8: global scroll management (fresh navigation to top + back restores position) ---
// .jg-main is the scroll container shared across pages - this module solves two things:
//  (1) Fresh navigation (clicking links/nav icons/programmatic) -> scroll to zero: nothing used to reset it,
//     so entering Nexus from mid-mod-list opened at the bottom (the position was carried over);
//  (2) Back/forward (popstate) -> return to the position at leave: at the moment of leaving, scrollTop is stored in
//     sessionStorage (keyed by URL); after returning, poll until the new page's content grows, then place.
// Key anti-pollution: during page switches (skeleton rendering, browser clamping scrollTop triggers scroll events)
// use __jgScrollLock to silence all scroll writes - otherwise clamping-induced scroll events write 0 into the snapshot,
// turning "back to original position" into "back to top" (one root cause of returning to the top after closing the downloads page).
// /mods has its own dedicated restore system; skip it during restore to avoid double placement.
(function () {
    var KEY = 'jg:urlscroll';
    function loadMap() { try { return JSON.parse(sessionStorage.getItem(KEY) || '{}'); } catch (e) { return {}; } }
    function saveMap(m) { try { sessionStorage.setItem(KEY, JSON.stringify(m)); } catch (e) { } }
    function urlKey() { return location.pathname + location.search; }
    function isMods(u) { return u === '/mods' || u.indexOf('/mods?') === 0 || u.indexOf('/mods/') === 0; }
    function main() { return document.querySelector('.jg-main'); }

    // Transition lock: all scroll writes silenced within 700ms after navigation (skeleton/clamping phase)
    function lock() {
        window.__jgScrollLock = true;
        clearTimeout(window.__jgScrollLockTimer);
        window.__jgScrollLockTimer = setTimeout(function () { window.__jgScrollLock = false; }, 700);
    }

    // (1) Fresh navigation: save the old page's position -> clear the target page's snapshot -> scroll to zero
    // vNext: /nexus is a "memory page" - leaving and returning via forward navigation must stop where it was:
    // its snapshot is no longer cleared, and after rendering it is placed from the snapshot (restoreFor).
    // /mods uses the page component's own dedicated restore system (restoreFor skips /mods); whether the snapshot is kept doesn't matter.
    // Other pages keep "fresh navigation = back to top".
    var origPush = history.pushState.bind(history);
    history.pushState = function (s, t, u) {
        var targetPath = null;
        try {
            var el = main();
            var m = loadMap();
            // Before leaving: store the current position under the [old URL] (that's what gets restored on return)
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
                el2.classList.remove('jg-restore-pending');   // Clear any unfinished "pending restore" hide from the previous round
                el2.scrollTop = 0;
            }
        } catch (e) { }
        if (targetPath === '/nexus') {
            // vNext: memory page with a scroll snapshot -> set the "pending restore" hide [before] the new page renders (.jg-main persists
            // across navigation, so nexus content is hidden as soon as it renders; the "paint top first" frame can never be painted),
            // revealed the same frame restoreFor places successfully (same idea as /mods' v0.72.4 dedicated system)
            try {
                var sy = loadMap()['/nexus'];
                if (sy && sy > 1 && el2) el2.classList.add('jg-restore-pending');
            } catch (e) { }
            setTimeout(restoreFor, 150);
        }
        return r;
    };

    // (2) Back/forward: set the "back navigation" marker (used by the /mods fallback restore) + lock + queue restore
    // vNext: going back to /nexus with a snapshot -> this listener registers before the Blazor router, so the hide marker is set synchronously;
    // the new nexus content is hidden from its first frame and revealed the same frame restoreFor places (removes the "top then middle" flash)
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

    // vNext: write the given value (or the current actual position) into the current URL's scroll snapshot.
    // Called after a manual refresh (clicking the current page's icon in the top bar) resets to top - without this the snapshot still holds the pre-refresh position,
    // and leaving and returning would restore to the old position rather than the refreshed top.
    window.junigridJs.saveCurrentUrlScroll = function (y) {
        try {
            var el = main();
            var m = loadMap();
            m[urlKey()] = typeof y === 'number' ? y : (el ? el.scrollTop : 0);
            saveMap(m);
        } catch (e) { }
    };

    // (3) Restore: poll up to ~4s; place as soon as content grows; user scrolling yields immediately
    // vNext: works with the "pending restore" hide set at the navigation instant - placed / abandoned / user takeover,
    // all three endings reveal the page the same frame: the top state is never painted, so the "top then jump" flash frames don't exist.
    // restoreSeq token: during rapid consecutive navigation, an older polling round silently yields so two rounds don't rewrite each other's scrollTop.
    var restoreSeq = 0;
    function restoreFor() {
        var url = urlKey();
        if (isMods(url)) return;   // Handled by the /mods dedicated system
        var el = main();
        if (!el) return;
        var y = loadMap()[url];
        if (!y || y < 1) { el.classList.remove('jg-restore-pending'); return; }   // No restorable value: also make sure no hide is left behind
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
            if (my !== restoreSeq) { cleanup(); return; }   // Superseded by a newer restore round: exit silently without touching the new round's hide marker
            if (userTouched || tries > 45 || !el.isConnected) { reveal(); cleanup(); return; }
            // Suppress the page entrance animation transform (it invalidates scrollTop settings)
            el.classList.remove('jg-page-enter');
            if (el.style.transform !== 'none') el.style.transform = 'none';
            var h = el.scrollHeight;
            var limit = h - el.clientHeight;
            stable = (h === lastH) ? stable + 1 : 0;   // Stable-height round count (auto-reset during skeleton/image growth)
            lastH = h;
            if (limit > 0) {
                el.scrollTop = y;
                if (Math.abs(el.scrollTop - y) <= 2) { reveal(); cleanup(); return; }
            }
            // Height stable for many rounds but still short of y (content shorter than at leave) -> finish at the reachable position,
            // don't keep the page hidden forever (stable resets to 0 during loading while height still changes, so no false positive)
            if (stable >= 8) { reveal(); cleanup(); return; }
            timer = setTimeout(tick, 90);
        };
        tick();   // Run the first round immediately (setInterval would wait another 90ms, needlessly extending the blank period)
        function cleanup() {
            clearTimeout(timer);
            el.removeEventListener('wheel', onUser);
            el.removeEventListener('touchstart', onUser);
            el.removeEventListener('pointerdown', onUser);
        }
    }
})();

// ------------------ v1.07.0: log page filter dropdown (same as the GSAP easeReverse demo "Dropdown") ------------------
// Expand: elastic arrow rotation 180° + elastic panel pop (yPercent -30->0 / scale .7->1) + back.out(3) staggered menu items;
// Collapse: the demo's easeReverse/timeScale(2.5) semantics - a smooth power ease-out at ≈2.5x speed (local gsap 3.12 lacks the easeReverse
// property, so an independent closing timeline is used equivalently); .open is removed / inline styles cleared only after the animation fully ends, leaving no white blocks.
(function () {
    function parts(wrapSel) {
        var wrap = document.querySelector(wrapSel);
        if (!wrap) return null;
        return {
            wrap: wrap,
            menu: wrap.querySelector('.jg-sort-menu'),
            arrow: wrap.querySelector('.jg-sort-arrow'),
            items: wrap.querySelectorAll('.jg-sort-item')
        };
    }
    function kill(p) { if (p.wrap.__tl) { p.wrap.__tl.kill(); p.wrap.__tl = null; } }

    function open(p) {
        kill(p);
        p.wrap.__ddOpen = true;
        p.wrap.classList.add('open');
        gsap.set(p.arrow, { rotation: 0 });
        gsap.set(p.menu, { autoAlpha: 0, yPercent: -30, scale: 0.7, transformOrigin: 'top center' });
        gsap.set(p.items, { opacity: 0, x: -20 });
        p.wrap.__tl = gsap.timeline()
            .to(p.arrow, { rotation: 180, duration: 0.9, ease: 'elastic.out(1.2, 0.3)' }, 0)
            .to(p.menu, { autoAlpha: 1, yPercent: 0, scale: 1, duration: 1, ease: 'elastic.out(1.2, 0.3)' }, 0)
            .fromTo(p.items, { opacity: 0, x: -20 },
                { opacity: 1, x: 0, duration: 0.5, ease: 'back.out(3)', stagger: 0.07 }, 0.1);
    }
    function close(p) {
        if (!p.wrap.__ddOpen) return;
        p.wrap.__ddOpen = false;
        kill(p);
        p.wrap.__tl = gsap.timeline({
            onComplete: function () {
                p.wrap.classList.remove('open');
                gsap.set([p.menu, p.arrow, p.items], { clearProps: 'all' });
            }
        });
        p.wrap.__tl
            .to(p.menu, { autoAlpha: 0, yPercent: -14, scale: 0.86, duration: 0.26, ease: 'power3.out' }, 0)
            .to(p.arrow, { rotation: 0, duration: 0.3, ease: 'power2.inOut' }, 0);
    }

    window.junigridJs.logsFilterInit = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p || !p.menu || p.wrap.__filterBound) return;
        p.wrap.__filterBound = true;
        if (!window.gsap) return;   // No gsap: rely on .open + CSS as the fallback toggle
        document.addEventListener('click', function (e) {
            if (p.wrap.__ddOpen && !p.wrap.contains(e.target)) close(p);
        });
    };
    window.junigridJs.logsFilterToggle = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p) return;
        if (!window.gsap) { p.wrap.classList.toggle('open', !p.wrap.__ddOpen); p.wrap.__ddOpen = !p.wrap.__ddOpen; return; }
        p.wrap.__ddOpen ? close(p) : open(p);
    };
    window.junigridJs.logsFilterClose = function (wrapSel) {
        var p = parts(wrapSel);
        if (!p) return;
        if (!window.gsap) { p.wrap.classList.remove('open'); p.wrap.__ddOpen = false; return; }
        close(p);
    };
})();

// ------------------ v0.2.2: GSAP elastic button hover (easeReverse smooth exit) ------------------
// Add class="jg-gsap-btn" to buttons
window.junigridJs.initGsapFx = function () {
    if (!window.gsap) return;
    const hasER = parseFloat(gsap.version) >= 3.13;
    const exitTs = 2.5;

    document.querySelectorAll('.jg-gsap-btn, .jg-path-btn, .jg-nexus-logout-btn, .jg-nexus-login-btn').forEach(function (btn) {
        if (btn.__jgFxBound) return;
        btn.__jgFxBound = true;
        btn.style.transformOrigin = 'center';
        const up = { scale: 1.12, duration: 1.0, ease: 'elastic.out(1.2, 0.3)' };
        if (hasER) up.easeReverse = 'power2.out';
        const tl = gsap.timeline({ paused: true }).to(btn, up, 0);
        btn.addEventListener('mouseenter', function () { tl.timeScale(1).play(); });
        btn.addEventListener('mouseleave', function () { tl.timeScale(exitTs).reverse(); });
    });
};

// ------------------ v0.2.2: PixelCard pixel hover effect for disabled toggle rows (React Bits port) ------------------
window.junigridJs.initPixelHover = function () {
    const COLORS = ['#fecdd3', '#fda4af', '#e11d48'];
    const GAP = 6;

    function Pixel(ctx, x, y, color, speed, delay) {
        this.ctx = ctx; this.x = x; this.y = y; this.color = color;
        this.speed = speed; this.delay = delay;
        this.size = 0;
        this.sizeStep = Math.random() * 0.4;
        this.minSize = 0.5;
        this.maxSize = Math.random() * (this.minSize + 2 - 0.5) + 0.5;
        this.counter = 0;
        this.counterStep = Math.random() * 4 + 10;
        this.isIdle = false; this.isReverse = false; this.isShimmer = false;
    }
    Pixel.prototype.draw = function () {
        const off = 1 - this.size * 0.5;
        this.ctx.fillStyle = this.color;
        this.ctx.fillRect(this.x + off, this.y + off, this.size, this.size);
    };
    Pixel.prototype.appear = function () {
        this.isIdle = false;
        if (this.counter <= this.delay) { this.counter += this.counterStep; return; }
        if (this.size >= this.maxSize) this.isShimmer = true;
        if (this.isShimmer) this.shimmer(); else this.size += this.sizeStep;
        this.draw();
    };
    Pixel.prototype.disappear = function () {
        this.isShimmer = false; this.counter = 0;
        if (this.size <= 0) { this.isIdle = true; return; }
        this.size -= 0.1;
        this.draw();
    };
    Pixel.prototype.shimmer = function () {
        if (this.size >= this.maxSize) this.isReverse = true;
        else if (this.size <= this.minSize) this.isReverse = false;
        if (this.isReverse) this.size -= this.speed; else this.size += this.speed;
    };

    document.querySelectorAll('.jg-switch-row.disabled').forEach(function (row) {
        if (row.__pxBound) return;
        row.__pxBound = true;
        const canvas = document.createElement('canvas');
        canvas.className = 'jg-pixel-canvas';
        row.appendChild(canvas);
        const ctx = canvas.getContext('2d');
        let pixels = [], anim = null, prev = performance.now();

        function init() {
            const w = Math.floor(row.clientWidth), h = Math.floor(row.clientHeight);
            if (!w || !h) return;
            canvas.width = w; canvas.height = h;
            pixels = [];
            for (let x = 0; x < w; x += GAP) {
                for (let y = 0; y < h; y += GAP) {
                    const color = COLORS[Math.floor(Math.random() * COLORS.length)];
                    const dx = x - w / 2, dy = y - h / 2;
                    pixels.push(new Pixel(ctx, x, y, color, 0.08, Math.sqrt(dx * dx + dy * dy)));
                }
            }
        }
        function frame(fn) {
            anim = requestAnimationFrame(function () { frame(fn); });
            const now = performance.now();
            if (now - prev < 1000 / 60) return;
            prev = now;
            ctx.clearRect(0, 0, canvas.width, canvas.height);
            let allIdle = true;
            for (let i = 0; i < pixels.length; i++) {
                pixels[i][fn]();
                if (!pixels[i].isIdle) allIdle = false;
            }
            if (allIdle) cancelAnimationFrame(anim);
        }
        function handle(name) {
            cancelAnimationFrame(anim);
            init();
            if (!pixels.length) return;
            anim = requestAnimationFrame(function () { frame(name); });
        }
        row.addEventListener('mouseenter', function () { handle('appear'); });
        row.addEventListener('mouseleave', function () { handle('disappear'); });
    });
};

// ------------------ v0.2.2: task dock dragging (swallows the click when dragged more than 5px) ------------------
window.junigridJs.makeTaskDockDraggable = function (sel) {
    const el = document.querySelector(sel);
    if (!el || el.__dragBound) return;
    el.__dragBound = true;
    el.style.touchAction = 'none';
    let dragging = false, moved = false;
    let sx = 0, sy = 0, baseL = 0, baseT = 0, w = 0, h = 0;

    // Place the dock's top-left corner at (l, t), recording the offset by "nearest corner"; no longer reads the element rect (the tilt transform pollutes it)
    function place(l, t) {
        const pr = (el.offsetParent || document.body).getBoundingClientRect();
        const maxL = Math.max(0, pr.width - w);
        const maxT = Math.max(0, pr.height - h);
        l = Math.min(Math.max(0, l), maxL);
        t = Math.min(Math.max(0, t), maxT);
        // Key: the opposite offsets must be explicitly set to auto - the stylesheet has right:20px/bottom:20px,
        // and inlining only left makes left+right both apply, forcing absolutely positioned elements to full width (giant ellipse bug)
        el.style.left = el.style.right = el.style.top = el.style.bottom = '';
        if (l + w / 2 <= pr.width / 2) { el.style.left = l + 'px'; el.style.right = 'auto'; }
        else { el.style.right = (pr.width - l - w) + 'px'; el.style.left = 'auto'; }
        if (t + h / 2 <= pr.height / 2) { el.style.top = t + 'px'; el.style.bottom = 'auto'; }
        else { el.style.bottom = (pr.height - t - h) + 'px'; el.style.top = 'auto'; }
    }

    // Re-clamp by the current anchored corner when the window resizes
    function reclamp() {
        const r = el.getBoundingClientRect();
        if (!r.width) return;
        const pr = (el.offsetParent || document.body).getBoundingClientRect();
        const maxL = Math.max(0, pr.width - r.width);
        const maxT = Math.max(0, pr.height - r.height);
        const l = r.left - pr.left, t = r.top - pr.top;
        if (l < 0 || l > maxL || t < 0 || t > maxT) place(Math.min(Math.max(0, l), maxL), Math.min(Math.max(0, t), maxT));
    }
    window.addEventListener('resize', reclamp);

    el.addEventListener('pointerdown', function (e) {
        dragging = true; moved = false;
        sx = e.clientX; sy = e.clientY;
        const r = el.getBoundingClientRect();
        const pr = (el.offsetParent || document.body).getBoundingClientRect();
        baseL = r.left - pr.left; baseT = r.top - pr.top;
        w = r.width; h = r.height;
        try { el.setPointerCapture(e.pointerId); } catch (err) { }
    });
    el.addEventListener('pointermove', function (e) {
        if (!dragging) return;
        const dx = e.clientX - sx, dy = e.clientY - sy;
        if (!moved && (Math.abs(dx) > 5 || Math.abs(dy) > 5)) {
            moved = true;
            el.__dragging = true;   // Disable tilt's cursor tilt
            if (window.gsap) {
                gsap.killTweensOf(el, 'rotationX,rotationY');
                gsap.set(el, { rotationX: 0, rotationY: 0 });
            }
        }
        if (moved) place(baseL + dx, baseT + dy);
    });
    function endDrag() {
        if (!dragging) return;
        dragging = false;
        el.__dragging = false;
        // Let tilt smoothly return to neutral (if the pointer is still over the button, the next move re-takes control)
        el.dispatchEvent(new Event('pointerleave'));
    }
    el.addEventListener('pointerup', endDrag);
    el.addEventListener('pointercancel', endDrag);
    // Swallow the click when a drag ends, to avoid accidentally entering the task page after dragging
    el.addEventListener('click', function (e) {
        if (moved) {
            e.stopImmediatePropagation();
            e.preventDefault();
            moved = false;
        }
    }, true);
};

// ------------------ v0.2.2: memory management slider panel open/close animation (same elastic curves as the dropdown menus) ------------------
// collapseSet: set the state with no animation (used on the first frame); collapseToggle: GSAP elastic open/close
(function () {
    window.junigridJs = window.junigridJs || {};

    function setInstant(el, open) {
        if (!window.gsap) { el.style.display = open ? '' : 'none'; return; }
        gsap.set(el, { display: open ? '' : 'none', height: 'auto', autoAlpha: open ? 1 : 0 });
    }

    window.junigridJs.collapseSet = function (el, open) {
        if (el) setInstant(el, open);
    };

    // Returns a Promise: C# can await it to commit state only after the animation truly finishes
    // (v0.2.2 fix for "won't collapse": clearProps:'all' also cleared Blazor's display:none,
    //  so the panel popped back after the animation; now only height/opacity/visibility are cleared, display is left to Blazor)
    window.junigridJs.collapseToggle = function (el, open) {
        return new Promise(function (resolve) {
            if (!el) { resolve(); return; }
            if (!window.gsap) { el.style.display = open ? '' : 'none'; resolve(); return; }
            gsap.killTweensOf(el);   // On rapid toggle clicking, kill the previous unfinished animation to prevent state fights
            if (open) {
                gsap.set(el, { display: '' });
                gsap.fromTo(el, { height: 0, autoAlpha: 0 },
                    { height: 'auto', autoAlpha: 1, duration: 0.9, ease: 'elastic.out(1.2, 0.3)',
                      clearProps: 'height', onComplete: resolve });
            } else {
                gsap.to(el, { height: 0, autoAlpha: 0, duration: 0.3, ease: 'power2.in',
                    onComplete: function () {
                        gsap.set(el, { clearProps: 'height,opacity,visibility' });
                        el.style.display = 'none';
                        resolve();
                    } });
            }
        });
    };
})();

// ------------------ v0.2.2: elastic slider (GSAP recreation of React Bits ElasticSlider) ------------------
// Pill track grows on hover; rubber-band stretch at both ends with the icon following; elastic snap-back on release.
// Markup contract: .e-slider[data-min,data-max,data-step,data-suffix] > .es-track-wrap > .es-track > .es-fill, with .es-value showing the value on the right
// Value changes call back to Blazor via dotNetRef.OnElasticValue(id, value) to persist settings.
(function () {
    window.junigridJs = window.junigridJs || {};

    function esDecay(value, max) {
        if (max === 0) return 0;
        var entry = value / max;
        var sigmoid = 2 * (1 / (1 + Math.exp(-entry)) - 0.5);
        return sigmoid * max;
    }

    window.junigridJs.elasticInit = function (root, id, startValue, dotNetRef) {
        if (!root || root.__esBound) return;
        root.__esBound = true;

        var min = parseFloat(root.dataset.min) || 0;
        var max = parseFloat(root.dataset.max) || 100;
        var step = parseFloat(root.dataset.step) || 1;
        var suffix = root.dataset.suffix || '';
        var MAX_OVER = 50;

        var track = root.querySelector('.es-track');
        var fill = root.querySelector('.es-fill');
        var valEl = root.querySelector('.es-value');

        var value = Math.min(Math.max(startValue, min), max);
        var dragging = false;
        var region = 'middle';
        var proxy = { o: 0 };

        function round(v) {
            if (step > 0) v = Math.round(v / step) * step;
            return Math.min(Math.max(v, min), max);
        }
        function render() {
            var pct = max > min ? (value - min) / (max - min) * 100 : 0;
            if (fill) fill.style.width = pct + '%';
            if (valEl) valEl.textContent = Math.round(value) + ' ' + suffix;
        }
        function setOverflow(o) {
            if (!track) return;
            if (o <= 0.5 || region === 'middle') {
                track.style.transform = '';
                return;
            }
            var w = track.getBoundingClientRect().width || 1;
            var sy = 1 - (o / MAX_OVER) * 0.2;
            if (region === 'left') {
                track.style.transformOrigin = 'right';
                track.style.transform = 'scaleX(' + (1 + o / w) + ') scaleY(' + sy + ')';
            } else {
                track.style.transformOrigin = 'left';
                track.style.transform = 'scaleX(' + (1 + o / w) + ') scaleY(' + sy + ')';
            }
        }
        function moveTo(e) {
            var rect = track.getBoundingClientRect();
            value = round(min + (e.clientX - rect.left) / rect.width * (max - min));
            var over = 0;
            if (e.clientX < rect.left) { region = 'left'; over = rect.left - e.clientX; }
            else if (e.clientX > rect.right) { region = 'right'; over = e.clientX - rect.right; }
            else region = 'middle';
            proxy.o = esDecay(Math.min(over, 200), MAX_OVER);
            setOverflow(proxy.o);
            render();
        }
        function release() {
            if (!dragging) return;
            dragging = false;
            if (window.gsap && proxy.o > 0.5) {
                gsap.to(proxy, {
                    o: 0, duration: 0.8, ease: 'elastic.out(1, 0.4)',
                    onUpdate: function () { setOverflow(proxy.o); },
                    onComplete: function () { setOverflow(0); }
                });
            } else {
                proxy.o = 0;
                setOverflow(0);
            }
            if (dotNetRef) dotNetRef.invokeMethodAsync('OnElasticValue', id, value);
        }

        root.addEventListener('pointerdown', function (e) {
            dragging = true;
            try { root.setPointerCapture(e.pointerId); } catch (err) { }
            moveTo(e);
        });
        root.addEventListener('pointermove', function (e) { if (dragging) moveTo(e); });
        root.addEventListener('pointerup', release);
        root.addEventListener('pointercancel', release);
        root.addEventListener('lostpointercapture', release);

        render();
    };
})();


// -- About card: CursorGrid cursor grid (React Bits port, canvas underneath) --
junigridJs.initCursorGrid = function (selector) {
    var CFG = { cellSize: 17.5, color: '#d3d3d3', radius: 140, falloff: 'smooth',
        holdTime: 400, fadeDuration: 800, lineWidth: 1.2, maxOpacity: 1,
        fillOpacity: 0, gridOpacity: 0, cellRadius: 0, clickPulse: true, pulseSpeed: 600 };
    var CURVES = {
        linear: function (t) { return t; },
        smooth: function (t) { return t * t * (3 - 2 * t); },
        sharp: function (t) { return t * t * t; }
    };
    var rgb = CFG.color.replace('#', '');
    var col = [parseInt(rgb.slice(0, 2), 16), parseInt(rgb.slice(2, 4), 16), parseInt(rgb.slice(4, 6), 16)];

    document.querySelectorAll(selector || '.jg-cursor-grid').forEach(function (container) {
        if (container.dataset.cgridBound) return;
        container.dataset.cgridBound = '1';
        var canvas = document.createElement('canvas');
        canvas.className = 'jg-cgrid-canvas';
        container.insertBefore(canvas, container.firstChild);
        var ctx = canvas.getContext('2d');
        var dpr = Math.min(window.devicePixelRatio || 1, 2);

        var cols = 0, rows = 0, offX = 0, offY = 0, w = 0, h = 0;
        var alphas = new Float32Array(0), touched = new Float64Array(0);
        var pulses = [], raf = 0, running = false, lastFrame = 0;

        function rebuild() {
            w = container.offsetWidth; h = container.offsetHeight;
            canvas.width = Math.max(1, Math.round(w * dpr));
            canvas.height = Math.max(1, Math.round(h * dpr));
            canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            cols = Math.ceil(w / CFG.cellSize) + 1;
            rows = Math.ceil(h / CFG.cellSize) + 1;
            offX = (w - cols * CFG.cellSize) / 2;
            offY = (h - rows * CFG.cellSize) / 2;
            alphas = new Float32Array(cols * rows);
            touched = new Float64Array(cols * rows);
        }
        function center(i) {
            return [offX + (i % cols) * CFG.cellSize + CFG.cellSize / 2,
                    offY + Math.floor(i / cols) * CFG.cellSize + CFG.cellSize / 2];
        }
        function energize(x, y) {
            var r = Math.max(CFG.radius, 1), ease = CURVES[CFG.falloff], now = performance.now();
            var minC = Math.max(0, Math.floor((x - r - offX) / CFG.cellSize));
            var maxC = Math.min(cols - 1, Math.floor((x + r - offX) / CFG.cellSize));
            var minR = Math.max(0, Math.floor((y - r - offY) / CFG.cellSize));
            var maxR = Math.min(rows - 1, Math.floor((y + r - offY) / CFG.cellSize));
            for (var cR = minR; cR <= maxR; cR++) for (var cC = minC; cC <= maxC; cC++) {
                var i = cR * cols + cC, c = center(i);
                var d = Math.hypot(c[0] - x, c[1] - y);
                if (d > r) continue;
                var lv = ease(1 - d / r) * CFG.maxOpacity;
                if (lv > alphas[i]) { alphas[i] = lv; touched[i] = now; }
                else if (lv > 0) touched[i] = now;
            }
        }
        function draw(now) {
            var dt = Math.min(now - lastFrame, 50); lastFrame = now;
            ctx.clearRect(0, 0, w, h);
            for (var pi = pulses.length - 1; pi >= 0; pi--) {
                var pu = pulses[pi], ringR = ((now - pu.t0) / 1000) * CFG.pulseSpeed;
                if (ringR > Math.hypot(w, h)) { pulses.splice(pi, 1); continue; }
                var band = CFG.cellSize;
                var minC = Math.max(0, Math.floor((pu.x - ringR - band - offX) / CFG.cellSize));
                var maxC = Math.min(cols - 1, Math.floor((pu.x + ringR + band - offX) / CFG.cellSize));
                var minR = Math.max(0, Math.floor((pu.y - ringR - band - offY) / CFG.cellSize));
                var maxR = Math.min(rows - 1, Math.floor((pu.y + ringR + band - offY) / CFG.cellSize));
                for (var cR = minR; cR <= maxR; cR++) for (var cC = minC; cC <= maxC; cC++) {
                    var i = cR * cols + cC, c = center(i);
                    var d = Math.hypot(c[0] - pu.x, c[1] - pu.y);
                    if (Math.abs(d - ringR) < band / 2 && CFG.maxOpacity > alphas[i]) {
                        alphas[i] = CFG.maxOpacity; touched[i] = now;
                    }
                }
            }
            var anyVisible = pulses.length > 0;
            var fadeStep = dt / Math.max(CFG.fadeDuration, 16);
            var half = CFG.cellSize / 2;
            for (var i = 0; i < alphas.length; i++) {
                var a = alphas[i];
                if (a <= 0) continue;
                if (now - touched[i] > CFG.holdTime) {
                    a = Math.max(0, a - fadeStep); alphas[i] = a;
                    if (a <= 0) continue;
                }
                anyVisible = true;
                var cc = center(i);
                var g = ctx.createRadialGradient(cc[0], cc[1], half * 0.1, cc[0], cc[1], CFG.cellSize);
                g.addColorStop(0, 'rgba(' + col + ', ' + a + ')');
                g.addColorStop(1, 'rgba(' + col + ', 0)');
                ctx.beginPath();
                ctx.rect(cc[0] - half + 0.5, cc[1] - half + 0.5, CFG.cellSize - 1, CFG.cellSize - 1);
                if (CFG.fillOpacity > 0) { ctx.fillStyle = 'rgba(' + col + ', ' + (a * CFG.fillOpacity) + ')'; ctx.fill(); }
                ctx.strokeStyle = g; ctx.lineWidth = CFG.lineWidth; ctx.stroke();
            }
            if (anyVisible) raf = requestAnimationFrame(draw);
            else { running = false; ctx.clearRect(0, 0, w, h); }
        }
        function wake() {
            if (running) return;
            running = true; lastFrame = performance.now();
            raf = requestAnimationFrame(draw);
        }
        function local(e) {
            var r = canvas.getBoundingClientRect();
            return [e.clientX - r.left, e.clientY - r.top];
        }
        container.addEventListener('pointermove', function (e) {
            var p = local(e); energize(p[0], p[1]); wake();
        });
        container.addEventListener('pointerdown', function (e) {
            var p = local(e); pulses.push({ x: p[0], y: p[1], t0: performance.now() }); wake();
        });
        if (window.ResizeObserver) new ResizeObserver(function () { rebuild(); wake(); }).observe(container);
        rebuild();
    });
};

// -- About card: LogoLoop icon marquee (React Bits port) --
junigridJs.initLogoLoop = function (selector) {
    var SPEED = 35, TAU = 0.25;
    document.querySelectorAll(selector || '.jg-logoloop').forEach(function (container) {
        if (container.dataset.loopBound) return;
        container.dataset.loopBound = '1';
        var track = container.querySelector('.jg-ll-track');
        var seq = container.querySelector('.jg-ll-seq');
        if (!track || !seq) return;

        var seqW = 0, offset = 0, velocity = 0, target = SPEED;
        var raf = 0, last = null, hovered = false;

        function measure() {
            seqW = seq.getBoundingClientRect().width;
            if (seqW <= 0) return;
            var need = Math.max(2, Math.ceil(container.clientWidth / seqW) + 2);
            var copies = track.querySelectorAll('.jg-ll-seq');
            for (var i = copies.length; i < need; i++) {
                var c = seq.cloneNode(true);
                c.setAttribute('aria-hidden', 'true');
                track.appendChild(c);
            }
        }
        function frame(ts) {
            if (last === null) last = ts;
            var dt = Math.max(0, ts - last) / 1000;
            last = ts;
            var want = hovered ? 0 : SPEED;
            velocity += (want - velocity) * (1 - Math.exp(-dt / TAU));
            if (seqW > 0) {
                offset = ((offset + velocity * dt) % seqW + seqW) % seqW;
                track.style.transform = 'translate3d(' + (-offset) + 'px, 0, 0)';
            }
            raf = requestAnimationFrame(frame);
        }
        container.addEventListener('pointerenter', function () { hovered = true; });
        container.addEventListener('pointerleave', function () { hovered = false; });
        container.addEventListener('click', function (e) {
            var copy = e.target.closest('[data-copy]');
            if (!copy) return;
            e.preventDefault();
            var text = copy.getAttribute('data-copy');
            function toast() {
                var t = document.querySelector('.jg-copy-toast');
                if (!t) {
                    t = document.createElement('div');
                    t.className = 'jg-copy-toast';
                    document.body.appendChild(t);
                }
                t.textContent = 'Email copied';
                t.classList.add('show');
                clearTimeout(t._timer);
                t._timer = setTimeout(function () { t.classList.remove('show'); }, 1800);
            }
            if (navigator.clipboard && navigator.clipboard.writeText) {
                navigator.clipboard.writeText(text).then(toast, toast);
            } else {
                var ta = document.createElement('textarea');
                ta.value = text; document.body.appendChild(ta); ta.select();
                try { document.execCommand('copy'); } catch (err) { }
                document.body.removeChild(ta);
                toast();
            }
        });
        if (window.ResizeObserver) new ResizeObserver(measure).observe(container);
        measure(); // Measure once immediately; recalibrate after images load
        var imgs = seq.querySelectorAll('img');
        imgs.forEach(function (im) {
            if (!im.complete) {
                im.addEventListener('load', measure, { once: true });
                im.addEventListener('error', measure, { once: true });
            }
        });
        setTimeout(measure, 500); // Fallback
        raf = requestAnimationFrame(frame);
    });
};
