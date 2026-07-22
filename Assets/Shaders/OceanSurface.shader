// Ocean — URP 17+ (Unity 6) sea surface shader.
// Consumes global texture arrays published by OceanSimulation.cs.
// Requires: Depth Texture + Opaque Texture enabled on the URP asset.

Shader "UnityLibrary/Water/Ocean"
{
    Properties
    {
        _DeepColor        ("Deep Water Color", Color) = (0.012, 0.05, 0.08, 1)
        _ScatterColor     ("Subsurface Scatter Color", Color) = (0.05, 0.42, 0.38, 1)
        _Density          ("Water Fog Density", Range(0.01, 2)) = 0.25
        _RefractionStrength("Refraction Strength", Range(0, 2)) = 0.6

        _Roughness        ("Roughness", Range(0.01, 0.5)) = 0.08
        _DistantRoughness ("Distant Roughness", Range(0.05, 0.8)) = 0.3
        _SpecularBoost    ("Sun Specular Boost", Range(0, 10)) = 1.5

        _SSSStrength      ("SSS Strength", Range(0, 4)) = 1.2
        _SSSHeight        ("SSS Crest Height Norm (m)", Range(0.1, 10)) = 2.5
        _SSSAmbient       ("SSS Ambient", Range(0, 1)) = 0.12

        _FoamColor        ("Foam Color", Color) = (0.92, 0.96, 0.98, 1)
        _FoamBias         ("Foam Threshold", Range(-1, 2)) = 0.84
        _FoamSharpness    ("Foam Sharpness", Range(0.5, 20)) = 6
        _FoamNoiseScale   ("Foam Noise Scale", Range(0.01, 2)) = 0.35

        _Cascade1Fade     ("Cascade 1 Fade Distance", Float) = 900
        _Cascade2Fade     ("Cascade 2 Fade Distance", Float) = 220
        _NormalFadeDistance("Normal Fade Distance", Float) = 2000
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "OceanForward"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            // ---- simulation globals (set by OceanSimulation.cs) ----
            TEXTURE2D_ARRAY(_Ocean_Displacement); SAMPLER(sampler_Ocean_Displacement);
            TEXTURE2D_ARRAY(_Ocean_Derivatives);  SAMPLER(sampler_Ocean_Derivatives);
            TEXTURE2D_ARRAY(_Ocean_Turbulence);   SAMPLER(sampler_Ocean_Turbulence);
            float4 _Ocean_LengthScales;

            CBUFFER_START(UnityPerMaterial)
                half4 _DeepColor, _ScatterColor, _FoamColor;
                half  _Density, _RefractionStrength;
                half  _Roughness, _DistantRoughness, _SpecularBoost;
                half  _SSSStrength, _SSSHeight, _SSSAmbient;
                half  _FoamBias, _FoamSharpness, _FoamNoiseScale;
                float _Cascade1Fade, _Cascade2Fade, _NormalFadeDistance;
            CBUFFER_END

            struct Attributes { float3 positionOS : POSITION; };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 positionWSFog : TEXCOORD0; // xyz world pos, w fog factor
                float4 uv01 : TEXCOORD1;          // cascade 0 + 1 uvs
                float4 uv2DistHeight : TEXCOORD2; // cascade 2 uv, view dist, wave height
            };

            float3 CascadeWeights(float dist)
            {
                float w1 = 1.0 - saturate((dist - 0.5 * _Cascade1Fade) / (0.5 * _Cascade1Fade));
                float w2 = 1.0 - saturate((dist - 0.5 * _Cascade2Fade) / (0.5 * _Cascade2Fade));
                return float3(1.0, w1, w2);
            }

            // ----------------------------------------------------- vertex
            Varyings Vert(Attributes input)
            {
                Varyings o;
                float3 posWS = TransformObjectToWorld(input.positionOS);

                float2 uv0 = posWS.xz / _Ocean_LengthScales.x;
                float2 uv1 = posWS.xz / _Ocean_LengthScales.y;
                float2 uv2 = posWS.xz / _Ocean_LengthScales.z;

                float dist = distance(_WorldSpaceCameraPos, posWS);
                float3 w = CascadeWeights(dist);

                float3 disp = 0;
                disp += SAMPLE_TEXTURE2D_ARRAY_LOD(_Ocean_Displacement, sampler_Ocean_Displacement, uv0, 0, 0).xyz * w.x;
                disp += SAMPLE_TEXTURE2D_ARRAY_LOD(_Ocean_Displacement, sampler_Ocean_Displacement, uv1, 1, 0).xyz * w.y;
                disp += SAMPLE_TEXTURE2D_ARRAY_LOD(_Ocean_Displacement, sampler_Ocean_Displacement, uv2, 2, 0).xyz * w.z;
                posWS += disp;

                o.positionCS = TransformWorldToHClip(posWS);
                o.positionWSFog = float4(posWS, ComputeFogFactor(o.positionCS.z));
                o.uv01 = float4(uv0, uv1);
                o.uv2DistHeight = float4(uv2, dist, disp.y);
                return o;
            }

            // cheap value noise for breaking up foam
            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float VNoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x),
                            lerp(Hash(i + float2(0, 1)), Hash(i + 1.0), f.x), f.y);
            }

            // ---------------------------------------------------- fragment
            half4 Frag(Varyings input) : SV_Target
            {
                float3 posWS = input.positionWSFog.xyz;
                float2 uv0 = input.uv01.xy;
                float2 uv1 = input.uv01.zw;
                float2 uv2 = input.uv2DistHeight.xy;
                float  dist = input.uv2DistHeight.z;
                float3 w = CascadeWeights(dist);

                // ---- normal from analytic derivatives ----
                float4 d = 0;
                d += SAMPLE_TEXTURE2D_ARRAY(_Ocean_Derivatives, sampler_Ocean_Derivatives, uv0, 0) * w.x;
                d += SAMPLE_TEXTURE2D_ARRAY(_Ocean_Derivatives, sampler_Ocean_Derivatives, uv1, 1) * w.y;
                d += SAMPLE_TEXTURE2D_ARRAY(_Ocean_Derivatives, sampler_Ocean_Derivatives, uv2, 2) * w.z;

                float2 slope = float2(d.x / max(1.0 + d.z, 0.25),
                                      d.y / max(1.0 + d.w, 0.25));
                half3 normalWS = normalize(float3(-slope.x, 1.0, -slope.y));

                half normalFade = saturate(1.0 - dist / _NormalFadeDistance);
                normalWS = normalize(lerp(half3(0, 1, 0), normalWS, lerp(0.3, 1.0, normalFade)));
                half roughness = lerp(_DistantRoughness, _Roughness, normalFade);

                half3 V = normalize(_WorldSpaceCameraPos - posWS);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);

                float4 shadowCoord = TransformWorldToShadowCoord(posWS);
                Light light = GetMainLight(shadowCoord);
                half shadow = light.shadowAttenuation;

                // ---- refraction + depth-based absorption ----
                float surfDepth = input.positionCS.w; // eye depth of surface
                float2 distortion = normalWS.xz * (_RefractionStrength / max(surfDepth, 0.5));
                float2 refractUV = screenUV + distortion;

                float sceneDepth = LinearEyeDepth(SampleSceneDepth(refractUV), _ZBufferParams);
                if (sceneDepth < surfDepth) // distorted sample hit something above water
                {
                    refractUV = screenUV;
                    sceneDepth = LinearEyeDepth(SampleSceneDepth(refractUV), _ZBufferParams);
                }
                float viewWaterDepth = max(sceneDepth - surfDepth, 0.0);

                half3 sceneColor = SampleSceneColor(refractUV);
                half  waterFog = 1.0 - saturate(exp(-viewWaterDepth * _Density));
                half3 deepTint = _DeepColor.rgb * lerp(half3(1, 1, 1), light.color, 0.5);
                half3 refraction = lerp(sceneColor, deepTint, waterFog);

                // ---- subsurface scattering (SoT-style crest glow) ----
                half crest = saturate(input.uv2DistHeight.w / _SSSHeight);
                half towardSun = pow(saturate(dot(V, -light.direction)), 4.0);
                half rim = pow(1.0 - saturate(dot(normalWS, V)), 2.0);
                half sssMask = _SSSStrength * crest * towardSun * rim + _SSSAmbient * crest;
                half3 sss = _ScatterColor.rgb * light.color * shadow * sssMask;

                // ---- reflection + fresnel ----
                half3 reflectVec = reflect(-V, normalWS);
                reflectVec.y = abs(reflectVec.y); // keep above horizon
                half3 reflection = GlossyEnvironmentReflection(reflectVec, posWS, roughness, 1.0h, screenUV);

                half NoV = saturate(dot(normalWS, V));
                half fresnel = 0.02 + 0.98 * pow(1.0 - NoV, 5.0);

                // ---- sun specular (GGX) ----
                half3 H = normalize(light.direction + V);
                half NoH = saturate(dot(normalWS, H));
                half a = roughness * roughness;
                half a2 = a * a;
                half denom = NoH * NoH * (a2 - 1.0) + 1.0;
                half ggx = min(a2 / (PI * denom * denom + 1e-5), 200.0);
                half3 specular = light.color * shadow * ggx * fresnel * _SpecularBoost;

                // ---- foam from accumulated Jacobian ----
                half turb = 0;
                turb += SAMPLE_TEXTURE2D_ARRAY(_Ocean_Turbulence, sampler_Ocean_Turbulence, uv0, 0).r;
                turb += SAMPLE_TEXTURE2D_ARRAY(_Ocean_Turbulence, sampler_Ocean_Turbulence, uv1, 1).r * w.y;
                turb += SAMPLE_TEXTURE2D_ARRAY(_Ocean_Turbulence, sampler_Ocean_Turbulence, uv2, 2).r * w.z;
                turb /= (1.0 + w.y + w.z);

                half foam = saturate((_FoamBias - turb) * _FoamSharpness);
                half n = VNoise(posWS.xz * _FoamNoiseScale * 4.0) * 0.6
                       + VNoise(posWS.xz * _FoamNoiseScale * 13.0) * 0.4;
                foam *= lerp(0.55, 1.0, n);

                half3 ambient = SampleSH(normalWS);
                half3 foamLit = _FoamColor.rgb *
                    (ambient + light.color * shadow * saturate(dot(normalWS, light.direction)));

                // ---- combine ----
                half3 color = lerp(refraction + sss, reflection, saturate(fresnel));
                color += specular;
                color = lerp(color, foamLit, foam);

                color = MixFog(color, input.positionWSFog.w);
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
