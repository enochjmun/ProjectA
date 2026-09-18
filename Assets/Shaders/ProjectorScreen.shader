// Projector screen for the LOBBY main display -- light thrown onto fabric, NOT a CRT tube.
// Samples the House-OS RenderTexture (_MainTex) and lays projector optics over it: soft diffusion (never
// pixel-sharp), a central lamp HOTSPOT falling off to darker edges, LIFTED blacks (projected black on fabric
// is dark grey, never true black -- the big believability tell), a gentle warm lamp FLICKER, faint GRAIN, and
// a whisper of edge CONVERGENCE fringe. Unlit/self-lit so it reads in a dark room.
//
// SETUP: make a material with this shader, set _MainTex = your ScreenUICamera's RenderTexture, assign it to
// the panel's front-face slot (replacing the plain Unlit/Base Map material). Tune in the material inspector;
// no node editing. Keep _Brightness so the amber leader bar still pops against the lifted black.
Shader "CasinoHorror/ProjectorScreen"
{
    Properties
    {
        _MainTex ("Screen (RenderTexture)", 2D) = "black" {}
        _Brightness ("Brightness", Range(0,3)) = 1.15
        _LampTint ("Lamp Tint (warm-white)", Color) = (1.0, 0.98, 0.94, 1)

        [Header(Diffusion)]
        _BlurSize ("Soft Focus (texels)", Range(0,4)) = 1.0

        [Header(Fabric wash)]
        _BlackLift ("Black Lift (ambient wash)", Range(0,0.25)) = 0.05
        _AmbientTint ("Ambient Wash Colour", Color) = (0.09, 0.075, 0.05, 1)

        [Header(Lamp shape)]
        _HotspotStrength ("Hotspot Strength", Range(0,1)) = 0.22
        _HotspotSize ("Hotspot Size", Range(0.2,2)) = 0.85
        _Vignette ("Edge Vignette", Range(0,1.5)) = 0.5

        [Header(Lamp life)]
        _FlickerAmount ("Flicker Amount", Range(0,0.3)) = 0.035
        _FlickerSpeed ("Flicker Speed", Range(0,30)) = 7
        _Grain ("Grain", Range(0,0.3)) = 0.04

        [Header(Optics)]
        _Chroma ("Edge Convergence", Range(0,4)) = 0.6
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
            struct Varyings   { float4 positionHCS : SV_POSITION; float2 uv : TEXCOORD0; };

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float4 _LampTint, _AmbientTint;
                float _Brightness, _BlurSize, _BlackLift;
                float _HotspotStrength, _HotspotSize, _Vignette;
                float _FlickerAmount, _FlickerSpeed, _Grain, _Chroma;
            CBUFFER_END

            float hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 5-tap soft-focus sample: the projector is never pixel-sharp.
            half3 Blur(float2 uv, float2 o)
            {
                half3 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).rgb * 0.4;
                c += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(o.x, 0)).rgb * 0.15;
                c += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - float2(o.x, 0)).rgb * 0.15;
                c += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv + float2(0, o.y)).rgb * 0.15;
                c += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv - float2(0, o.y)).rgb * 0.15;
                return c;
            }

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float2 texel = _MainTex_TexelSize.xy;
                float2 o = texel * _BlurSize;

                // edge convergence: split R/B along the radial direction, scaled by distance from centre
                float2 fromC = IN.uv - 0.5;
                float rad = length(fromC);
                float2 dir = (rad > 1e-4) ? fromC / rad : float2(0, 0);
                float2 caOff = dir * _Chroma * texel * rad * 2.0;

                half3 cR = Blur(IN.uv + caOff, o);
                half3 cG = Blur(IN.uv, o);
                half3 cB = Blur(IN.uv - caOff, o);
                half3 col = half3(cR.r, cG.g, cB.b);

                // lifted blacks -- ambient light on fabric, projected black is never true black
                col += _AmbientTint.rgb * _BlackLift;

                // lamp tint + overall throw
                col *= _LampTint.rgb * _Brightness;

                // lamp shape: brighter centre hotspot, darker edges
                float hot = 1.0 + _HotspotStrength * (1.0 - smoothstep(0.0, _HotspotSize, rad));
                float vig = 1.0 - _Vignette * smoothstep(_HotspotSize * 0.6, 0.78, rad);
                col *= hot * saturate(vig);

                // gentle lamp flicker (smooth wobble + a little irregularity)
                float fl = 1.0 - _FlickerAmount *
                    (0.5 + 0.5 * sin(_Time.y * _FlickerSpeed)
                         + 0.3 * (hash21(float2(floor(_Time.y * 30.0), 1.0)) - 0.5));
                col *= fl;

                // faint grain
                float g = hash21(IN.uv * float2(837.0, 491.0) + frac(_Time.y));
                col += (g - 0.5) * _Grain;

                return half4(max(col, 0.0), 1.0);
            }
            ENDHLSL
        }

        // Depth / normals / shadow passes so BuckshotPost's edge-outline treats the panel as a solid occluder
        // (same reason as DeadCRTScreen) -- otherwise it outlines geometry behind the glass.
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
