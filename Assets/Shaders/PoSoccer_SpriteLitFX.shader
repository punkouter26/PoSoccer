// PoSoccer sprite shader - the first custom shader in the project.
//
// Derived from URP's Sprite-Lit-Default so it keeps the full 2D lighting path
// (CombinedShapeLightShared), including normal maps and every Light2D in the
// stadium. All it adds is a procedural modulation of the albedo BEFORE lighting,
// which is what lets one shader serve the pitch, the ball, the players and the
// advertising boards.
//
// Every effect strength defaults to 0, so a material using this shader with
// default values is pixel-identical to Sprite-Lit-Default. Features are opted
// into per material by Agent_Surfaces at runtime.
//
//   _StripeStrength / _StripeCount / _StripeAngle  mown-grass banding
//   _SheenStrength  / _SheenSpeed  / _SheenWidth   travelling highlight
//   _RimColor       / _RimStrength / _RimPower     team rim on player bodies
//   _EmissionBoost                                 celebration flare
//   _NetStrength    / _NetTiling   / _NetRipple    goal net (cuts ALPHA, not albedo)
//
// No shader_feature keywords on purpose: four toggles would mean sixteen
// variants for effects that cost a few ALU each, and keyword-free branches keep
// the SRP Batcher's constant layout identical across every material.
Shader "PoSoccer/SpriteLitFX"
{
    Properties
    {
        _MainTex("Diffuse", 2D) = "white" {}
        _MaskTex("Mask", 2D) = "white" {}
        _NormalMap("Normal Map", 2D) = "bump" {}
        [MaterialToggle] _ZWrite("ZWrite", Float) = 0

        _StripeStrength("Stripe Strength", Range(0, 0.5)) = 0
        _StripeCount("Stripe Count", Float) = 10
        _StripeAngle("Stripe Angle (radians)", Float) = 0

        _SheenStrength("Sheen Strength", Range(0, 1)) = 0
        _SheenSpeed("Sheen Speed", Float) = 0.25
        _SheenWidth("Sheen Width", Float) = 6

        _RimColor("Rim Color", Color) = (1,1,1,1)
        _RimStrength("Rim Strength", Range(0, 3)) = 0
        _RimPower("Rim Power", Float) = 4

        _EmissionBoost("Emission Boost", Range(0, 4)) = 0

        _NetStrength("Net Strength", Range(0, 1)) = 0
        _NetTiling("Net Cords Across", Float) = 14
        _NetRipple("Net Ripple", Range(0, 1)) = 0

        // Legacy properties, kept so this can fall back to the sprite shader.
        [HideInInspector] _Color("Tint", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _AlphaTex("External Alpha", 2D) = "white" {}
        [HideInInspector] _EnableExternalAlpha("Enable External Alpha", Float) = 0
    }

    SubShader
    {
        Tags {"Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
        Cull Off
        ZWrite [_ZWrite]

        Pass
        {
            Tags { "LightMode" = "Universal2D" }

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex LitVertex
            #pragma fragment LitFragment

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/ShapeLightShared.hlsl"

            #pragma multi_compile_instancing
            #pragma multi_compile _ SKINNED_SPRITE

            struct Attributes
            {
                COMMON_2D_INPUTS
                half4 color        : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_LIT_OUTPUTS
                half4 color        : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Lit2DCommon.hlsl"

            // NOTE: identical layout in every pass - the SRP Batcher cannot cope
            // with a constant buffer that differs between passes of one shader.
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half  _StripeStrength;
                half  _StripeCount;
                half  _StripeAngle;
                half  _SheenStrength;
                half  _SheenSpeed;
                half  _SheenWidth;
                half4 _RimColor;
                half  _RimStrength;
                half  _RimPower;
                half  _EmissionBoost;
                half  _NetStrength;
                half  _NetTiling;
                half  _NetRipple;
            CBUFFER_END

            #include "PoSoccerFX.hlsl"

            Varyings LitVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonLitVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 LitFragment(Varyings input) : SV_Target
            {
                // Mirrors CommonLitFragment, with the FX applied to the albedo
                // before it reaches the lighting - so stripes and rims are LIT
                // rather than pasted over the top of the lighting result.
                half4 main = input.color * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                main.rgb = PoSoccerApplyFX(main.rgb, input.uv);
                // Before InitializeSurfaceData: the net has to be cut out of the
                // alpha that feeds the lighting, or the holes stay lit.
                main.a *= PoSoccerNetMask(input.uv);

                const half4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, input.uv);
                const half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));

                SurfaceData2D surfaceData;
                InputData2D inputData;
                InitializeSurfaceData(main.rgb, main.a, mask, normalTS, surfaceData);
                InitializeInputData(input.uv, input.lightingUV, inputData);

                return CombinedShapeLightShared(surfaceData, inputData);
            }
            ENDHLSL
        }

        Pass
        {
            Tags { "LightMode" = "NormalsRendering"}

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex NormalsRenderingVertex
            #pragma fragment NormalsRenderingFragment

            #pragma multi_compile_instancing
            #pragma multi_compile _ SKINNED_SPRITE

            struct Attributes
            {
                COMMON_2D_NORMALS_INPUTS
                float4 color        : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_NORMALS_OUTPUTS
                half4   color           : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Normals2DCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half  _StripeStrength;
                half  _StripeCount;
                half  _StripeAngle;
                half  _SheenStrength;
                half  _SheenSpeed;
                half  _SheenWidth;
                half4 _RimColor;
                half  _RimStrength;
                half  _RimPower;
                half  _EmissionBoost;
                half  _NetStrength;
                half  _NetTiling;
                half  _NetRipple;
            CBUFFER_END

            Varyings NormalsRenderingVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonNormalsVertex(input);
                o.color = input.color * _Color * unity_SpriteColor;
                return o;
            }

            half4 NormalsRenderingFragment(Varyings input) : SV_Target
            {
                SetUpSpriteInstanceProperties();
                return CommonNormalsFragment(input, input.color);
            }
            ENDHLSL
        }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" "Queue"="Transparent" "RenderType"="Transparent"}

            HLSLPROGRAM
            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/Core2D.hlsl"

            #pragma vertex UnlitVertex
            #pragma fragment UnlitFragment

            struct Attributes
            {
                COMMON_2D_INPUTS
                half4 color : COLOR;
                UNITY_SKINNED_VERTEX_INPUTS
            };

            struct Varyings
            {
                COMMON_2D_OUTPUTS
                half4 color : COLOR;
            };

            #include "Packages/com.unity.render-pipelines.universal/Shaders/2D/Include/2DCommon.hlsl"

            #pragma multi_compile_instancing
            #pragma multi_compile _ SKINNED_SPRITE

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half  _StripeStrength;
                half  _StripeCount;
                half  _StripeAngle;
                half  _SheenStrength;
                half  _SheenSpeed;
                half  _SheenWidth;
                half4 _RimColor;
                half  _RimStrength;
                half  _RimPower;
                half  _EmissionBoost;
                half  _NetStrength;
                half  _NetTiling;
                half  _NetRipple;
            CBUFFER_END

            #include "PoSoccerFX.hlsl"

            Varyings UnlitVertex(Attributes input)
            {
                UNITY_SKINNED_VERTEX_COMPUTE(input);
                SetUpSpriteInstanceProperties();
                input.positionOS = UnityFlipSprite(input.positionOS, unity_SpriteProps.xy);

                Varyings o = CommonUnlitVertex(input);
                o.color = input.color *_Color * unity_SpriteColor;
                return o;
            }

            half4 UnlitFragment(Varyings input) : SV_Target
            {
                half4 result = CommonUnlitFragment(input, input.color);
                result.rgb = PoSoccerApplyFX(result.rgb, input.uv);
                result.a *= PoSoccerNetMask(input.uv);
                return result;
            }
            ENDHLSL
        }
    }

    Fallback "Sprites/Default"
}
