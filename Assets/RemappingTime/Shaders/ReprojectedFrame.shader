// Draws one buffered passthrough camera frame on a quad that matches the camera frustum.
//
// Features (all driven from ReprojectedFrameView / TimelineWindow):
//  * Frame blending: cross-fades two neighbouring frames (_TexA → _TexB by _Blend), so slow motion
//    is smooth rather than holding each 60 Hz frame for several display frames.
//  * Feathered edges: alpha falls off near the border, so the canvas dissolves into the live
//    system passthrough behind it instead of reading as a rectangle.
//  * Lens scope: optionally restricts the remapped image to a soft circle (a "time lens").
//  * Rate cues: colour tint / desaturation and an edge glow that the view sets from the current
//    playback rate and delay (the "Transparency" dimension of the design space).
//
// Output is premultiplied alpha. Passthrough is composited *under* the eye buffer using its alpha,
// so alpha = 0 shows the real-time system passthrough and alpha = 1 shows the remapped frame.
Shader "RemappingTime/ReprojectedFrame"
{
    Properties
    {
        _TexA ("Frame A (older)", 2D) = "black" {}
        _TexB ("Frame B (newer)", 2D) = "black" {}
        _Blend ("Blend A to B", Range(0, 1)) = 0
        _Opacity ("Opacity", Range(0, 1)) = 1
        _Feather ("Edge feather (fraction of height)", Range(0, 0.5)) = 0.08
        _Aspect ("Image aspect (w/h)", Float) = 1.3333
        _TintColor ("Tint colour", Color) = (1, 1, 1, 1)
        _TintAmount ("Tint amount", Range(0, 1)) = 0
        _Desat ("Desaturation", Range(0, 1)) = 0
        _GlowColor ("Edge glow colour", Color) = (1, 1, 1, 1)
        _Glow ("Edge glow", Range(0, 2)) = 0
        _LensOn ("Lens on", Float) = 0
        _LensCenter ("Lens centre (uv)", Vector) = (0.5, 0.5, 0, 0)
        _LensRadius ("Lens radius (fraction of height)", Float) = 0.2
        _LensFeather ("Lens feather", Float) = 0.05
        _EyeSelect ("Select eye by stereo index", Float) = 0
        _EyeIndex ("Eye index (0 left, 1 right)", Float) = 0
    }

    SubShader
    {
        // Drawn before regular content so virtual objects and UI appear on top of the passthrough canvas.
        Tags { "Queue" = "Background+10" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _TexA;
            sampler2D _TexB;
            float _Blend, _Opacity, _Feather, _Aspect;
            float4 _TintColor, _GlowColor, _LensCenter;
            float _TintAmount, _Desat, _Glow;
            float _LensOn, _LensRadius, _LensFeather;
            float _EyeSelect, _EyeIndex;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;

            #if defined(USING_STEREO_MATRICES)
                // Single-pass stereo (multiview / instancing): each eye's quad is drawn into both
                // views, so collapse it to a degenerate point in the view it does not belong to.
                // With per-eye cameras (the default scene setup) culling layers do this instead.
                if (_EyeSelect > 0.5 && abs((float)unity_StereoEyeIndex - _EyeIndex) > 0.5)
                    o.pos = float4(2, 2, 2, 1);
            #endif
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 c = lerp(tex2D(_TexA, i.uv).rgb, tex2D(_TexB, i.uv).rgb, _Blend);

                // Distance to the nearest edge, measured in units of image height.
                float2 e = min(i.uv, 1.0 - i.uv);
                e.x *= _Aspect;
                float edge = min(e.x, e.y);
                float mask = _Feather > 1e-4 ? smoothstep(0.0, _Feather, edge) : 1.0;
                float glowBand = 1.0 - saturate(edge / max(_Feather * 2.5, 0.02));

                if (_LensOn > 0.5)
                {
                    float2 d = i.uv - _LensCenter.xy;
                    d.x *= _Aspect;
                    float dist = length(d);
                    float lens = 1.0 - smoothstep(_LensRadius - _LensFeather, _LensRadius, dist);
                    mask *= lens;
                    float ring = smoothstep(_LensRadius - 2.5 * _LensFeather, _LensRadius - _LensFeather, dist) * lens;
                    glowBand = max(glowBand * lens, ring);
                }

                // Remapping cues (Transparency): tint and desaturation by playback speed, plus an edge glow.
                float lum = dot(c, float3(0.2126, 0.7152, 0.0722));
                c = lerp(c, lum.xxx, _Desat);
                c *= lerp(float3(1, 1, 1), _TintColor.rgb, _TintAmount);
                c += _GlowColor.rgb * (_Glow * glowBand);

                float alpha = saturate(_Opacity * mask);
                return float4(c * alpha, alpha);
            }
            ENDCG
        }
    }
}
