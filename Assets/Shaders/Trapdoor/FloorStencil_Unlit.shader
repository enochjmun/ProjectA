Shader "Trapdoor/FloorStencil_Unlit"
{
    // TEST-ONLY flat floor that HIDES itself where the mask stamped.
    // Use this to prove the hole works before moving the stencil test onto
    // your real lit (ORM_Lit) floor material.
    Properties
    {
        _BaseColor ("Colour", Color) = (0.5, 0.5, 0.5, 1)
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
        }

        Pass
        {
            Name "FloorStencil"

            Stencil
            {
                Ref 1
                Comp NotEqual   // draw ONLY where the mask did NOT stamp -> the floor skips the hole
            }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

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
                return _BaseColor;
            }
            ENDHLSL
        }
    }
}
