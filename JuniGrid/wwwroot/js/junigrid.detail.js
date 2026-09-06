// ============================================================
// Mod detail page: image click-to-zoom, accordion animations,
// task timeline morphing, dropdown "click outside to close"
// fallback, back to top
// ============================================================
// ============ v0.59.0: mod detail page image click-to-zoom (with an in-place placeholder to prevent collapse) ============
// Click image → leave a same-size placeholder in the original spot (keeps the box) → move the
// image to the center of the modal for a simple zoom;
// Close → put the image back in place and remove the placeholder.
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
            // put back in the original position (located via the placeholder)
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
            // create the placeholder: holds the original box size, prevents collapse
            var rect = el.getBoundingClientRect();
            placeholder = document.createElement('div');
            placeholder.className = 'jg-zoom-placeholder';
            placeholder.style.width = rect.width + 'px';
            placeholder.style.height = rect.height + 'px';
            // inherit the margin so spacing above/below stays consistent
            var cs = window.getComputedStyle(el);
            placeholder.style.margin = cs.margin;
            placeholder.style.display = cs.display === 'inline' ? 'inline-block' : cs.display;
            openParent.insertBefore(placeholder, el);
            // move the image to the center of the modal
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
                if (car && car.__justDragged) return;   // releasing after a drag shouldn't trigger the zoom
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


// ============ v0.69.0: detail page parallax image carousel (wheel horizontal scroll + drag; scrolling pauses while zoomed) ============
(function () {
    if (!window.junigridJs) window.junigridJs = {};
    })();

// v0.69.9: accordion on-demand scrolling — measure the real content height after expanding; add .jg-acc-scroll to .jg-acc only when it exceeds 360px
window.junigridJs = window.junigridJs || {};
window.junigridJs.accMeasureScroll = function () {
    var accs = document.querySelectorAll(".jg-acc.open");
    for (var i = 0; i < accs.length; i++) {
        var acc = accs[i];
        var inner = acc.querySelector(".jg-acc-inner");
        if (!inner) continue;
        // header + real content height (scrollHeight ignores max-height)
        if (inner.scrollHeight > 360) acc.classList.add("jg-acc-scroll");
        else acc.classList.remove("jg-acc-scroll");
    }
    var closed = document.querySelectorAll(".jg-acc:not(.open)");
    for (var j = 0; j < closed.length; j++) closed[j].classList.remove("jg-acc-scroll");
};

// v0.70.0: accordion GSAP elastic animation (based on the easeReverse demo) —
// expand: arrow elastic rotation + panel elastic expansion + rows entering staggered via back.out;
// collapse: quick power-family retraction (≈2.5x exit speed), then measure the scrollbar on demand when done.
window.junigridJs = window.junigridJs || {};
window.junigridJs.accAnimate = function (id, opening) {
    var acc = document.getElementById(id);
    if (!acc) return;
    var inner = acc.querySelector(".jg-acc-inner");
    var arrow = acc.querySelector(".jg-acc-arrow");
    if (!inner) return;
    if (typeof gsap === "undefined") {           // without GSAP, degrade to instant show/hide
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
// ============ v1.1.3: task timeline — pixel square → checkmark morph (smooth-morph style) ============
// A Blazor re-render swaps the previous row's pixel grid straight for the checkmark SVG; a
// MutationObserver catches that exact moment: first cover the icon with a 3x3 pixel ghost
// (still visually present at the instant Blazor swaps it), converge the ghost pixels toward
// the center and fade them out while the new checkmark pops in via back.out + stroke draw-in
// — the net effect is a smooth morph.
window.junigridJs.taskTimelineWatch = function (panelSel) {
    var panel = document.querySelector(panelSel);
    if (!panel || panel.__tlWatch) return;
    panel.__tlWatch = true;
    var hadGrid = new WeakMap();   // icon element → previous frame was a pixel square

    function scan() {
        panel.querySelectorAll('.jg-tl-icon').forEach(function (ic) {
            var check = ic.querySelector('.jg-tl-check');
            if (check) {
                if (hadGrid.get(ic) && !check.__morphed) { check.__morphed = true; morph(ic, check); }
                hadGrid.delete(ic);
            } else if (ic.querySelector('.jg-pxgrid')) {
                hadGrid.set(ic, true);
            }
        });
    }

    function morph(icon, check) {
        if (!window.gsap) return;
        var row = icon.closest('.jg-tl-row');
        var color = (row && getComputedStyle(row).getPropertyValue('--c')) || '#1a9c5b';
        // 1) pixel ghost: a full 3x3 block covering the icon, converging toward the center and fading out
        var ghost = document.createElement('div');
        ghost.className = 'jg-pxgrid';
        ghost.style.cssText = 'position:absolute;left:50%;top:50%;width:max-content;transform:translate(-50%,-50%);pointer-events:none;filter:drop-shadow(0 0 4px ' + color + ');';
        var cells = [];
        for (var c = 0; c < 9; c++) {
            var sp = document.createElement('i');
            sp.style.cssText = 'opacity:1;transform:scale(1);animation:none;--c:' + color + ';';
            ghost.appendChild(sp); cells.push(sp);
        }
        icon.appendChild(ghost);
        var tl = gsap.timeline({ onComplete: function () { ghost.remove(); } });
        cells.forEach(function (sp, i) {
            tl.to(sp, {
                x: (1 - i % 3) * 4, y: (1 - Math.floor(i / 3)) * 4,
                scale: 0, opacity: 0, duration: .3, ease: 'power2.in'
            }, i * 0.018);
        });
        // 2) checkmark pop-in + stroke draw-in (arc drawn first, check drawn last)
        gsap.fromTo(check, { scale: .2, opacity: 0, rotation: -30 },
            { scale: 1, opacity: 1, rotation: 0, duration: .45, ease: 'back.out(2.4)', clearProps: 'transform,opacity' });
        var shapes = check.querySelectorAll('path');
        shapes.forEach(function (p, i) {
            var len = p.getTotalLength ? p.getTotalLength() : 60;
            gsap.fromTo(p, { strokeDasharray: len, strokeDashoffset: len },
                { strokeDashoffset: 0, duration: .3, ease: 'power2.out', delay: .06 + i * .15 });
        });
    }

    var obs = new MutationObserver(function () { scan(); });
    obs.observe(panel, { childList: true, subtree: true });
    scan();
};


// ============ v1.1.2: global fallback for dropdown "click outside to close" ============
// The overlay (.jg-dd-overlay) may fail to catch real clicks in some scenarios (hovered elements
// raising their stacking order, hit-testing timing, etc.), so this adds a fallback on document:
// when an overlay is open and the click lands outside every dropdown container → click the open
// overlay ourselves, going through its own @onclick (CloseSort/CloseProfile/CloseAllDd) to close.
// Two kinds of clicks are left alone: clicks inside a dropdown container (trigger + menu), and
// clicks on the overlay itself (already handled by the overlay's own @onclick; skipping them also
// avoids the synthetic click re-entering this listener recursively).
(function () {
    if (window.__ddOutsideBound) return;
    window.__ddOutsideBound = true;
    document.addEventListener('click', function (e) {
        var openOvs = document.querySelectorAll('.jg-dd-overlay.open');
        if (!openOvs.length) return;
        var t = e.target;
        if (!t || !t.closest) return;
        if (t.closest('.jg-sort-dd, .jg-profile-dd')) return;   // click inside the dropdown itself
        if (t.closest('.jg-dd-overlay')) return;                // click on the overlay (it closes itself)
        openOvs.forEach(function (o) { o.click(); });
    });
})();

// ============ v1.1.2: back to top (bottom-left, smooth scroll; on Mod management / Mod detail / Nexus) ============
// v1.1.3: supports vertical dragging to reposition — the default position covers the last mod cover
// in the list, so press and drag it to any height.
// The position is stored in localStorage (records the "offset from the bottom of the content area",
// recomputed from that offset and clamped back on-screen when the window height changes).
// Moving <5px still counts as a click and doesn't scroll back to top; a real drag swallows the
// subsequent click.
window.junigridJs.backTopInit = function () {
    var scroller = document.querySelector('.jg-main');
    var host = document.querySelector('.jg-content');
    var btn = document.getElementById('jgBackTop');
    if (!scroller || !btn || btn.__backTopBound) return;
    btn.__backTopBound = true;
    var POS_KEY = 'jg:backtop:bottom';
    // clamp, then apply the bottom offset (top:auto keeps bottom positioning)
    function applyBottom(b) {
        var h = host || document.body;
        var minB = 8;
        var maxB = Math.max(minB, h.clientHeight - btn.offsetHeight - 8);
        b = Math.min(Math.max(b, minB), maxB);
        btn.style.top = 'auto';
        btn.style.bottom = b + 'px';
        return b;
    }
    // restore the position after the last drag
    try {
        var saved = parseFloat(localStorage.getItem(POS_KEY));
        if (!isNaN(saved)) applyBottom(saved);
    } catch (e) { }
    // window height changed → re-clamp from the stored offset so the button never hangs off-screen
    window.addEventListener('resize', function () {
        try {
            var v = parseFloat(localStorage.getItem(POS_KEY));
            if (!isNaN(v)) applyBottom(v);
        } catch (e) { }
    });
    function update() {
        var p = location.pathname || '/';
        // only on Mod management / Mod detail / Nexus (including all subviews); and only when not at the top
        var ok = p === '/mods' || p.indexOf('/mod/') === 0 || p.indexOf('/nexus') === 0;
        btn.classList.toggle('show', ok && scroller.scrollTop > 260);
    }
    scroller.addEventListener('scroll', update, { passive: true });
    window.junigridJs.backTopRefresh = update;

    // ── vertical drag (pointer capture; .dragging in CSS disables transitions to prevent jumps) ──
    var dragging = false, moved = false, startY = 0, startB = 0;
    btn.addEventListener('pointerdown', function (e) {
        if (e.button !== 0) return;
        dragging = true; moved = false;
        startY = e.clientY;
        var r = btn.getBoundingClientRect();
        var hr = (host || document.body).getBoundingClientRect();
        startB = hr.bottom - r.bottom;   // the button's current equivalent bottom offset
        try { btn.setPointerCapture(e.pointerId); } catch (err) { }
    });
    btn.addEventListener('pointermove', function (e) {
        if (!dragging) return;
        var dy = e.clientY - startY;
        if (!moved && Math.abs(dy) < 5) return;   // tiny movement isn't a drag, leave it for click
        if (!moved) { moved = true; btn.classList.add('dragging'); }
        applyBottom(startB - dy);   // dragging up (dy<0) → bottom increases
        e.preventDefault();
    });
    function endDrag(e) {
        if (!dragging) return;
        dragging = false;
        try { btn.releasePointerCapture(e.pointerId); } catch (err) { }
        if (!moved) return;
        btn.__jgDragged = true;   // swallow the synthetic click on release so it doesn't accidentally trigger scroll-to-top
        try { localStorage.setItem(POS_KEY, String(parseFloat(btn.style.bottom) || 0)); } catch (err) { }
        // remove .dragging on the next frame: removing it in the same frame restores transitions immediately and the transform jumps
        requestAnimationFrame(function () { btn.classList.remove('dragging'); });
    }
    btn.addEventListener('pointerup', endDrag);
    btn.addEventListener('pointercancel', endDrag);

    btn.addEventListener('click', function () {
        if (btn.__jgDragged) { btn.__jgDragged = false; return; }
        // tween a proxy object's y with GSAP → write it back to scrollTop every frame (smooth scroll
        // to top; GSAP can't directly tween non-style properties like scrollTop on DOM elements)
        if (window.gsap) {
            var proxy = { y: scroller.scrollTop };
            gsap.to(proxy, {
                y: 0, duration: 0.6, ease: 'power2.inOut',
                onUpdate: function () { scroller.scrollTop = proxy.y; }
            });
        } else {
            scroller.scrollTo({ top: 0, behavior: 'smooth' });
        }
    });
    update();
};


// v0.71.1: wait until the container's scrollHeight is big enough before setting scrollTop (won't be
// clamped back to the top when unloaded images leave the height too small)
// v0.71.6: guard against double interference — 1) update detection sets the list to display:none
// (scrollHeight collapses to 0 during the skeleton phase)
// 2) at the moment of going back, playPageEnter wraps .jg-main in a transform enter animation, and the transform will make
