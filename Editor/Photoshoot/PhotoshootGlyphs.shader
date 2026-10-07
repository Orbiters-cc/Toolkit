// Glyph coverage of a font atlas, for the photoshoot's text: overlapping glyphs and outline offsets keep the most covered value.
Shader "Hidden/Orbiters/PhotoshootGlyphs"
{
    Properties
    {
        _MainTex ("Font Texture", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        BlendOp Max
        Blend One One

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed coverage = tex2D(_MainTex, i.uv).a;
                return fixed4(coverage, coverage, coverage, coverage);
            }
            ENDCG
        }
    }
}
