Shader "Hidden/BuckshotPost"
{
    Properties
    {
        // DISPLAY-NAME CONVENTION (keep this consistent when adding properties):
        //   - The display name must be DERIVABLE BY EYE from the reference name: drop the group
        //     prefix, add units, space/title-case it. NEVER substitute a different noun.
        //     "_EdgeStrength" -> "Strength" is fine (the header supplies "Edge").
        //     "_DitherPixelScale" -> "Cell Size" is NOT -- you cannot find it when someone says
        //     _DitherPixelScale. That mismatch cost two round trips on 2026-07-20; don't reintroduce it.
        //   - [Header()] carries the SCOPE, which is what buys room to drop the prefix.
        //   - Parentheses carry UNITS ONLY -- (px), (m). Never a type, never a scope hint.
        //   - Keep display names under ~28 chars or Unity truncates them in the inspector.
        //   - [ToggleUI] for on/off floats: renders a checkbox but keeps the float, so the existing
        //     `if (_X > 0.5)` branches still work. Plain [Toggle] would generate a shader keyword and
        //     require a #pragma shader_feature plus rewriting those branches.
        // Reference names (_Exposure etc.) are NOT changed by any of this -- Unity matches serialized
        // material values by reference name, so reordering and relabelling is purely cosmetic.

        [Header(Tone)]
        // NEUTRAL BY DESIGN (2026-07-20): nothing drives these at runtime any more. The project gets
        // its lobby-vs-dungeon consistency from LIGHTING, not from a per-area post grade, so these sit
        // at identity and the look comes from the actual lights.
        _Exposure        ("Exposure",   Range(0.5, 2.0)) = 1.0
        _Contrast        ("Contrast",   Range(0.5, 2.0)) = 1.0
        _Saturation      ("Saturation", Range(0.0, 2.0)) = 0.95
        // ⚠ Applied BEFORE posterize, so it doesn't just brighten -- it RELOCATES dark content upward
        // into levels 1-3, where the level-to-level ratio is 2^_QuantizeGamma (~4.6x) and quantisation
        // is at its most visible. That's what caused the dungeon's dark band. Left at 1.0 (identity).
        // If you ever need it, prefer _Exposure for general brightness -- a multiply preserves
        // relative differences between neighbouring pixels, whereas this gamma curve EXPANDS them
        // (its derivative blows up near black), spreading smooth dark gradients across more levels.
        _ShadowLift      ("Shadow Lift", Range(1.0, 3.0)) = 1.0

        [Header(Posterize and Dither)]
        [ToggleUI] _EnablePosterize ("Enable", Float) = 1
        // Overall band count. NOTE: this does NOT fix dark banding -- the ratio between the first two
        // levels above black is 2^_QuantizeGamma regardless of how many levels there are. Treat this as
        // a look knob for the midtones; dark bands are broken by dither.
        _ColorLevels     ("Color Levels", Range(2.0, 64.0)) = 16.0
        // Perceptual curve. Levels land at (k/(N-1))^gamma, so the first-to-second-level RATIO is
        // 2^gamma -- 2.2 -> 4.6x, 3.0 -> 8x. Vision judges ratios, so HIGHER gamma makes the darkest
        // band MORE visible, not less. Lower it if the dungeon's darkest band reads.
        _QuantizeGamma   ("Quantize Gamma", Range(1.0, 3.0)) = 2.2
        // 0 = hard bands; 1 = nearly continuous. Largely redundant alongside dither -- both soften the
        // same boundary. Enoch prefers ~0 (hard edges, authentic).
        // Max band-edge softening, applied ONLY in flat regions (see _SoftnessGradientRef). 0 = hard
        // bands everywhere. ~0.5 blurs the fog's faceted contours without touching detailed surfaces.
        _PosterizeSoftness ("Posterize Softness", Range(0.0, 1.0)) = 0.5
        // Gradient at which softening reaches ZERO (bands go fully hard). Softening ramps from full at
        // a flat 0 gradient down to none here. LOWER = only the very flattest areas soften, so more of
        // the image keeps hard bands. RAISE if faceting still shows on gently-shaded surfaces.
        _SoftnessGradientRef ("Softness Gradient Ref", Range(0.005, 0.5)) = 0.05
        // Controls what FRACTION of pixels get randomised, so it's a banding-vs-noise trade, not a
        // correctness setting. 1.0 = every pixel probabilistic: no banding anywhere, but noise across
        // the WHOLE frame (and the `lq/l` divide amplifies that noise most in the darks). 0.1 = only
        // pixels within 5% of a boundary move, so flat areas stay clean. A dark foggy dungeon is mostly
        // large smooth low-luminance regions, so low amplitude wins here -- 0.1 is Enoch's tuned value.
        _DitherStrength  ("Dither Strength", Range(0.0, 1.0)) = 0.1
        // Bayer cell size in real pixels, INTEGER only (floored in the frag). One cell per pixel at 1,
        // so an 8x8 tile spans 8x8 screen pixels and the eye averages it instead of resolving it.
        // Fractional values would put the tile out of phase with the display grid and beat into bars.
        // DungeonPostGrade drives this from Screen.height (1080p -> 1, 4K -> 2) to hold physical size.
        _DitherPixelScale ("Dither Pixel Scale", Range(1.0, 4.0)) = 1.0
        // Luminance floor for the final rescale. `col *= lq/l` divides by luminance, so near black a
        // single quantize step becomes an enormous multiplier -> the classic dark speckle/amplification.
        // Clamping the divisor kills that. Raise if darks still over-fire; lower for more dark contrast.
        _LumFloor        ("Lum Floor", Range(0.0005, 0.05)) = 0.004
        // Below this PERCEPTUAL luminance (same lp space the quantiser works in, so it reads as a
        // fraction of the visible range rather than of linear luminance) posterize is faded out and
        // the original smooth gradient passes through -- killing the darkest band, which no amount of
        // _ColorLevels or _QuantizeGamma can fix. 0 = off (quantise everything, old behaviour).
        _PosterizeFloor     ("Posterize Floor",      Range(0.0, 0.5)) = 0.12
        // Width of the fade above the floor. Too narrow and the switch from smooth to quantised reads
        // as a seam; too wide and the posterize look is diluted across a large part of the range.
        _PosterizeFadeWidth ("Posterize Fade Width", Range(0.01, 0.5)) = 0.12

        [Header(Edge Outlines)]
        [ToggleUI] _EnableEdges ("Enable", Float) = 1
        _EdgeStrength        ("Strength",        Range(0.0, 1.0))  = 0.5
        _EdgeThickness       ("Thickness (px)",  Range(0.5, 4.0))  = 1.0
        // Virtual resolution -- sizes the outline TAP SPACING so thickness is consistent on any
        // monitor. Edge-only: pixelation is gone and dither now keys off _DitherPixelScale instead.
        _PixelHeight         ("Pixel Height",    Float)            = 240.0
        _DepthEdgeThreshold  ("Depth Threshold", Range(0.005, 1.0)) = 0.05
        _NormalEdgeThreshold ("Normal Threshold",Range(0.1, 2.0))   = 0.6
        _DepthNormalThreshold      ("Depth Normal Threshold",       Range(0.0, 1.0))  = 0.5
        _DepthNormalThresholdScale ("Depth Normal Threshold Scale", Range(1.0, 20.0)) = 7.0
        _EdgeFadeStart       ("Fade Start (m)",  Float)            = 15.0
        _EdgeFadeEnd         ("Fade End (m)",    Float)            = 40.0
        // Fades outlines out where the image is already BRIGHT, so edges of geometry BEHIND a transparent
        // volumetric light (which doesn't write depth/normals -> the edge pass still sees it) don't draw
        // over the glow. 0 = off (outlines everywhere, old behaviour). Raise toward 1 to clear them from lit pools.
        _EdgeBrightFade      ("Fade In Light",   Range(0.0, 1.0)) = 0.0
        // Luminance window the "Fade In Light" ramps across. Outlines fade from none at Lum Low to full
        // (scaled by _EdgeBrightFade) at Lum High. LOWER these to reach further DOWN a dim volumetric cone
        // (not just the bright bulb core); raise to confine the fade to only the brightest pools. Since the
        // fog is composited into the image (not sampled separately), this brightness window is how you clear
        // outlines from the WHOLE cone without a RenderGraph fog-buffer read.
        _EdgeFadeLumLow      ("Fade Lum Low",    Range(0.0, 0.5)) = 0.06
        _EdgeFadeLumHigh     ("Fade Lum High",   Range(0.0, 1.0)) = 0.22

        [Header(Split Tone)]
        // Optional colour cast -- off by default; dial Strength up for a warm/cold split.
        _ShadowTint        ("Shadow Tint",    Color)           = (0.78, 0.85, 1.0, 1.0)
        _HighlightTint     ("Highlight Tint", Color)           = (1.0, 0.88, 0.72, 1.0)
        _SplitToneStrength ("Strength",       Range(0.0, 1.0)) = 0.0

        [Header(Color filter)]
        // A flat multiply over the whole image -- the honest way to TINT (saturation just greys,
        // contrast just darkens). Baseline white = no effect; ScreenFeedback drives it toward amber as
        // you tire, for the Apeirophobia-style brown exertion wash.
        _ColorFilter       ("Color Filter", Color) = (1, 1, 1, 1)

        [Header(Vignette)]
        // Driven at RUNTIME by ScreenFeedback (wake-up eyelid, low-stamina edge, hurt pulse). Baseline
        // 0 = off; the game leaves it there and animates it per-effect. As Amount rises the clear
        // radius shrinks, so the darkness closes inward rather than just deepening.
        _Vignette          ("Vignette Amount",   Range(0.0, 1.0)) = 0.0
        _VignetteSoftness  ("Vignette Softness", Range(0.05, 1.0)) = 0.4
        _VignetteColor     ("Vignette Color",    Color)           = (0, 0, 0, 1)
        // 1 = circular. Higher makes the clear area WIDER than tall, so the darkness closes from top and
        // bottom into a horizontal slit -- eyelids. ~2.5-3 reads as an eye. Shared by every vignette
        // effect (wake-up eye, low-stamina edge), so both take this shape.
        _VignetteAspect    ("Vignette Eye Shape", Range(1.0, 5.0)) = 2.5

        [Header(Wake blur)]
        // Script-driven by ScreenFeedback: a full-screen blur (radius in virtual px) that eases to 0 as you
        // come to. 0 = sharp (no cost -- the blur taps are branched out when it's off).
        _WakeBlur          ("Wake Blur (px)", Range(0.0, 20.0)) = 0.0

        [Header(Chromatic Aberration)]
        // RADIAL R/B channel split, measured in virtual px AT THE FRAME EDGE. Grows from 0 at centre to
        // full at the corners (how a real lens fringes) -- uniform CA reads as an error, this reads as glass.
        // Cheap (2 extra taps) and branched, so 0 = no cost. Subtle is ~0.5-1.5; CRT/VHS grunge ~2-4.
        _ChromaticAberration ("Amount (edge px)", Range(0.0, 6.0)) = 0.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "BuckshotPost"

            HLSLPROGRAM
            // Core gives us URP macros, _Time, _ScreenParams, etc.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // Blit gives us the fullscreen Vert(), the Varyings struct (with .texcoord),
            // and the declarations for _BlitTexture + sampler_LinearClamp.
            // NOTE: in URP 17 (Unity 6) this lives in the CORE package, not universal.
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            // Scene depth + normals — needed for edge-detection outlines. Requires the
            // Full Screen Pass feature's Requirements to include Depth and Normal.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            // Material-set parameters. Declared here so the Full Screen Pass
            // Renderer Feature's material inspector can drive them.
            float  _Exposure;
            float  _Contrast;
            float  _Saturation;
            float  _ShadowLift;

            float  _PixelHeight;
            float  _DitherPixelScale;

            float  _EnablePosterize;
            float  _ColorLevels;
            float  _DitherStrength;
            float  _LumFloor;
            float  _PosterizeFloor;
            float  _PosterizeFadeWidth;
            float  _QuantizeGamma;
            float  _PosterizeSoftness;
            float  _SoftnessGradientRef;

            float4 _ShadowTint;
            float4 _HighlightTint;
            float  _SplitToneStrength;

            float4 _ColorFilter;

            float  _Vignette;
            float  _VignetteSoftness;
            float4 _VignetteColor;
            float  _VignetteAspect;
            float  _WakeBlur;
            float  _ChromaticAberration;

            float  _EnableEdges;
            float  _EdgeStrength;
            float  _EdgeThickness;
            float  _DepthEdgeThreshold;
            float  _NormalEdgeThreshold;
            float  _DepthNormalThreshold;
            float  _DepthNormalThresholdScale;
            float  _EdgeFadeStart;
            float  _EdgeFadeEnd;
            float  _EdgeBrightFade;
            float  _EdgeFadeLumLow;
            float  _EdgeFadeLumHigh;

            // ---- helpers ---------------------------------------------------

            // 8x8 ordered (Bayer) dither threshold in [0,1).
            //
            // Why 8x8 and not 4x4: a 4x4 matrix has only 16 threshold levels, and at a quantize
            // boundary the cells >= 8 form a perfect 2x2 CHECKERBOARD -- which is what you see as an
            // obvious grid seam along every band edge. 8x8 gives 64 levels, so the transition
            // dissolves into a much finer, far less structured pattern.
            //
            // Ordered (not error-diffusion) is deliberate: the pattern is fixed in screen space, so
            // it's temporally STABLE. Error diffusion looks better in stills and boils horribly in
            // motion, which is unusable for a first-person game.
            float Bayer8x8(float2 p)
            {
                int x = (int)fmod(p.x, 8.0);
                int y = (int)fmod(p.y, 8.0);
                const int m[64] = {
                     0, 32,  8, 40,  2, 34, 10, 42,
                    48, 16, 56, 24, 50, 18, 58, 26,
                    12, 44,  4, 36, 14, 46,  6, 38,
                    60, 28, 52, 20, 62, 30, 54, 22,
                     3, 35, 11, 43,  1, 33,  9, 41,
                    51, 19, 59, 27, 49, 17, 57, 25,
                    15, 47,  7, 39, 13, 45,  5, 37,
                    63, 31, 55, 23, 61, 29, 53, 21
                };
                return (m[y * 8 + x] + 0.5) / 64.0;
            }

            float3 RGBtoHSV(float3 c)
            {
                float cmax = max(c.r, max(c.g, c.b));
                float cmin = min(c.r, min(c.g, c.b));
                float d = cmax - cmin;
                float h = 0.0;
                if (d > 1e-5)
                {
                    if      (cmax == c.r) h = fmod((c.g - c.b) / d, 6.0);
                    else if (cmax == c.g) h = (c.b - c.r) / d + 2.0;
                    else                  h = (c.r - c.g) / d + 4.0;
                    h /= 6.0;
                    if (h < 0.0) h += 1.0;
                }
                float s = (cmax <= 1e-5) ? 0.0 : d / cmax;
                return float3(h, s, cmax);
            }

            float3 HSVtoRGB(float3 hsv)
            {
                float h = hsv.x * 6.0;
                float s = hsv.y;
                float v = hsv.z;
                int i = (int)floor(h);
                float f = h - (float)i;
                float p = v * (1.0 - s);
                float q = v * (1.0 - s * f);
                float t = v * (1.0 - s * (1.0 - f));
                if (i == 0) return float3(v, t, p);
                if (i == 1) return float3(q, v, p);
                if (i == 2) return float3(p, v, t);
                if (i == 3) return float3(p, q, v);
                if (i == 4) return float3(t, p, v);
                return float3(v, p, q);
            }

            float3 ContrastSaturation(float3 c, float contrast, float saturation)
            {
                c = (c - 0.5) * contrast + 0.5;
                float3 hsv = RGBtoHSV(c);
                hsv.y *= saturation;
                return saturate(HSVtoRGB(hsv));
            }

            // ---- main pass -------------------------------------------------

            // Roberts-cross edge detection on scene depth + normals -> outline factor 0..1.
            float DetectEdge(float2 uv, float2 texel)
            {
                float2 o = texel * _EdgeThickness;
                float2 a = uv + float2(-o.x, -o.y);   // four diagonal taps
                float2 b = uv + float2( o.x,  o.y);
                float2 c = uv + float2(-o.x,  o.y);
                float2 d = uv + float2( o.x, -o.y);

                // --- Depth edges: first-difference gradient (solid, continuous lines) with a
                // GRAZING-ANGLE-AWARE threshold (Roystan-style, the production standard). A flat
                // surface seen edge-on has large per-pixel depth changes that would false-fire, so
                // we raise the threshold as the surface turns away from view -> slanted flats (felt,
                // floor) stay silent while true depth JUMPS still draw solid & continuous.
                float rawC = SampleSceneDepth(uv);
                float dC = LinearEyeDepth(rawC, _ZBufferParams);
                float da = LinearEyeDepth(SampleSceneDepth(a), _ZBufferParams);
                float db = LinearEyeDepth(SampleSceneDepth(b), _ZBufferParams);
                float dc = LinearEyeDepth(SampleSceneDepth(c), _ZBufferParams);
                float dd = LinearEyeDepth(SampleSceneDepth(d), _ZBufferParams);
                float depthDiff = abs(da - db) + abs(dc - dd);

                // Reconstruct the view angle to scale the threshold with grazing-ness.
                float3 nCtr   = SampleSceneNormals(uv);
                float3 posWS  = ComputeWorldSpacePosition(uv, rawC, UNITY_MATRIX_I_VP);
                float3 viewWS = normalize(_WorldSpaceCameraPos - posWS);
                float  NdotV  = 1.0 - saturate(dot(nCtr, viewWS));   // 0 head-on -> 1 grazing
                float  graze  = saturate((NdotV - _DepthNormalThreshold) / max(1.0 - _DepthNormalThreshold, 1e-3));
                float  depthThresh = _DepthEdgeThreshold * dC * (graze * _DepthNormalThresholdScale + 1.0);
                float  depthEdge   = step(depthThresh, depthDiff);

                // --- Normal edges = creases: first-difference (solid crease lines). A real bevel
                // (panel frame) gives a strong coherent normal change; fine normal-map detail (felt
                // weave) gives a weak one -> separate them with _NormalEdgeThreshold. If the felt
                // still reads through, lower the felt material's normal strength (the true source).
                float3 na = SampleSceneNormals(a);
                float3 nb = SampleSceneNormals(b);
                float3 nc = SampleSceneNormals(c);
                float3 nd = SampleSceneNormals(d);
                float normalDiff = dot(abs(na - nb), float3(1, 1, 1)) + dot(abs(nc - nd), float3(1, 1, 1));
                float normalEdge = step(_NormalEdgeThreshold, normalDiff);

                // Fade outlines out with distance so far, detail-dense geometry (tiled floor,
                // grime normals) doesn't dissolve into speckle. The depth term self-fades via its
                // dC-scaled threshold, but the normal term doesn't — this handles it.
                float distFade = 1.0 - saturate((dC - _EdgeFadeStart) / max(_EdgeFadeEnd - _EdgeFadeStart, 1e-3));
                return max(depthEdge, normalEdge) * distFade;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                // Godot's SCREEN_PIXEL_SIZE == the texel size of the texture being sampled.
                // Using _ScreenParams here lets the pixelate/dither grid beat against the
                // real source resolution -> regularly-spaced vertical/horizontal bars.
                float2 px = _BlitTexture_TexelSize.xy;            // (1/width, 1/height) of source
                if (px.x <= 0.0) px = 1.0 / _ScreenParams.xy;     // fallback if not bound

                // Virtual pixel size: a FIXED grid independent of the player's monitor resolution,
                // so the pixelate / dither / grain / outline scale look identical on 1080p, 1440p,
                // 4K, ultrawide, etc. Everything resolution-sensitive is keyed off vpx, not px.
                float  vRows  = max(_PixelHeight, 1.0);
                float  aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 vpx    = float2(1.0 / (vRows * aspect), 1.0 / vRows);

                // Subtle ink outlines: detect at native uv with virtual-pixel tap spacing (consistent
                // thickness on any monitor). No pixelation now, so uv stays the plain screen uv.
                float edge = (_EnableEdges > 0.5) ? DetectEdge(uv, vpx) : 0.0;

                // Sample the frame -- with an optional WAKE BLUR (script-driven, eases to 0 as you come to).
                // Branched, so it costs nothing when _WakeBlur is 0 (the normal case).
                float3 col;
                if (_WakeBlur > 0.01)
                {
                    float2 r = vpx * _WakeBlur;
                    float3 s = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0.0).rgb * 2.0;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( r.x, 0), 0.0).rgb;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-r.x, 0), 0.0).rgb;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( 0, r.y), 0.0).rgb;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( 0,-r.y), 0.0).rgb;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( r.x, r.y), 0.0).rgb;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-r.x,-r.y), 0.0).rgb;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( r.x,-r.y), 0.0).rgb;
                    s += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-r.x, r.y), 0.0).rgb;
                    col = s / 10.0;
                }
                else if (_ChromaticAberration > 0.01)
                {
                    // Radial R/B split: offset direction is (uv-0.5), so magnitude is 0 at centre and
                    // ~1 at the frame edge -> fringing only where a real lens fringes. Scaled to virtual
                    // px so `Amount` reads as edge-px on any monitor. Green stays put (the anchor channel).
                    float2 caOff = (uv - 0.5) * 2.0 * vpx * _ChromaticAberration;
                    col.r = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + caOff, 0.0).r;
                    col.g = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv,         0.0).g;
                    col.b = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv - caOff, 0.0).b;
                }
                else col = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0.0).rgb;

                // 3) EXPOSURE + SPLIT-TONE + SHADOW LIFT
                col *= _Exposure;

                // Split-tone: cool shadows, warm highlights by luminance (replaces the
                // old flat green tint). The warmth lives in the lit pools; shadows go cool.
                float lumST = dot(col, float3(0.2126, 0.7152, 0.0722));
                float3 split = lerp(_ShadowTint.rgb, _HighlightTint.rgb, saturate(lumST));
                col = lerp(col, col * split, _SplitToneStrength);

                // Shadow lift: gamma raises darks/mids, white stays at 1 -- keeps the dark
                // corners readable instead of crushed (the "too harsh" fix).
                col = pow(max(col, 0.0), 1.0 / _ShadowLift);

                // 4) POSTERIZE + ORDERED DITHER — the signature step.
                if (_EnablePosterize > 0.5)
                {
                    // ---- REWRITTEN 2026-07-18 ------------------------------------------------
                    // Standard ordered-dither quantization, in this order:
                    //   1. take luminance (per-channel quantizing gives coloured rings)
                    //   2. move to a perceptual curve (linear steps band hard in shadow)
                    //   3. add FULL-AMPLITUDE ordered dither, then floor
                    //   4. return to linear, rescale colour with a CLAMPED divisor
                    // The two things that were wrong before: a 4x4 matrix (checkerboard seams) and
                    // an unclamped `lq/l` divide (near-black amplification).
                    // -------------------------------------------------------------------------
                    // Bayer grid keyed to REAL pixels at an INTEGER scale (not the virtual row grid the
                    // outline taps use). Two reasons, and they're the whole fix:
                    //   BEATING -- a virtual grid is rarely an integer multiple of the real pixel grid,
                    //     so tiles land on a different sub-pixel phase each row and interfere with the
                    //     display grid into regular bars. Integer real-pixel cells are always phase-
                    //     aligned, so the tile can never beat against anything.
                    //   AMPLITUDE -- dither is only invisible while a cell is at/near one pixel, so the
                    //     eye integrates the 8x8 pattern into an average instead of resolving it. Cells
                    //     several pixels wide read as a literal grid at full strength. Cell size and
                    //     _DitherStrength trade off directly: big cells force low amplitude, and low
                    //     amplitude can't break a band. Small cells are what make _DitherStrength = 1
                    //     (one full quantization step, the amount that actually dissolves a boundary)
                    //     usable at all.
                    // Constant PHYSICAL grain size across monitors comes from DungeonPostGrade scaling
                    // this with Screen.height, NOT from a virtual grid -- integer steps only, so 1440p
                    // rounds to 1 and runs slightly finer than 1080p. That drift is invisible; the
                    // moire it avoids is not.
                    float  dScale = max(floor(_DitherPixelScale), 1.0);
                    float2 dpx    = px * dScale;
                    float  b      = Bayer8x8(floor(input.texcoord / dpx));

                    // Quantize BRIGHTNESS only, not each RGB channel. Per-channel quantizing
                    // makes R/G/B cross their step boundaries at different points -> coloured
                    // rings. Stepping luminance and rescaling the colour keeps hue intact, so
                    // the bands are neutral grey and need far less dither to hide.
                    float l  = dot(col, float3(0.2126, 0.7152, 0.0722));

                    // Quantize on a PERCEPTUAL curve, not linear luminance, so the level spacing at
                    // least roughly follows how brightness is actually perceived.
                    // What this canNOT do is fix the darkest band. Levels land at (k/(N-1))^gamma, so
                    // the ratio between the first two levels above black is 2^gamma -- INDEPENDENT of
                    // level count, and it GROWS with gamma (2.2 -> 4.6x, 3.0 -> 8x). Vision responds to
                    // ratios, so that first step is a ~400% brightness jump while the top-end step is
                    // ~12%. Raising gamma or _ColorLevels does not touch it. Only dither (spatial) and
                    // lifting darks off levels 1-2 do.
                    float lp  = pow(max(l, 0.0), 1.0 / _QuantizeGamma);

                    // Ordered dither. (b - 0.5) spans +-0.5, so _DitherStrength scales the offset as a
                    // fraction of ONE quantization step -- which means it sets what PROPORTION of pixels
                    // are randomised at all: at 1.0 every pixel's band assignment is probabilistic
                    // (perfect reconstruction, noise everywhere); at 0.1 only pixels within 5% of a
                    // boundary move, leaving flat regions untouched. Banding vs noise, not right vs
                    // wrong -- pick by how much of the frame actually bands. This scene is mostly smooth
                    // dark fog, so low amplitude wins.
                    // If the PATTERN itself becomes visible, that's cell size, not amplitude -- shrink
                    // _DitherPixelScale rather than dropping strength.
                    float steps  = max(_ColorLevels - 1.0, 1.0);
                    float scaled = lp * steps + (b - 0.5) * _DitherStrength;

                    // GRADIENT-ADAPTIVE SOFTNESS -- hard bands where they read as style, soft
                    // transitions only where they'd read as artifact.
                    //
                    // The problem being solved: the volumetric fog renders at ~1/3.6 resolution (see
                    // the Frame Debugger -- _VolumetricFog_675x257) and is bilinearly upsampled, so its
                    // iso-contours are piecewise-linear. A hard band boundary has INFINITE gain, which
                    // turns those linear kinks into visible faceted teeth along every contour.
                    //
                    // Widening the boundary in VALUE space blurs a displaced contour into something the
                    // eye can't trace -- but applied globally it costs half of every band (at softness
                    // 0.5 the smoothstep spans f = 0.25..0.75), which kills the hard-band look.
                    //
                    // The two needs are separable because they live in different parts of the image:
                    //   FLAT regions (low fwidth) = smooth fog. Faceting shows here, and a hard
                    //     boundary describes no detail because there is none -> soften freely.
                    //   GRADIENT regions (high fwidth) = lit surfaces, geometry. The posterised look
                    //     earns its keep here, and the fog artifact isn't visible -> stay hard.
                    //
                    // NOTE this is NOT analytic anti-aliasing (smoothstep +-fwidth). AA fixes edge
                    // WIDTH; faceting is an edge PATH problem -- the contour zigzags, and making that
                    // zigzag smoothly-edged leaves it just as traceable. Only value-space widening
                    // hides it. Hence softening by gradient rather than scaling width by gradient.
                    float lower = floor(scaled);
                    float f     = scaled - lower;

                    float grad    = fwidth(scaled);   // how fast the band coordinate moves per pixel
                    float flatness = saturate(1.0 - grad / max(_SoftnessGradientRef, 1e-4));
                    float w       = max(_PosterizeSoftness, 0.0) * flatness * 0.5;

                    float edgeT = (w > 1e-4) ? smoothstep(0.5 - w, 0.5 + w, f) : step(0.5, f);
                    float lpq   = (lower + edgeT) / steps;

                    float lq    = pow(max(lpq, 0.0), _QuantizeGamma);   // back to linear luminance

                    // FADE POSTERIZE OUT TOWARD BLACK -- this is what removes "the last band".
                    //
                    // The darkest boundary is unfixable by tuning: levels land at (k/(N-1))^gamma, so
                    // the level-1-to-level-2 ratio is 2^gamma (~4.6x at 2.2) no matter how many levels
                    // exist or what gamma is set to. The only way to not see it is to not CREATE it --
                    // below _PosterizeFloor we simply don't quantize, and the original smooth gradient
                    // passes through. No boundary, no band.
                    //
                    // It also removes the boundary's INFINITE GAIN in the darks, which is what was
                    // turning faint piecewise-linear structure in the source (a bilinearly upsampled
                    // half-res buffer) into visible faceted teeth along the contours. No boundary =
                    // no amplifier.
                    //
                    // Costs nothing visually: the region given up is dark fog with no surface detail,
                    // i.e. exactly where quantisation only ever produced artifacts and never style.
                    //
                    // NB this is what _DitherDarkFade (removed) was reaching for, one stage too early.
                    // Fading the DITHER out in darks made bands worse, because dither is what breaks
                    // them. Fading the POSTERIZE out removes the band itself. Right instinct, and the
                    // stage it belongs at.
                    float pFade  = saturate((lp - _PosterizeFloor) / max(_PosterizeFadeWidth, 1e-4));
                    float lFinal = lerp(l, lq, pFade);   // pFade = 0 -> l, so the rescale below is a no-op

                    // Rescale colour to the (possibly quantized) luminance. The CLAMPED divisor is the
                    // fix for near-black amplification: unclamped, lq/l explodes as l -> 0 and one
                    // quantize step becomes a huge multiplier (the old dark speckle).
                    col *= lFinal / max(l, _LumFloor);
                }

                // 4b) EDGE OUTLINES — darken silhouette/crease pixels into dark lines. Fade them where the
                // image is already BRIGHT so outlines of geometry BEHIND a transparent volumetric glow (which
                // doesn't write depth/normals, so the edge pass still "sees" it) don't paint over the light.
                float edgeLum  = dot(col, float3(0.2126, 0.7152, 0.0722));
                float bright   = smoothstep(_EdgeFadeLumLow, _EdgeFadeLumHigh, edgeLum);   // ramp tunable — pull down to cover a dim cone
                float edgeFade = 1.0 - _EdgeBrightFade * bright;
                col *= (1.0 - edge * _EdgeStrength * edgeFade);

                // Contrast + saturation grade.
                col = ContrastSaturation(col, _Contrast, _Saturation);

                // Colour filter -- flat tint multiply (white = no-op). Driven for the exertion wash.
                col *= _ColorFilter.rgb;

                // VIGNETTE -- runtime-driven by ScreenFeedback (wake-up eyelid, low-stamina edge, hurt
                // pulse). At 0 the branch is skipped. As _Vignette rises the clear radius shrinks from
                // past-the-corner to inside centre, so the darkness closes IN rather than just deepening
                // -- which is what reads as eyelids rather than a static frame. aspect-corrected so it's
                // circular, not stretched. `aspect` is already computed earlier in the frag.
                if (_Vignette > 1e-4)
                {
                    float2 vd = (input.texcoord - 0.5) * float2(aspect, 1.0);
                    // Shrink the horizontal contribution so the clear region is WIDER than tall: the
                    // darkness then closes from top and bottom into a horizontal slit (eyelids) rather
                    // than a circle inward. Aspect 1 = circular.
                    vd.x /= max(_VignetteAspect, 0.01);
                    float vdist  = length(vd);
                    float clearR = lerp(1.2, -0.2, _Vignette);
                    float vig    = smoothstep(clearR, clearR + _VignetteSoftness, vdist);
                    col = lerp(col, _VignetteColor.rgb, vig);
                }

                return half4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }
}
