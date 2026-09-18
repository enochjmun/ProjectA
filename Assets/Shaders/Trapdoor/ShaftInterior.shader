Shader "Trapdoor/ShaftInterior"
{
    // The dark shaft you see DOWN the hole. Plain opaque, double-sided, NO stencil -
    // so it renders from every angle, including from inside as the player falls.
    // The floor hides it from above (depth); it only shows through the hole.
    // Put this on the cylinder (the void shaft object).
    Properties
    {
        _BaseColor ("Colour", Color) = (0.02, 0.02, 0.02, 1)
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
            Name "ShaftInterior"

            Cull Off        // show the cylinder walls regardless of normal direction (safe default)

            // NO stencil test. The shaft is a plain opaque object.
            // From ABOVE, the solid floor sits in front of it and hides it everywhere
            // except the hole (where the floor erased itself and wrote no depth).
            // From BELOW, nothing gates it, so it stays visible as the player falls.
            // (An Equal stencil test here would make it a one-way window that vanishes
            //  the moment the camera drops below the mask plane.)

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
