// ============================================================
// Startup animation / splash screen: logo -> stroked text -> fade out -> UI rises
// ============================================================
// ============================================================
// ============================================================
// Startup animation: centered logo -> slide left -> JuniGrid wordmark stroke-and-fill -> fade out -> UI rises from the bottom
// ============================================================
(function () {
    window.junigridJs = window.junigridJs || {};
    var _splashDone = false;   // animation finished playing
    var _uiReady = false;      // Blazor UI mounted

    function el(id) { return document.getElementById(id); }

    // Fallback: if gsap is not loaded or elements are missing, release the UI directly (never deadlock the app)
    window.junigridJs.splashInit = function () {
        // v0.19.0: The front-end splash has been reduced to an empty shell (display:none); the logo is shown by WPF SplashWindow.
        // This only hides the shell until Blazor finishes mounting, so the main UI does not flash through.
        document.body.classList.add('jg-booting');
    };

    // v0.20.0: Wait until Blazor's first frame is truly stable (two rAF frames + 100ms) before notifying WPF.
    // This avoids the main window fading in showing the dark fallback (#app background color) instead of the light theme.
    window.junigridJs.splashUiReadyWhenStable = function () {
        function stable() {
            requestAnimationFrame(function () {
                requestAnimationFrame(function () {
                    setTimeout(function () { window.junigridJs.splashUiReady(); }, 100);
                });
            });
        }
        // Also wait for the shell DOM to appear (Blazor has mounted but layout may not be settled yet)
        if (document.querySelector('#app .jg-shell')) stable();
        else setTimeout(function () { window.junigridJs.splashUiReadyWhenStable(); }, 30);
    };

    window.junigridJs.splashUiReady = function () {
        _uiReady = true;
        // v0.19.0: The transparent startup animation is fully handled by WPF SplashWindow; the front end here only does two things:
        // 1) notify the WPF host (SplashWindow / App) of ui-ready, and it fades out the Splash and shows the main window;
        // 2) immediately clear jg-booting and release .jg-shell - otherwise, if the animation path above exits early due to
        //    missing elements and never cleans up jg-booting, the whole main UI stays opacity:0/visibility:hidden,
        //    appearing as a "black main window".
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

        // The logo appears right away, without waiting for font measurement - the splash must show up immediately
        gsap.fromTo(logo, { opacity: 0, scale: 0.75 }, { opacity: 1, scale: 1, duration: 0.45 });

        var text = 'JuniGrid';
        var fs = Math.round(Math.max(72, Math.min(window.innerWidth, window.innerHeight) * 0.11));
        var dash = Math.max(fs * 7, 200);

        // Stroke text + fill text (the fill is used for clipping)
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
                if (isStroke) ts.setAttribute('data-draw', '1');  // only stroke glyphs take part in the per-glyph draw
                t.appendChild(ts);
            }
            return t;
        }

        svg.appendChild(strokeText);
        svg.appendChild(fillText);

        // Measure the text width and start playing once fonts finish loading; if fonts.ready is slow to fire (fonts blocked/offline),
        // force-start after 1.4s using the estimate, avoiding "the animation never starts and the page stays stuck on the dark cover".
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

            // Clip piece for the fill text (mask reveals left to right)
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

        // Stroke initial state: the whole dash is hidden away first, then drawn out segment by segment from 0
        gsap.set(strokes, { strokeDasharray: dash, strokeDashoffset: dash });
        gsap.set(wipeRect, { attr: { width: 0 } });
        // Text starts fully transparent - not shown until the logo animation completes
        gsap.set(svg, { opacity: 0 });

        // So that the "logo + text" group is centered, the logo sits exactly over the group's center;
        // at startup the logo first rests at screen center, then slides left (restX) to its intended slot during the animation.
        var stage = el('jg-splash-stage');
        var gap = stage ? (parseFloat(getComputedStyle(stage).gap) || 14) : 14;
        var restX = (vw + gap) + vx * 0;   // vx already includes padding; extra offset = text width + gap
        gsap.set(logo, { x: restX / 2 });

        // Logo slide duration (normal pace)
        var slideDur = 0.9;

        var tl = gsap.timeline({
            defaults: { ease: 'power2.out' },
            onComplete: function () { _splashDone = true; maybeReveal(); }
        });

        // 0) The logo appears immediately via buildWordmark -> rests at center for 0.5s -> then slides left into place
        //    Fix: it used to wait only 0.05s, looking like it "ran off as soon as it appeared"; now it holds for 0.5s before moving
        var startDelay = 0.5;
        tl.to(logo, { x: 0, duration: slideDur, ease: 'linear' }, startDelay);
        // 1) Only after the logo lands does the text fade in, then stroke-draw glyph by glyph + fill
        tl.to(svg, { opacity: 1, duration: 0.35 }, startDelay + slideDur);
        tl.to(strokes, { strokeDashoffset: 0, duration: 1.5, ease: 'power2.inOut', stagger: 0.03 }, startDelay + slideDur + 0.25);
        tl.to(wipeRect, { attr: { width: vw }, duration: 0.9, ease: 'power2.inOut' }, startDelay + slideDur + 0.9);
        // Tail pause: make sure the stroke (1.5s) and fill (0.9s) fully finish before fading out, so the main UI does not peek through early (screenshot 3 bug fix)
        tl.to({}, { duration: 0.8 });
    }

    function maybeReveal() {
        if (!(_splashDone && _uiReady)) return;
        if (!window.gsap) { hideSplash(); return; }
        // Leave the booting state so the app shell becomes visible. Remove .jg-shell's opacity:0!important first;
        // gsap sets opacity:0 in the same frame and slides it in, so there is no white flash.
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
        document.body.classList.remove('jg-booting');   // fallback: never leave the shell permanently hidden
        var splash = el('jg-splash');
        if (splash) { splash.classList.add('hidden'); if (splash.parentNode) splash.parentNode.removeChild(splash); }
    }
})();

// ---- Top nav selection thumb adapts to window size ----
// Resizing redistributes the top nav's flex items, so the .active item's offsetLeft/offsetWidth changes,
// but Blazor does not re-render on resize -> the thumb's left/width would stay at the old values and misalign.
// Listen to the resize event, re-read the real geometry, and reposition.
window.addEventListener('resize', function () {
    if (window.junigridJs && typeof window.junigridJs.placeNavThumb === 'function')
        window.junigridJs.placeNavThumb();
});

