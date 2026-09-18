Shader "Trapdoor/StencilMask"
{
    // The invisible disc that STAMPS a value into the stencil buffer.
    // Writes no colour and no depth - it only marks "a hole belongs here".
    // Put this on the flat filled circle (the mask object).
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry-1"   // render BEFORE floor & shaft, so the mark exists first
        }

        Pass
        {
            Name "StencilMask"

            ColorMask 0     // draw no colour (invisible)
            ZWrite Off      // write no depth (occlude nothing)
            Cull Off        // stamp from either side of the disc

            Stencil
            {
                Ref 1        // the value we stamp
                Comp Always  // always stamp, no test
                Pass Replace // write Ref (1) into the stencil buffer
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionHCS : SV_POSITION; };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                return half4(0, 0, 0, 0); // never shown (ColorMask 0)
            }
            ENDHLSL
        }
    }
}
