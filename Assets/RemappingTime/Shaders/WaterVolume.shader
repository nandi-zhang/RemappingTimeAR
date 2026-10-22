// A block of virtual water that refracts the real world behind it.
//
// The real scene behind each surface point is looked up in that eye's passthrough camera image: the point
// (nudged by the animated surface normal, which is what makes it look like refraction) is projected into
// the camera with _WorldToCam and the camera intrinsics in _Intr. Each eye uses its own camera, so the
// refraction is stereo-correct. On top: a cool tint, fresnel rim, specular glints and a caustic shimmer.
// _Inside (0..1) is raised while a hand is in the water.
Shader "RemappingTime/WaterVolume"
{
    Properties
    {
        _CamTex ("Camera image", 2D) = "grey" {}
        _Intr ("Camera intrinsics (a, b, c, d)", Vector) = (1, 0.5, 1, 0.5)
        _Tint ("Water tint", Color) = (0.62, 0.86, 1.0, 1)
        _Deep ("Deep colour", Color) = (0.05, 0.28, 0.45, 1)
        _Opacity ("Opacity", Range(0, 1)) = 0.9
        _Refraction ("Refraction (m)", Range(0, 0.1)) = 0.025
        _WaveAmp ("Wave strength", Range(0, 1)) = 0.35
        _Inside ("Hand inside (0..1)", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _CamTex;
            float4x4 _WorldToCam;
            float4 _Intr, _Tint, _Deep;
            float _Opacity, _Refraction, _WaveAmp, _Inside;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float3 wp = i.worldPos;
                float3 n = normalize(i.worldNormal);
                float3 V = normalize(_WorldSpaceCameraPos - wp);
                float t = _Time.y;

                // Animated ripples: the gradient of a few travelling sine waves tilts the normal in the face plane.
                float3 t1 = normalize(cross(n, abs(n.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
                float3 t2 = cross(n, t1);
                float2 q = float2(dot(wp, t1), dot(wp, t2)) * 28.0;
                float speed = 1.0 + 1.5 * _Inside;
                float2 g = float2(
                    cos(q.x + t * 1.7 * speed) + 0.6 * cos(1.9 * q.x - 1.3 * q.y + t * 2.3 * speed),
                    cos(1.1 * q.y - t * 1.3 * speed) + 0.5 * cos(2.2 * q.y + 0.7 * q.x + t * 2.9 * speed));
                float3 pn = normalize(n + (t1 * g.x + t2 * g.y) * _WaveAmp * 0.25);

                // Refraction: look up the camera image slightly "inside" the surface along the bent normal.
                float3 sp = wp - pn * _Refraction;
                float3 pc = mul(_WorldToCam, float4(sp, 1)).xyz;
                float3 refr = _Deep.rgb;
                if (pc.z > 0.01)
                {
                    float2 uv = float2(_Intr.x * pc.x / pc.z + _Intr.y, _Intr.z * pc.y / pc.z + _Intr.w);
                    refr = tex2D(_CamTex, saturate(uv)).rgb;
                }

                float fres = pow(1.0 - saturate(dot(pn, V)), 3.0);
                float3 col = refr * _Tint.rgb;
                col = lerp(col, _Deep.rgb, 0.18 + 0.15 * _Inside);

                float3 L = normalize(float3(0.3, 1.0, 0.25));
                float spec = pow(saturate(dot(pn, normalize(L + V))), 90.0) * 1.1;
                float caustic = pow(saturate(0.5 + 0.5 * sin(q.x * 0.31 + t * 1.9) * sin(q.y * 0.29 - t * 1.6)), 8.0) * 0.35;

                float2 e = min(i.uv, 1.0 - i.uv);
                float edge = 1.0 - smoothstep(0.0, 0.025, min(e.x, e.y));

                col += fres * 0.35 * float3(0.85, 0.95, 1.0) + spec + caustic * _Tint.rgb + edge * (0.18 + 0.2 * _Inside);
                float a = saturate(_Opacity * (0.85 + 0.15 * fres));
                return float4(col, a);
            }
            ENDCG
        }
    }
}
