Shader "Hidden/InkOutline"
{
    // Depth+normal ink outlines as a STANDALONE full-screen pass, extracted from BuckshotPost so it can run
    // BEFORE the volumetric fog. Drawing outlines into the color buffer ahead of the fog means the fog's own
    // compositing handles occlusion for free: thin fog shows edges through it, dense fog washes them out — no
    // brightness thresholds, because it uses the fog's real density via honest compositing. BuckshotPost keeps
    // posterize/CA/vignette AFTER the fog (so the fog still gets posterized); it just no longer draws edges.
    //
    // SETUP: add a "Full Screen Pass Renderer Feature" with this material, Requirements = Depth + Normal +
    // Color, injected BEFORE the volumetric fog's render pass event (see the fog Volume's Render Pass Event).
    // Turn OFF _EnableEdges on the BuckshotPost material so outlines aren't drawn twice.
    Properties
    {
        [Header(Edge Outlines)]
        _EdgeStrength        ("Strength",        Range(0.0, 1.0))  = 0.5
        _EdgeThickness       ("Thickness (px)",  Range(0.5, 4.0))  = 1.0
        _PixelHeight         ("Pixel Height",    Float)            = 240.0
        _DepthEdgeThreshold  ("Depth Threshold", Range(0.005, 1.0)) = 0.05
        _NormalEdgeThreshold ("Normal Threshold",Range(0.1, 2.0))   = 0.6
        _DepthNormalThreshold      ("Depth Normal Threshold",       Range(0.0, 1.0))  = 0.5
        _DepthNormalThresholdScale ("Depth Normal Threshold Scale", Range(1.0, 20.0)) = 7.0
        _EdgeFadeStart       ("Fade Start (m)",  Float)            = 15.0
        _EdgeFadeEnd         ("Fade End (m)",    Float)            = 40.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "InkOutline"

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            #pragma vertex Vert
            #pragma fragment Frag

            float _EdgeStrength;
            float _EdgeThickness;
            float _PixelHeight;
            float _DepthEdgeThreshold;
            float _NormalEdgeThreshold;
            float _DepthNormalThreshold;
            float _DepthNormalThresholdScale;
            float _EdgeFadeStart;
            float _EdgeFadeEnd;

            // Roberts-cross edge detection on scene depth + normals -> outline factor 0..1.
            // (Identical to BuckshotPost's DetectEdge — kept in sync if you tune one, tune both.)
            float DetectEdge(float2 uv, float2 texel)
            {
                float2 o = texel * _EdgeThickness;
                float2 a = uv + float2(-o.x, -o.y);
                float2 b = uv + float2( o.x,  o.y);
                float2 c = uv + float2(-o.x,  o.y);
                float2 d = uv + float2( o.x, -o.y);

                float rawC = SampleSceneDepth(uv);
                float dC = LinearEyeDepth(rawC, _ZBufferParams);
                float da = LinearEyeDepth(SampleSceneDepth(a), _ZBufferParams);
                float db = LinearEyeDepth(SampleSceneDepth(b), _ZBufferParams);
                float dc = LinearEyeDepth(SampleSceneDepth(c), _ZBufferParams);
                float dd = LinearEyeDepth(SampleSceneDepth(d), _ZBufferParams);
                float depthDiff = abs(da - db) + abs(dc - dd);

                float3 nCtr   = SampleSceneNormals(uv);
                float3 posWS  = ComputeWorldSpacePosition(uv, rawC, UNITY_MATRIX_I_VP);
                float3 viewWS = normalize(_WorldSpaceCameraPos - posWS);
                float  NdotV  = 1.0 - saturate(dot(nCtr, viewWS));
                float  graze  = saturate((NdotV - _DepthNormalThreshold) / max(1.0 - _DepthNormalThreshold, 1e-3));
                float  depthThresh = _DepthEdgeThreshold * dC * (graze * _DepthNormalThresholdScale + 1.0);
                float  depthEdge   = step(depthThresh, depthDiff);

                float3 na = SampleSceneNormals(a);
                float3 nb = SampleSceneNormals(b);
                float3 nc = SampleSceneNormals(c);
                float3 nd = SampleSceneNormals(d);
                float normalDiff = dot(abs(na - nb), float3(1, 1, 1)) + dot(abs(nc - nd), float3(1, 1, 1));
                float normalEdge = step(_NormalEdgeThreshold, normalDiff);

                float distFade = 1.0 - saturate((dC - _EdgeFadeStart) / max(_EdgeFadeEnd - _EdgeFadeStart, 1e-3));
                return max(depthEdge, normalEdge) * distFade;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;

                // Virtual-pixel tap spacing so outline thickness is consistent on any monitor resolution
                // (same scheme as BuckshotPost).
                float  vRows  = max(_PixelHeight, 1.0);
                float  aspect = _ScreenParams.x / max(_ScreenParams.y, 1.0);
                float2 vpx    = float2(1.0 / (vRows * aspect), 1.0 / vRows);

                float edge = DetectEdge(uv, vpx);
                float3 col = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0.0).rgb;

                // Darken silhouette/crease pixels into ink lines. No brightness fade here on purpose —
                // the fog composites AFTER this pass and occludes the lines by its real density.
                col *= (1.0 - edge * _EdgeStrength);

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
}
