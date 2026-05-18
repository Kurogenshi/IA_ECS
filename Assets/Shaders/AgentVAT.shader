Shader "Crowd/AgentVAT"
{
    Properties
    {
        _BaseColor      ("Base Color", Color) = (1,1,1,1)
        _BaseMap        ("Base Map", 2D) = "white" {}
        _Smoothness     ("Smoothness", Range(0,1)) = 0.2
        _Metallic       ("Metallic", Range(0,1)) = 0.0

        [NoScaleOffset] _PositionMap ("VAT Position Map", 2D) = "black" {}
        _VertexCount    ("Vertex Count (debug)", Float) = 0
        _TotalFrames    ("Total Frames (debug)", Float) = 0
        _VATWidth       ("VAT Width",        Float) = 1
        _VATHeight      ("VAT Height",       Float) = 1
        _RowsPerFrame   ("Rows Per Frame",   Float) = 1

        // Per-VAT clip metadata (set by the baker / authoring):
        // x = clip0, y = clip1, z = clip2, w = clip3
        _ClipStartFrame ("Clip Start Frame", Vector) = (0,0,0,0)
        _ClipFrameCount ("Clip Frame Count", Vector) = (0,0,0,0)
        _ClipFps        ("Clip Fps",         Vector) = (30,30,30,30)

        // Per-instance defaults (overridden by Entities Graphics)
        [PerRendererData] _AnimClip           ("Anim Clip Index",       Float) = 0
        [PerRendererData] _AnimTime           ("Anim Time",             Float) = 0
        [PerRendererData] _AgentVisible       ("Agent Visible",         Float) = 1
        [PerRendererData] _AgentShadowVisible ("Agent Shadow Visible",  Float) = 1

        // Debug: 0 = normal, 1 = vertex ID as RGB, 2 = sampled VAT row as RGB, 3 = animTime as RGB
        [IntRange] _DebugMode ("Debug Mode (0=Off 1=VertexID 2=VATRow 3=AnimTime)", Range(0,3)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }
        LOD 200

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_BaseMap);            SAMPLER(sampler_BaseMap);
        TEXTURE2D(_PositionMap);        SAMPLER(sampler_PositionMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float  _Smoothness;
            float  _Metallic;
            float  _VertexCount;     // kept for debug display
            float  _TotalFrames;     // kept for debug display
            float  _VATWidth;
            float  _VATHeight;
            float  _RowsPerFrame;
            float4 _ClipStartFrame;
            float4 _ClipFrameCount;
            float4 _ClipFps;
            float  _AnimClip;
            float  _AnimTime;
            float  _AgentVisible;
            float  _AgentShadowVisible;
            float  _DebugMode;
        CBUFFER_END

        // ---- DOTS instancing: per-instance overrides ----
        #ifdef UNITY_DOTS_INSTANCING_ENABLED
        UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
            UNITY_DOTS_INSTANCED_PROP(float, _AnimClip)
            UNITY_DOTS_INSTANCED_PROP(float, _AnimTime)
            UNITY_DOTS_INSTANCED_PROP(float, _AgentVisible)
            UNITY_DOTS_INSTANCED_PROP(float, _AgentShadowVisible)
        UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

        #define _AnimClip           UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _AnimClip)
        #define _AnimTime           UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _AnimTime)
        #define _AgentVisible       UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _AgentVisible)
        #define _AgentShadowVisible UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _AgentShadowVisible)
        #endif

        // Output a clip-space position behind the near plane so the rasterizer culls the
        // entire triangle — cheap way to skip fragment work for off-distance agents.
        float4 CulledClipPos()
        {
            return float4(0, 0, -2, 1);
        }

        // Returns animated object-space position for this vertex.
        // 2D layout: vertices are wrapped across multiple rows per frame when the mesh
        // exceeds the chosen texture width. col = vId mod W, localRow = vId / W,
        // texRow = globalFrame * rowsPerFrame + localRow.
        //
        // CRITICAL: globalFrame MUST be floored to an integer before multiplying by
        // rowsPerFrame. A non-integer (eg. 3.7) would push samplings of vertices with
        // localRow>=1 into the NEXT frame's rows, mixing positions from two different
        // animation poses on the same character -> stretched / exploded triangles.
        float3 SampleVAT(float vertexId)
        {
            int ci = (int)clamp(_AnimClip, 0, 3);
            float startF = _ClipStartFrame[ci];
            float countF = max(1.0, _ClipFrameCount[ci]);
            float fps    = max(1.0, _ClipFps[ci]);

            float frameInClip = floor(fmod(_AnimTime * fps, countF));
            float globalFrame = startF + frameInClip;

            float w  = max(1.0, _VATWidth);
            float h  = max(1.0, _VATHeight);
            float rpf = max(1.0, _RowsPerFrame);

            float col      = fmod(vertexId, w);
            float localRow = floor(vertexId / w);
            float texRow   = globalFrame * rpf + localRow;

            float u = (col    + 0.5) / w;
            float v = (texRow + 0.5) / h;

            float4 s = SAMPLE_TEXTURE2D_LOD(_PositionMap, sampler_PositionMap, float2(u, v), 0);
            return s.xyz;
        }

        // Estimate normal by finite difference against the next frame: cheap-ish and
        // good enough for diffuse-lit crowds. For higher quality, bake a normal VAT.
        float3 EstimateNormal(float vertexId, float3 staticNormalOS, float3 animatedPosOS)
        {
            // For v1 we just rotate the static normal by an approximate yaw derived from
            // the local position drift. This is a fast hack: the mesh root keeps facing
            // forward, so static normals are usually acceptable for distant crowds.
            return normalize(staticNormalOS);
        }
        ENDHLSL

        // -------------------------------------------------------------------------
        // Forward Pass
        // -------------------------------------------------------------------------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   Vert
            #pragma fragment Frag

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma instancing_options renderinglayer

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
                uint   vertexId   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float2 uv          : TEXCOORD2;
                float  fogFactor   : TEXCOORD3;
                float2 debug       : TEXCOORD4; // x = vertexId, y = sampled v coord
                half4  vColor      : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                // Per-instance distance cull: degenerate clip-space discards the triangle.
                if (_AgentVisible < 0.5)
                {
                    OUT.positionCS = CulledClipPos();
                    return OUT;
                }

                float vId = (float)IN.vertexId;
                float3 animPosOS = SampleVAT(vId);
                float3 normalOS  = EstimateNormal(vId, IN.normalOS, animPosOS);

                VertexPositionInputs posIn = GetVertexPositionInputs(animPosOS);
                VertexNormalInputs   nrmIn = GetVertexNormalInputs(normalOS);

                OUT.positionCS = posIn.positionCS;
                OUT.positionWS = posIn.positionWS;
                OUT.normalWS   = nrmIn.normalWS;
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor  = ComputeFogFactor(posIn.positionCS.z);
                OUT.debug      = float2(vId, 0);
                OUT.vColor     = IN.color;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 baseTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half3 albedo  = baseTex.rgb * _BaseColor.rgb * IN.vColor.rgb;

                InputData inputData = (InputData)0;
                inputData.positionWS         = IN.positionWS;
                inputData.normalWS           = normalize(IN.normalWS);
                inputData.viewDirectionWS    = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                inputData.shadowCoord        = TransformWorldToShadowCoord(IN.positionWS);
                inputData.fogCoord           = IN.fogFactor;
                inputData.vertexLighting     = 0;
                inputData.bakedGI            = SampleSH(inputData.normalWS);
                inputData.normalizedScreenSpaceUV = 0;
                inputData.shadowMask         = half4(1,1,1,1);

                SurfaceData surface = (SurfaceData)0;
                surface.albedo               = albedo;
                surface.specular             = 0;
                surface.metallic             = _Metallic;
                surface.smoothness           = _Smoothness;
                surface.normalTS             = half3(0,0,1);
                surface.emission             = 0;
                surface.occlusion            = 1;
                surface.alpha                = 1;
                surface.clearCoatMask        = 0;
                surface.clearCoatSmoothness  = 0;

                half4 color = UniversalFragmentPBR(inputData, surface);
                color.rgb = MixFog(color.rgb, IN.fogFactor);

                if (_DebugMode > 0.5)
                {
                    if (_DebugMode < 1.5)
                    {
                        // 1: VertexID as smooth gradient
                        float t = saturate(IN.debug.x / max(1.0, _VertexCount));
                        color.rgb = half3(t, frac(t * 7.0), frac(t * 31.0));
                    }
                    else if (_DebugMode < 2.5)
                    {
                        // 2: World-space animated position normalized as RGB
                        color.rgb = saturate(IN.positionWS * 0.5 + 0.5);
                    }
                    else
                    {
                        // 3: _AnimTime as RGB. If frozen / black across agents -> DOTS
                        //    instancing is not pushing per-instance time. If colors vary
                        //    and cycle smoothly, animation pipeline is feeding the GPU.
                        float a = frac(_AnimTime * 0.5);
                        color.rgb = half3(a, frac(a * 3.0), frac(a * 11.0));
                    }
                }
                return color;
            }
            ENDHLSL
        }

        // -------------------------------------------------------------------------
        // ShadowCaster Pass
        // -------------------------------------------------------------------------
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   ShadowVert
            #pragma fragment ShadowFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                uint   vertexId   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float4 GetShadowClipPos(float3 positionWS, float3 normalWS)
            {
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));

                #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            ShadowVaryings ShadowVert(ShadowAttributes IN)
            {
                ShadowVaryings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                // Tighter shadow distance: stop casting shadows well before the agent
                // is invisible in the forward pass.
                if (_AgentShadowVisible < 0.5)
                {
                    OUT.positionCS = CulledClipPos();
                    return OUT;
                }

                float3 animPosOS = SampleVAT((float)IN.vertexId);
                VertexPositionInputs posIn = GetVertexPositionInputs(animPosOS);
                VertexNormalInputs   nrmIn = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS = GetShadowClipPos(posIn.positionWS, nrmIn.normalWS);
                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        // -------------------------------------------------------------------------
        // DepthOnly Pass
        // -------------------------------------------------------------------------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex   DepthVert
            #pragma fragment DepthFrag

            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                uint   vertexId   : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            DepthVaryings DepthVert(DepthAttributes IN)
            {
                DepthVaryings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                if (_AgentVisible < 0.5)
                {
                    OUT.positionCS = CulledClipPos();
                    return OUT;
                }

                float3 animPosOS = SampleVAT((float)IN.vertexId);
                OUT.positionCS = TransformObjectToHClip(animPosOS);
                return OUT;
            }

            half4 DepthFrag(DepthVaryings IN) : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack Off
}
