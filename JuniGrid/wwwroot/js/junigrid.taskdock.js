// ============================================================
// Task center floating dock (TaskDock): cursor tilt, pill<->circle morph, download details dropdown, dragging
// ============================================================
// ─── v1.06.7: task dock cursor perspective tilt (same as the gsap cursor-driven-perspective-tilt demo) ───
// ─── v1.06.7: task dock cursor perspective tilt (same as the gsap cursor-driven-perspective-tilt demo) ───
// The outer rotationX/Y follow the cursor smoothly via quickTo; the inner text shifts slightly the opposite way for parallax; resets on leave.
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
        if (el.__dragging) {   // tilt is disabled while dragging so the transform does not interfere with drag positioning
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

// ─── v1.07: TaskDock pill ↔ circle smooth morph (same look as the gsap smooth-morph demo) ───
// When everything is done the whole pill shrinks into a 56px circle, the text fades out and a white checkmark pops in;
// it expands back to a pill when new tasks arrive.
// The morph itself eases width/height/padding (border-radius stays 999px, so equal width and height naturally form a circle),
// paired with power3.inOut for the smooth-morph "jelly" feel. The text stays in the DOM and only its opacity animates,
// which keeps Blazor re-renders from swapping nodes and leaves content to measure the pill's natural size when expanding back.
junigridJs.taskDockMorph = function (sel, done, animate) {
    var el = document.querySelector(sel);
    if (!el) return;
    if (el.__morphTl) { el.__morphTl.kill(); el.__morphTl = null; }
    var body = el.querySelector('.jg-taskdock-body');
    var check = el.querySelector('.jg-taskdock-check');
    if (!window.gsap) {
        // Fallback without gsap: jump straight to the final state and let CSS transitions handle it
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
        tl.set(body, { opacity: 0 })   // the text disappears instantly, no fade — completion snaps straight to the checkmark
          .to(el, { width: SIZE, minWidth: SIZE, height: SIZE, minHeight: SIZE,
                    paddingTop: 0, paddingBottom: 0, paddingLeft: 0, paddingRight: 0,
                    duration: 0.55, ease: 'power3.inOut' }, 0)
          .fromTo(check, { opacity: 0, scale: 0.4, rotation: -30 },
                         { opacity: 1, scale: 1, rotation: 0, duration: 0.45, ease: 'back.out(2.2)' }, 0.28);
        el.__morphTl = tl;
    } else {
        el.classList.remove('done');
        if (!animate) { gsap.set(body, { opacity: 1, scale: 1 }); gsap.set(check, { opacity: 0 }); return; }
        // While shrunk, inline styles override the natural size — remove them, measure the real pill size once, then expand from the circle
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

// ─── v1.06.7: task card "download details" dropdown (same as the Dropdown in gsap easeReverse UI interactions) ───
// Elastic arrow rotation + panel height 0→auto elastic expansion + staggered info rows; collapse uses easeReverse 2.5x.
// Every click kills the old timeline and rebuilds from the current DOM — while downloading, Blazor re-renders can replace nodes,
// and a cached timeline would point at stale nodes leaving the panel stuck open.
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
        // v1.07: clear the inline styles when the animation ends; steady-state display is handled by CSS .open (survives Blazor re-renders)
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

// ------------------ v0.2.2: task dock dragging (dragging more than 5px swallows the click) ------------------
window.junigridJs.makeTaskDockDraggable = function (sel) {
    const el = document.querySelector(sel);
    if (!el || el.__dragBound) return;
    el.__dragBound = true;
    el.style.touchAction = 'none';
    let dragging = false, moved = false;
    let sx = 0, sy = 0, baseL = 0, baseT = 0, w = 0, h = 0;

    // Place the dock's top-left corner at (l, t), anchoring by the nearest corner; no longer reads the element rect (the tilt transform pollutes it)
    function place(l, t) {
        const pr = (el.offsetParent || document.body).getBoundingClientRect();
        const maxL = Math.max(0, pr.width - w);
        const maxT = Math.max(0, pr.height - h);
        l = Math.min(Math.max(0, l), maxL);
        t = Math.min(Math.max(0, t), maxT);
        // Key: the opposite offset must be set to auto explicitly — the stylesheet has right:20px/bottom:20px,
        // so inlining only left makes left+right apply at the same time and the absolutely positioned element gets stretched to full width (giant ellipse bug)
        el.style.left = el.style.right = el.style.top = el.style.bottom = '';
        if (l + w / 2 <= pr.width / 2) { el.style.left = l + 'px'; el.style.right = 'auto'; }
        else { el.style.right = (pr.width - l - w) + 'px'; el.style.left = 'auto'; }
        if (t + h / 2 <= pr.height / 2) { el.style.top = t + 'px'; el.style.bottom = 'auto'; }
        else { el.style.bottom = (pr.height - t - h) + 'px'; el.style.top = 'auto'; }
    }

    // Re-clamp by the current anchor corner when the window is resized
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
        if (e.button !== 0) return;   // v1.1.4: drag with the left button only — the right button is reserved for "hide the dock"
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
            el.__dragging = true;   // disable the tilt cursor effect
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
        // Let tilt ease back to neutral (while the pointer is still over the button, the next move retakes control)
        el.dispatchEvent(new Event('pointerleave'));
    }
    el.addEventListener('pointerup', endDrag);
    el.addEventListener('pointercancel', endDrag);
    // Swallow the click at drag end so finishing a drag does not open the tasks page
    el.addEventListener('click', function (e) {
        if (moved) {
            e.stopImmediatePropagation();
            e.preventDefault();
            moved = false;
        }
    }, true);
};

