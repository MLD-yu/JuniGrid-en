// ============================================================
// Interop core: window.junigridJs base object + top nav thumb + window state
// (Must be loaded first - every other junigrid.*.js file attaches methods to this object)
// ============================================================
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
    // Top nav selection thumb: translate .jg-topnav-thumb under the current .active item (sliding/horizontal toggle)
    placeNavThumb() {
        const nav = document.querySelector('.jg-topnav');
        const thumb = document.querySelector('.jg-topnav-thumb');
        const active = nav && nav.querySelector('.jg-topnav-item.active');
        if (!thumb || !active) return;
        thumb.style.width = active.offsetWidth + 'px';
        thumb.style.left = active.offsetLeft + 'px';
    },
    // v0.33.0: Sort dropdown - both open and close clear the initial state cleanly, preventing leftover white blocks
    // v1.05.0: Right-align the menu when the wrap has .jg-dd-right (dropdowns near the window's right edge no longer overflow); origin is top right
    // v1.07.0: (1) The external overlay .jg-dd-overlay now toggles inside this function in the same frame as the menu - the
    //            overlay used to be filled in by a Blazor re-render, one beat slower than JS; during the "menu open, overlay
    //            missing" window, hover/clicks fell through to the mod cards below;
    //          (2) The animation targets the .jg-sort-menu-in visual inner layer (Nexus page) while the outer box keeps its
    //            final rectangle, so the hit area is at its final position from the first frame of opening; pages without an
    //            inner layer (Logs/Mods) automatically fall back to animating the menu itself
    dropdownToggle(wrapSel, open) {
        const wrap = document.querySelector(wrapSel);
        if (!wrap) return;
        // v1.1.2: Pair and sync by data-dd key - a page may have multiple dropdown overlays (Mods' sort/saves);
        // toggling them all blindly would intercept each other's clicks (the later overlay covers the earlier one and @onclick lands on the wrong Close)
        const ddKey = wrap.dataset.dd;
        document.querySelectorAll('.jg-dd-overlay').forEach(o => o.classList.toggle('open', !!open && o.dataset.dd === ddKey));
        const menu  = wrap.querySelector('.jg-sort-menu');
        const arrow = wrap.querySelector('.jg-sort-arrow');
        const items = wrap.querySelectorAll('.jg-sort-item');
        if (!menu) return;
        const vis = menu.querySelector(':scope > .jg-sort-menu-in') || menu;
        const fromRight = wrap.classList.contains('jg-dd-right');
        const originY = fromRight ? 'top right' : 'top left';

        // Fallback: without gsap, open/close relies on .open + CSS to avoid leftover white blocks
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
                    // Key: only clear inline styles and remove the open class once the animation fully ends, eliminating white blocks
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

    // v0.33.0: Expanding search - animate the width, and on close clearProps hands back to the CSS 40px width
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
                    // Hand back to CSS: width is defined as 40px by .jg-search-x (without .open)
                    gsap.set([wrap, field], { clearProps: 'all' });
                    wrap.classList.remove('open');
                }
            });
            tl.to(field, { autoAlpha: 0, duration: 0.16, ease: 'power2.in' }, 0)
              .to(wrap,  { width: 40, duration: 0.32, ease: 'power2.out' }, 0.05);
            wrap.__srTl = tl;
        }
    },

    // On state changes such as starting/stopping, immediately clear any leftover pixel-dissolve mask (.px-grid)
    // and tilt transform on the launch button, otherwise the leftover white "Click to launch!" would cover
    // the new button text (e.g. "Launching...").
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
    // v0.31.0: PCL-style page entrance - add .jg-page-enter to <main.jg-main> to trigger the CSS keyframes
    playPageEnter() {
        const el = document.querySelector('.jg-main');
        if (!el) return;
        el.classList.remove('jg-page-enter');
        // Force a reflow, then add the class back so the animation can retrigger
        // eslint-disable-next-line no-unused-expressions
        void el.offsetWidth;
        el.classList.add('jg-page-enter');
        // v1.1.2: Refresh back-to-top button visibility after page changes (the route changed, and so did the scroll position)
        if (window.junigridJs.backTopRefresh) window.junigridJs.backTopRefresh();
        // Clear it afterwards to avoid clashing with later interaction animations (longest child delay 290 + duration 420 ≈ 710)
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
            if (e.target.closest && e.target.closest('.jg-upd-btn')) return;   // the update button must not drag the window along
            dotNetRef.invokeMethodAsync('BeginDrag');
        });
        el.addEventListener('dblclick', e => {
            if (e.target.closest && e.target.closest('.jg-upd-btn')) return;
            dotNetRef.invokeMethodAsync('ToggleMaximize');
        });
    }
};

// ---- Added in v0.5.0 ----
// v0.39.0: Pixel Reveal - the login-success card is covered by a pixel curtain;
// the pixels dissolve left to right with random jitter, revealing the avatar/nickname/welcome text underneath
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
    const bg = '#161616';

    // Reveal moment per pixel: normalized x + random jitter mapped into the 0..1 range
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
        // ease: power2.out so the tail end reveals content faster
        const p = 1 - (1 - t) * (1 - t);
        ctx.clearRect(0, 0, w, h);
        for (let r = 0; r < rows; r++) {
            for (let c2 = 0; c2 < cols; c2++) {
                const rt = reveal[r][c2];
                if (p < rt) {
                    // Not yet revealed: solid pixel
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

// v0.36.0: AnimatedList scroll effect (Blazor port of the React Bits AnimatedList)
// - Rows scale 0.7→1 + opacity 0→1 (0.2s) when 50% in view, and collapse when leaving the viewport
// - Top/bottom gradient masks of the scroll container fade in/out with scroll position
window.junigridJs.animatedListInit = function (scrollSel, listSel) {
    const scroller = document.querySelector(scrollSel);
    const list = document.querySelector(listSel);
    if (!scroller || !list) return;

    // -- Row entrance animation: IntersectionObserver, threshold ~0.5, collapse when leaving the viewport (triggerOnce:false) --
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
        // Initial state: collapsed until entering the viewport (only for rows currently outside it; visible rows expand immediately to avoid a flash)
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
// v1.08: Called on filter/search changes - clears the old rows' entrance-animation inline styles
// (opacity/transform written by the IntersectionObserver) so the new filtered results show directly without replaying the whole list animation
window.junigridJs.animatedListReset = function (listSel) {
    const list = document.querySelector(listSel);
    if (!list) return;
    list.querySelectorAll('[data-al]').forEach(el => {
        el.style.opacity = '';
        el.style.transform = '';
        el.style.transition = '';
        if (el.__alBound && list.__alObs) list.__alObs.unobserve(el);
        el.__alBound = false;
    });
};

// v0.35.0: Real-time nav thumb sync - reposition immediately on route change/window resize/refresh (double rAF waits for layout to settle)
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
    // Double rAF: measure only after Blazor has moved .active to the target item and the layout reflow has completed
    requestAnimationFrame(() => requestAnimationFrame(place));
};
// When zoom/font loading changes item widths, the thumb follows in real time (the CSS transition smooths the move, so it never sits misaligned)
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
// v1.1.2: Light/dark theme (JS-side interface for the WPF overlay circular-reveal scheme)
// The switch animation itself lives on the host side (MainWindow.RevealThemeSwitchAsync:
// CapturePreview snapshots the old theme -> the overlay punches a circular hole). This only provides state read/write
// and an instant, animation-free switch (called by C# once the overlay is in place).
// ============================================================
window.junigridJs.getTheme = function () {
    return document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
};
// Apply the theme without animation (takes effect instantly; called by the host once the overlay is in place)
window.junigridJs.applyTheme = function (theme) {
    var t = theme === 'dark' ? 'dark' : 'light';
    document.documentElement.dataset.theme = t;
    try { localStorage.setItem('jg-theme', t); } catch (e) { }
    return t;
};
