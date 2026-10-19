Shader "Hidden/Orbiters/XRayGizmos/WeightPaint"
{
    Properties
    {
        _Alpha ("Alpha", Range(0, 1)) = 0.82
        _SurfaceOffset ("Surface Offset", Float) = 0.001
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" }

        Pass
        {
            Name "WeightPaint"
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
            };

            float _Alpha;
            float _SurfaceOffset;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex + float4(v.normal * _SurfaceOffset, 0));
                o.color = v.color;
                o.color.a *= _Alpha;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}

