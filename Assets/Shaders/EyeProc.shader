Shader "Unlit/EyeProc"
{
    // Procedural stylized eye drawn ON THE EYEBALL SPHERE (no disc, no UV needed). It uses the fragment's
    // OBJECT-SPACE direction relative to a forward axis (_Forward) — so the iris/pupil sit centred on the
    // eye's front and curve over the sphere, and they rotate WITH the eyeball for gaze. PUPIL SIZE is a live
    // float (_PupilSize) for dilation (fear = wide, light = constricted). Unlit — fits the retro grade.
    //
    // SET _Forward to the eyeball's local "looking" axis. If the iris ends up on the side/back, that axis is
    // wrong — try (0,0,1), (0,1,0), (0,0,-1)… (same axis quirk as the gaze rotation).
    Properties
    {
        _ScleraColor  ("Sclera",  Color) = (0.86, 0.84, 0.80, 1)   // dull/warm, not clean white (tired)
        _IrisColor    ("Iris",    Color) = (0.22, 0.16, 0.12, 1)
        _PupilColor   ("Pupil",   Color) = (0.02, 0.02, 0.02, 1)
        _Forward      ("Forward Axis (local)", Vector) = (0, 0, 1, 0)
        _IrisRadius   ("Iris Radius",  Range(0.05, 0.9)) = 0.42     // in sin(angle-from-front): 0=point, 1=equator
        _PupilSize    ("Pupil Size",   Range(0.0, 0.85)) = 0.18     // drive this for dilation
        _EdgeSoftness ("Edge Softness",Range(0.0, 0.1)) = 0.02
        _Highlight    ("Highlight Strength", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #pragma vertex Vert
            #pragma fragment Frag

            float4 _ScleraColor, _IrisColor, _PupilColor, _Forward;
            float  _IrisRadius, _PupilSize, _EdgeSoftness, _Highlight;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionHCS : SV_POSITION; float3 dirOS : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                o.dirOS = normalize(IN.positionOS.xyz);   // direction from the sphere centre (object space)
                return o;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                float3 dir = normalize(IN.dirOS);
                float3 fwd = normalize(_Forward.xyz);

                float align = dot(dir, fwd);                       // 1 = front pole, 0 = side, <0 = back
                // Perpendicular distance from the front axis: 0 at the front pole, 1 at the equator.
                // Back hemisphere forced far so it stays sclera.
                float d = (align > 0.0) ? sqrt(saturate(1.0 - align * align)) : 1.0;

                float s = max(_EdgeSoftness, 1e-4);
                float irisMask  = smoothstep(_IrisRadius + s, _IrisRadius - s, d);
                float pupilMask = smoothstep(_PupilSize   + s, _PupilSize   - s, d);

                float3 col = _ScleraColor.rgb;
                col = lerp(col, _IrisColor.rgb,  irisMask);
                col = lerp(col, _PupilColor.rgb, pupilMask);

                // Small offset highlight for a spark of life. Build a tangent frame around _Forward and place
                // the spot up-and-left of the pupil.
                float3 t = normalize(cross(fwd, float3(0, 1, 0)) + 1e-4);
                float3 u = cross(t, fwd);
                float3 perp = dir - fwd * align;
                float2 p2 = float2(dot(perp, t), dot(perp, u));
                float h = smoothstep(0.06, 0.0, distance(p2, float2(-0.08, 0.09))) * _Highlight * step(0.0, align);
                col += h;

                return half4(col, 1);
            }
            ENDHLSL
        }
    }
}
