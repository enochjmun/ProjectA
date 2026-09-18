// Dead CRT screen for the security-room menu's monitor wall.
// A mostly-DARK screen with a bright horizontal STATIC BAND that scrolls vertically, plus scanlines and a
// subtle flicker. Self-lit (unlit) so it reads in a pitch-black room. Each screen desyncs automatically
// from its world position, so a wall of them doesn't roll in unison. No script, no texture needed --
// assign a material using this shader to the screen's material slot and remove MenuStaticScreen from it.
//
// Tune per bank: _Tint = phosphor colour (green for the monochrome bank), _BandSpeed/_BandHeight for the
// roll, _Brightness for how hot the static is, _BaseGlow for how "off" the dark areas look.
Shader "CasinoHorror/DeadCRTScreen"
{
    Properties
    {
        _Tint ("Phosphor Tint", Color) = (0.55, 0.85, 0.65, 1)
        _BaseGlow ("Base Glow (dark areas)", Range(0,0.25)) = 0.03
        _Brightness ("Static Brightness", Range(0,4)) = 1.4
        _BandHeight ("Band Height", Range(0.01,0.6)) = 0.14
        _BandSpeed ("Band Speed", Range(-2,2)) = 0.18
        [Toggle] _SwapAxis ("Swap scroll axis (if it rolls sideways)", Float) = 0
        _Scanline ("Scanline Strength", Range(0,1)) = 0.35
        _ScanlineCount ("Scanline Count", Float) = 220
        _StaticScale ("Static Cell Scale", Float) = 80
        _StaticSpeed ("Static Speed", Float) = 14
        _Flicker ("Flicker Amount", Range(0,1)) = 0.12
        _GlowJitter ("Per-screen Variation", Range(0,1)) = 0.4
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; float3 seed : TEXCOORD1; };

            CBUFFER_START(UnityPerMaterial)
                float4 _Tint;
                float _BaseGlow, _Brightness, _BandHeight, _BandSpeed;
                float _Scanline, _ScanlineCount, _StaticScale, _StaticSpeed, _Flicker, _SwapAxis, _GlowJitter;
            CBUFFER_END

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                // per-object seed from world position -> each screen desyncs
                OUT.seed = float3(unity_ObjectToWorld._m03, unity_ObjectToWorld._m13, unity_ObjectToWorld._m23);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float seed = frac(dot(IN.seed, float3(0.13, 0.71, 0.29))) * 10.0;
                float t = _Time.y + seed * 7.0;

                // pick the screen's vertical axis (swap if the mesh UVs are rotated 90 deg)
                float v = (_SwapAxis > 0.5) ? IN.uv.x : IN.uv.y;

                // scrolling bright band (wraps)
                float bandPos = frac(t * _BandSpeed + seed);
                float d = abs(v - bandPos);
                d = min(d, 1.0 - d);
                float band = smoothstep(_BandHeight, 0.0, d);

                // flickering static cells
                float2 cell = floor(IN.uv * _StaticScale);
                float n = hash21(cell + floor(t * _StaticSpeed));

                // scanlines (run across the same axis as the band)
                float scan = lerp(1.0, sin(v * _ScanlineCount * 3.14159) * 0.5 + 0.5, _Scanline);

                // global flicker
                float fl = 1.0 - _Flicker * hash21(float2(floor(t * 20.0), seed));

                // per-screen variation (free): offset base glow + static brightness from the object seed,
                // so the wall reads as a range of dead/active screens on ONE shared material.
                float r1 = frac(seed * 0.617 + 0.13);
                float r2 = frac(seed * 0.911 + 0.57);
                float glowVary   = max(0.0, 1.0 + _GlowJitter * (r1 * 2.0 - 1.0));
                float brightVary = max(0.0, 1.0 + _GlowJitter * (r2 * 2.0 - 1.0));

                float lum = (_BaseGlow * glowVary + band * n * _Brightness * brightVary) * scan * fl;
                return half4(_Tint.rgb * lum, 1.0);
            }
            ENDHLSL
        }

        // Writes depth (and normals) in the prepass so BuckshotPost's edge-outline treats the screen as a
        // solid occluder -- otherwise it outlines the monitor's own interior/back geometry THROUGH the glass.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Off
            HLSLPROGRAM
            #pragma vertex vertD
            #pragma fragment fragD
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct AttrD { float4 positionOS : POSITION; };
            struct VarD  { float4 positionHCS : SV_POSITION; };
            VarD vertD (AttrD IN) { VarD o; o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz); return o; }
            half4 fragD (VarD IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            Cull Off
            HLSLPROGRAM
            #pragma vertex vertDN
            #pragma fragment fragDN
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct AttrDN { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct VarDN  { float4 positionHCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            VarDN vertDN (AttrDN IN)
            {
                VarDN o;
                o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return o;
            }
            half4 fragDN (VarDN IN) : SV_Target { return half4(normalize(IN.normalWS) * 0.5 + 0.5, 1.0); }
            ENDHLSL
        }

        // Casts shadows so the screen OCCLUDES light -- without this the glass is invisible to shadows and
        // light spills straight through it, even with the light's shadows enabled.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex vertSC
            #pragma fragment fragSC
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct AttrSC { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct VarSC  { float4 positionCS : SV_POSITION; };

            VarSC vertSC (AttrSC IN)
            {
                VarSC o;
                float3 posWS  = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normWS = TransformObjectToWorldNormal(IN.normalOS);
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(posWS, normWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                o.positionCS = positionCS;
                return o;
            }

            half4 fragSC (VarSC IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
