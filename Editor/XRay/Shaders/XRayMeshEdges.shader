Shader "Hidden/Orbiters/XRayGizmos/MeshEdges"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 0.65)
        _SurfaceOffset ("Surface Offset", Float) = 0.0015
        _LineWidth ("Line Width", Float) = 1.25
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" }

        Pass
        {
            Name "MeshEdges"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 barycentric : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 barycentric : TEXCOORD0;
            };

            fixed4 _Color;
            float _SurfaceOffset;
            float _LineWidth;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex + float4(v.normal * _SurfaceOffset, 0));
                o.barycentric = v.barycentric;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 barycentric = float3(
                    i.barycentric.x,
                    i.barycentric.y,
                    1.0 - i.barycentric.x - i.barycentric.y);
                float edgeDistance = min(min(barycentric.x, barycentric.y), barycentric.z);
                float edgeWidth = max(fwidth(edgeDistance) * _LineWidth, 0.0001);
                float edgeAlpha = 1.0 - smoothstep(0.0, edgeWidth, edgeDistance);
                clip(edgeAlpha - 0.001);

                fixed4 color = _Color;
                color.a *= edgeAlpha;
                return color;
            }
            ENDCG
        }
    }
}
