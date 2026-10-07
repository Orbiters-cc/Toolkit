// The photoshoot's depth pass for its effects: every object of the stage drawn with this shader (a replacement render)
// writes its view-space normal (rgb) and its distance in front of the camera in metres (a). Both sides are drawn, so
// hair cards and thin clothes count; their alpha cutouts don't.
Shader "Hidden/Orbiters/PhotoshootDepthNormals"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float depth : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = COMPUTE_VIEW_NORMAL;
                o.depth = -UnityObjectToViewPos(v.vertex).z;
                return o;
            }

            float4 frag(v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.normal) * (facing > 0 ? 1.0 : -1.0);
                return float4(n, i.depth);
            }
            ENDCG
        }
    }
}
