// Polygon edges of a renderer, drawn over it by the Scene view's command buffer (XRayMeshEdgeService.cs). The renderer's
// own vertices come in (skinned, with blendshapes and live edits); a geometry shader gives each triangle barycentric
// coordinates and the fragment keeps a line about _LineWidth pixels wide along each edge.
Shader "Hidden/Orbiters/XRayGizmos/MeshEdges"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 0.65)
        _LineWidth ("Line Width (pixels)", Float) = 1.25
        _DepthPull ("Depth Pull", Float) = 0.0006
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    fixed4 _Color;
    float _LineWidth;
    float _DepthPull;

    struct appdata
    {
        float4 vertex : POSITION;
    };

    // The vertex where the mesh shows it, a little nearer the camera so the edges pass the depth test over their own
    // surface: the view-space point scaled toward the eye keeps its pixel (perspective), or moves along the view axis
    // only (orthographic).
    float4 EdgeClipPos(float4 vertex)
    {
        float3 view = UnityObjectToViewPos(vertex.xyz);
        float pull = 1.0 - _DepthPull;
        view.z *= pull;
        view.xy *= lerp(pull, 1.0, unity_OrthoParams.w);
        return UnityViewToClipPos(view);
    }
    ENDCG

    // GPUs with geometry shaders (Direct3D 11, Vulkan, OpenGL Core).
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "IgnoreProjector"="True" }

        Pass
        {
            Name "EDGES"
            Cull Off
            ZWrite Off
            ZTest [_ZTest]
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 4.0
            #pragma require geometry
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag

            struct v2g
            {
                float4 pos : SV_POSITION;
            };

            struct g2f
            {
                float4 pos : SV_POSITION;
                float3 barycentric : TEXCOORD0;
            };

            v2g vert(appdata v)
            {
                v2g o;
                o.pos = EdgeClipPos(v.vertex);
                return o;
            }

            [maxvertexcount(3)]
            void geom(triangle v2g input[3], inout TriangleStream<g2f> stream)
            {
                g2f o;
                o.pos = input[0].pos;
                o.barycentric = float3(1.0, 0.0, 0.0);
                stream.Append(o);
                o.pos = input[1].pos;
                o.barycentric = float3(0.0, 1.0, 0.0);
                stream.Append(o);
                o.pos = input[2].pos;
                o.barycentric = float3(0.0, 0.0, 1.0);
                stream.Append(o);
            }

            fixed4 frag(g2f i) : SV_Target
            {
                // Each barycentric coordinate is 0 on one edge; its screen-space rate turns it into pixels.
                float3 width = max(fwidth(i.barycentric) * _LineWidth, float3(0.0001, 0.0001, 0.0001));
                float3 ramp = smoothstep(float3(0.0, 0.0, 0.0), width, i.barycentric);
                float alpha = 1.0 - min(ramp.x, min(ramp.y, ramp.z));
                clip(alpha - 0.001);

                fixed4 color = _Color;
                color.a *= alpha;
                return color;
            }
            ENDCG
        }
    }

    // GPUs without geometry shaders (Metal): one colour, drawn by the editor with GL.wireframe on.
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay" "IgnoreProjector"="True" }

        Pass
        {
            Name "WIRE"
            Cull Off
            ZWrite Off
            ZTest [_ZTest]
            Offset -1, -1
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(appdata v) : SV_POSITION
            {
                return EdgeClipPos(v.vertex);
            }

            fixed4 frag(float4 pos : SV_POSITION) : SV_Target
            {
                return _Color;
            }
            ENDCG
        }
    }

    Fallback Off
}
