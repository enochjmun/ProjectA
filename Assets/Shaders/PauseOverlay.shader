// Full-screen PAUSE / escape-menu overlay for URP's Full Screen Pass Renderer Feature. Samples the camera
// colour and, scaled by _Amount (0 = off, 1 = full), blurs it, lays House-OS scanlines + a warm phosphor
// tint + vignette over it, and rolls a bright scan band down once on open (_ScanRoll 0→1) — the house's
// screen powering on over your view. Add as a Full Screen Pass Renderer Feature AFTER BuckshotPost, drive
// _Amount / _ScanRoll from PauseMenu.cs.
Shader "CasinoHorror/PauseOverlay"
{
    Properties
    {
        _Amount ("Amount", Range(0,1)) = 0
        _BlurSize ("Blur Size (texels)", Range(0,6)) = 2.5
        _ScanIntensity ("Scanline Intensity", Range(0,1)) = 0.35
        _ScanCount ("Scanline Count", Float) = 700
        _Darken ("Darken", Range(0,1)) = 0.45
        _Vignette ("Vignette", Range(0,3)) = 1.2
        [HDR] _Tint ("Phosphor Tint", Color) = (0.94, 0.66, 0.24, 1)
        _TintAmount ("Tint Amount", Range(0,1)) = 0.14
        _ScanRoll ("Scan Roll (transition sweep)", Range(0,1)) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off
        Pass
        {
            Name "PauseOverlay"
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Blit.hlsl"
            #pragma vertex Vert
            #pragma fragment Frag

            float _Amount, _BlurSize, _ScanIntensity, _ScanCount, _Darken, _Vignette, _TintAmount, _ScanRoll;
            float4 _Tint;

            half3 Tap(float2 uv) { return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb; }

            half3 Blur5(float2 uv, float2 o)
            {
                half3 c = Tap(uv) * 0.4;
                c += Tap(uv + float2(o.x, 0)) * 0.15;
                c += Tap(uv - float2(o.x, 0)) * 0.15;
                c += Tap(uv + float2(0, o.y)) * 0.15;
                c += Tap(uv - float2(0, o.y)) * 0.15;
                return c;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 texel = 1.0 / _ScreenParams.xy;

                half3 orig = Tap(uv);
                if (_Amount <= 0.001) return half4(orig, 1.0);   // fully off — pass through

                half3 col = lerp(orig, Blur5(uv, texel * _BlurSize), _Amount);

                // scanlines
                float scan = sin(uv.y * _ScanCount * 3.14159) * 0.5 + 0.5;
                col *= lerp(1.0, 1.0 - _ScanIntensity * scan, _Amount);

                // darken + phosphor tint
                col *= lerp(1.0, 1.0 - _Darken, _Amount);
                col = lerp(col, col * _Tint.rgb, _TintAmount * _Amount);

                // vignette
                float2 d = uv - 0.5;
                col *= saturate(1.0 - dot(d, d) * _Vignette * _Amount);

                // one-shot bright scan band rolling top→bottom as _ScanRoll goes 0→1 (the power-on sweep)
                float band = smoothstep(0.015, 0.0, abs(uv.y - _ScanRoll));
                col += band * _Amount * 0.2 * _Tint.rgb;

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
