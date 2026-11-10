// Weight paint drawn over a renderer by the Scene view's command buffer (XRayWeightPaintGpu.cs). The renderer's own
// vertices come in (skinned, with blendshapes and live edits); each one's weight is read from a buffer of one float per
// mesh vertex by its vertex index, and turned into Blender's ramp in the vertex stage exactly as
// XRayWeightPaintService.EvaluateBlenderWeightColor does for the hidden-copy path (so both look the same).
Shader "Hidden/Orbiters/XRayGizmos/WeightPaintBuffer"
{
    Properties
    {
        _Alpha ("Alpha", Range(0, 1)) = 0.82
        _SurfaceOffset ("Surface Offset", Float) = 0.001
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+20" "IgnoreProjector"="True" }

        Pass
        {
            Name "WeightPaintBuffer"
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            // Structured buffers in the vertex stage.
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // Set by the command buffer before each renderer's draws.
            StructuredBuffer<float> _OrbitersWeightPaintWeights;
            int _OrbitersWeightPaintCount;
            float _Alpha;
            float _SurfaceOffset;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                uint id : SV_VertexID;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
            };

            float3 BlenderWeightRamp(float w)
            {
                if (w <= 0.25) return lerp(float3(0, 0, 1), float3(0, 1, 1), w / 0.25);
                if (w <= 0.5) return lerp(float3(0, 1, 1), float3(0, 1, 0), (w - 0.25) / 0.25);
                if (w <= 0.75) return lerp(float3(0, 1, 0), float3(1, 1, 0), (w - 0.5) / 0.25);
                return lerp(float3(1, 1, 0), float3(1, 0, 0), (w - 0.75) / 0.25);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex + float4(v.normal * _SurfaceOffset, 0));
                // A vertex beyond the buffer (a mesh swapped before the overlay heard of it) shows no weight.
                float w = (int)v.id < _OrbitersWeightPaintCount ? _OrbitersWeightPaintWeights[v.id] : 0.0;
                o.color = float4(BlenderWeightRamp(saturate(w)), _Alpha);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }

    Fallback Off
}
