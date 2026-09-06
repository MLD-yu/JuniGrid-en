// ============================================================
// Nexus page: search island, back navigation, DepthText parallax text, Grainient WebGL background
// ============================================================

// ─── v0.74.0: Nexus search island (GSAP easeReverse: back.out(2) to expand / power2.out to collapse) ───
// v1.06.2: restore the magnifier button (the button sits right after the Nexus logo; clicking the input expands it to the right); without a button, degrade to always-expanded mode.
junigridJs.searchIslandInit = function (islandId, btnId, inputId) {
    var island = document.getElementById(islandId);
    if (!island || island.dataset.islandBound) return;
    island.dataset.islandBound = "1";
    var field = island.querySelector('.jg-island-field');
    var input = document.getElementById(inputId);
    var btn = document.getElementById(btnId);
    var isOpen = false;
    // No button (always-expanded mode): only bind the search history visibility (bindHistory is a function declaration, so hoisting makes it usable).
    if (!btn) { bindHistory(); return; }

    if (typeof gsap === 'undefined') { // no-GSAP fallback: class toggle
        btn.addEventListener('click', function () {
            isOpen = !isOpen;
            if (!isOpen && input && input.value && input.value.trim().length > 0) { isOpen = true; return; } // v1.01.0: don't collapse when there is content
            island.classList.toggle('open', isOpen);
            if (isOpen && input) input.focus();
        });
        bindHistory();
        return;
    }
    // easeReverse requires GSAP 3.13+; older versions automatically fall back to symmetric easing
    var erOK = parseFloat(gsap.version || '0') >= 3.13;
    gsap.set(island, { width: 40 });
    gsap.set(field, { autoAlpha: 0, width: 0 });
    var tl = gsap.timeline({ paused: true })
        .to(island, { width: 300, duration: 0.7, ease: 'back.out(2)', easeReverse: erOK ? 'power2.out' : undefined }, 0)
        .to(field, { autoAlpha: 1, width: 244, duration: 0.35, ease: 'power2.out', easeReverse: erOK ? 'power2.in' : undefined }, 0.18);

    function hasContent() { return !!(input && input.value && input.value.trim().length > 0); }
    function toggle(force) {
        isOpen = (typeof force === 'boolean') ? force : !isOpen;
        if (!isOpen && hasContent()) return; // v1.01.0: don't allow closing while there is content, to avoid accidentally losing input
        btn.setAttribute('aria-expanded', isOpen);
        if (isOpen) {
            tl.timeScale(1).play();
            setTimeout(function () { if (input) input.focus(); }, 380);
        } else {
            tl.timeScale(1.5).reverse(); // collapse slightly faster so it feels snappy
        }
    }
    btn.addEventListener('click', function (e) { e.stopPropagation(); toggle(); });
    document.addEventListener('click', function (e) { if (isOpen && !island.contains(e.target)) toggle(false); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && isOpen) { toggle(false); btn.focus(); } });
    bindHistory();   // v1.06.2: with button-mode expand logic restored, the history panel binding must be wired back in too (since v1.05.4 it was only called on the no-button path)

    // ─── v1.05.1: search history panel visibility — driven entirely by JS.
    // Tested: the focus event triggered by JS input.focus() never reaches Blazor (@onfocus never fires),
    // so panel visibility no longer goes through C# state; instead we toggle the outer container's .history-open class.
    // v1.05.4: extracted into bindHistory(); the always-expanded mode without a search button binds it too. ───
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
        // keep it open while the mouse is over the panel (focus moving to the panel won't flash it closed)
        histWrap.addEventListener('mouseover', function (e) {
            if (e.target.closest && e.target.closest('.jg-search-history')) { if (hideTimer) clearTimeout(hideTimer); }
        });
        // clicking a history row / Clear history → collapse once the action runs (removing a single row doesn't collapse, so several can be deleted in a row)
        histWrap.addEventListener('click', function (e) {
            if (!e.target.closest) return;
            if (e.target.closest('.jg-search-history-row') || e.target.closest('.jg-search-history-clearall')) hideHist();
        });
    }
    } // bindHistory()
};

// v0.93.0: back from the detail page — fall back to the origin page (the Blazor Router listens for popstate and takes over navigation)
window.junigridJs.goBack = function () {
    if (window.history.length > 1) window.history.back();
    else window.location.href = "/mods";
};

// v0.93.0: DepthText pointer parallax + idle auto-orbit (slimmed-down port of the original React Bits logic)
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

/* ─── v1.00.0: Grainient background (ported from React Bits, native WebGL2 implementation, no ogl dependency) ─── */
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
            if (!el.isConnected) { ro.disconnect(); states.delete(el); return; }  // element removed by Blazor → clean itself up
            gl.uniform1f(U.iTime, (t - t0) * 0.001);
            gl.drawArrays(gl.TRIANGLES, 0, 3);
            raf = requestAnimationFrame(loop);
        }
        raf = requestAnimationFrame(loop);
        states.set(el, { stop: function () { cancelAnimationFrame(raf); ro.disconnect(); } });
    };
    })();




