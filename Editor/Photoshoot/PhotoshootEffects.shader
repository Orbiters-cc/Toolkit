// The photoshoot's effects over a rendered shot. Sizes are fractions of the image (or scale with its height), so the small
// live preview looks like the full-size capture. Passes: 0 ambient occlusion, 1 occlusion applied, 2 depth of field,
// 3-5 bloom (threshold, down, up), 6 the rest in one go (lens distortion, chromatic aberration, bloom, comic halftone,
// vignette, grain).
Shader "Hidden/Orbiters/PhotoshootEffects"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _DepthNormals;
    sampler2D _AoTex;
    float4 _AoTex_TexelSize;
    sampler2D _BloomTex;
    float _HasDepth;
    float4 _Frustum;  // x: tan(half vertical fov) * aspect, y: tan(half vertical fov)
    float4 _Screen;   // destination: width, height, 1 / width, 1 / height
    float4 _Ao;       // strength, radius (m), bias (m)
    float4 _Dof;      // focus distance (m), half the sharp band (m), largest blur (fraction of the height)
    float4 _Bloom;    // threshold, knee, intensity
    float4 _Lens;     // distortion, chromatic aberration, vignette, grain
    float4 _Halftone; // on (1) or off, dot cell (px), outline width (px), grain seed
    float4 _Comic;    // tones, dots on (1) or off
    float _Focus;     // distance of the avatar's view point (m)

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vert(appdata_img v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    float4 DepthNormal(float2 uv) { return tex2Dlod(_DepthNormals, float4(uv, 0, 0)); }

    // Nothing drawn there (cleared to 0): far away.
    float Depth(float2 uv)
    {
        float d = DepthNormal(uv).w;
        return d > 0 ? d : 1e4;
    }

    // View space with +z away from the camera, in metres.
    float3 ViewPosition(float2 uv, float depth) { return float3((uv * 2 - 1) * _Frustum.xy * depth, depth); }

    float Hash(float2 p)
    {
        p = frac(p * float2(123.34, 456.21));
        p += dot(p, p + 45.32);
        return frac(p.x * p.y);
    }

    float Luma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

    float3 Box4(sampler2D t, float2 uv, float2 texel)
    {
        float4 o = texel.xyxy * float4(-1, -1, 1, 1);
        return (tex2D(t, uv + o.xy).rgb + tex2D(t, uv + o.zy).rgb + tex2D(t, uv + o.xw).rgb + tex2D(t, uv + o.zw).rgb) * 0.25;
    }

    // ---- Ambient occlusion: how much nearby geometry hides each point, from the depth pass ------------------------

    #define AO_SAMPLES 14

    float4 FragOcclusion(v2f i) : SV_Target
    {
        float4 dn = DepthNormal(i.uv);
        if (dn.w <= 0) return 1;
        float3 n = normalize(float3(dn.x, dn.y, -dn.z));
        float3 p = ViewPosition(i.uv, dn.w);
        float3 up = abs(n.y) < 0.99 ? float3(0, 1, 0) : float3(1, 0, 0);
        float3 tx = normalize(cross(up, n));
        float3 ty = cross(n, tx);
        float spin = Hash(floor(i.uv * _Screen.xy * 0.5)) * 6.2831853;
        float occlusion = 0;
        [unroll]
        for (int k = 0; k < AO_SAMPLES; k++)
        {
            // A cosine-weighted spiral over the hemisphere around the normal, nearer points first.
            float t = (k + 0.5) / AO_SAMPLES;
            float a = spin + k * 2.39996323;
            float r = sqrt(t);
            float3 h = float3(cos(a) * r, sin(a) * r, sqrt(1 - t));
            float3 s = p + (tx * h.x + ty * h.y + n * h.z) * _Ao.y * lerp(0.15, 1, t * t);
            float2 suv = s.xy / (s.z * _Frustum.xy) * 0.5 + 0.5;
            float sd = Depth(suv);
            float near = saturate(_Ao.y / max(abs(p.z - sd), 1e-4));
            occlusion += (sd < s.z - _Ao.z ? 1.0 : 0.0) * near;
        }
        return 1 - occlusion / AO_SAMPLES;
    }

    float4 FragApplyOcclusion(v2f i) : SV_Target
    {
        float4 c = tex2D(_MainTex, i.uv);
        float d0 = Depth(i.uv);
        float sum = 0;
        float weights = 0;
        // Smoothed across neighbours at about the same depth, so the noise goes and silhouettes stay sharp.
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            [unroll]
            for (int y = -2; y <= 2; y++)
            {
                float2 uv = i.uv + float2(x, y) * _AoTex_TexelSize.xy;
                float w = 1 / (1 + abs(Depth(uv) - d0) * 40);
                sum += tex2D(_AoTex, uv).r * w;
                weights += w;
            }
        }
        float ao = sum / max(weights, 1e-4);
        c.rgb *= pow(saturate(ao), 3 * _Ao.x);
        return c;
    }

    // ---- Depth of field: sharp around the avatar's view point, blurred before and behind ------------------------

    #define DOF_TAPS 72

    float Blur(float depth) { return saturate((abs(depth - _Dof.x) - _Dof.y) / max(_Dof.y * 3, 1e-3)); }

    float4 FragDepthOfField(v2f i) : SV_Target
    {
        float4 center = tex2D(_MainTex, i.uv);
        float d0 = Depth(i.uv);
        float b0 = Blur(d0);
        float2 radius = _Dof.z * float2(_Screen.y * _Screen.z, 1);
        // The spiral turned per pixel: a wide blur shows fine noise rather than rings.
        float spin = Hash(i.uv * _Screen.xy) * 6.2831853;
        float3 sum = center.rgb;
        float weights = 1;
        [loop]
        for (int k = 1; k < DOF_TAPS; k++)
        {
            float t = sqrt((k + 0.5) / DOF_TAPS);
            float a = spin + k * 2.39996323;
            float2 uv = i.uv + float2(cos(a), sin(a)) * t * radius;
            float dk = Depth(uv);
            // A point spreads over this pixel when its own blur reaches it. One behind this pixel spreads no more than
            // this pixel is blurred, so a sharp avatar never takes on the background around it.
            float bk = dk > d0 ? min(Blur(dk), b0) : Blur(dk);
            float w = saturate((bk - t) * 8 + 1);
            sum += tex2D(_MainTex, uv).rgb * w;
            weights += w;
        }
        return float4(sum / weights, center.a);
    }

    // ---- Bloom: what is brighter than the threshold, blurred over a chain of smaller copies ------------------------

    float4 FragPrefilter(v2f i) : SV_Target
    {
        float3 c = Box4(_MainTex, i.uv, _MainTex_TexelSize.xy);
        float brightness = max(c.r, max(c.g, c.b));
        float soft = clamp(brightness - _Bloom.x + _Bloom.y, 0, 2 * _Bloom.y);
        soft = soft * soft / (4 * _Bloom.y + 1e-5);
        float contribution = max(soft, brightness - _Bloom.x) / max(brightness, 1e-5);
        return float4(c * contribution, 1);
    }

    float4 FragDown(v2f i) : SV_Target { return float4(Box4(_MainTex, i.uv, _MainTex_TexelSize.xy), 1); }

    float4 FragUp(v2f i) : SV_Target
    {
        float4 o = _MainTex_TexelSize.xyxy * float4(1, 1, -1, 0);
        float3 s = tex2D(_MainTex, i.uv - o.xy).rgb;
        s += tex2D(_MainTex, i.uv - o.wy).rgb * 2;
        s += tex2D(_MainTex, i.uv - o.zy).rgb;
        s += tex2D(_MainTex, i.uv + o.zw).rgb * 2;
        s += tex2D(_MainTex, i.uv).rgb * 4;
        s += tex2D(_MainTex, i.uv + o.xw).rgb * 2;
        s += tex2D(_MainTex, i.uv + o.zy).rgb;
        s += tex2D(_MainTex, i.uv + o.wy).rgb * 2;
        s += tex2D(_MainTex, i.uv + o.xy).rgb;
        return float4(s / 16, 1);
    }

    // ---- Lens, comic halftone, vignette and grain -----------------------------------------------------------------

    float Aspect() { return _Screen.x * _Screen.w; }

    // Barrel distortion (positive) scaled so the corners stay in the picture.
    float2 Distort(float2 uv)
    {
        float k = _Lens.x;
        if (k == 0) return uv;
        float aspect = Aspect();
        float2 c = (uv - 0.5) * float2(aspect, 1);
        float corner = 0.25 * (aspect * aspect + 1);
        c *= (1 + k * dot(c, c)) / (1 + max(k, 0) * corner);
        return c / float2(aspect, 1) + 0.5;
    }

    // Where the depth or the surface turns sharply: the comic's ink lines.
    float Outline(float2 uv)
    {
        if (_HasDepth < 0.5) return 0;
        float2 t = _Halftone.z * _Screen.zw;
        float4 c = DepthNormal(uv);
        float4 l = DepthNormal(uv - float2(t.x, 0));
        float4 r = DepthNormal(uv + float2(t.x, 0));
        float4 u = DepthNormal(uv + float2(0, t.y));
        float4 d = DepthNormal(uv - float2(0, t.y));
        float dc = c.w > 0 ? c.w : 1e4;
        float jump = abs((l.w > 0 ? l.w : 1e4) - (r.w > 0 ? r.w : 1e4)) + abs((u.w > 0 ? u.w : 1e4) - (d.w > 0 ? d.w : 1e4));
        float depthEdge = saturate(jump / (dc * 0.04) - 0.5);
        float normalEdge = saturate((2 - dot(l.xyz, r.xyz) - dot(u.xyz, d.xyz)) * 1.2 - 0.35);
        return saturate(max(depthEdge, normalEdge));
    }

    float3 Comic(float3 c, float2 uv)
    {
        float luma = Luma(c);
        // The avatar (up to a little behind its view point) gets the whole look and the ink lines; what lies behind
        // keeps half its own shading, so a photo doesn't break into blobs.
        float subject = _HasDepth > 0.5 ? saturate((_Focus + 1.4 - Depth(uv)) / 0.5) : 1;
        // Flat colours in as many tones as chosen: the brightness steps, the hue stays.
        float steps = max(_Comic.x - 1, 1);
        float level = floor(luma * steps + 0.5) / steps;
        float3 flat = saturate(c * (max(level, 0.05) / max(luma, 1e-3)));
        flat = saturate(lerp(Luma(flat).xxx, flat, 1.2));
        flat = lerp(lerp(c, flat, 0.5), flat, subject);
        if (_Comic.y > 0.5)
        {
            // A print screen: ink dots on a 45 degree grid, growing with the darkness until they merge.
            float2 p = uv * _Screen.xy;
            float2 grid = float2(p.x + p.y, p.y - p.x) * 0.70710678 / _Halftone.y;
            float distance = length(frac(grid) - 0.5);
            // A dot's area is the darkness (a mid-grey is half ink), the darkest merging into solid ink.
            float radius = sqrt(saturate(1 - luma) * 0.3183) * 1.06;
            float edge = 1.0 / _Halftone.y;
            float dotMask = 1 - smoothstep(radius - edge, radius + edge, distance);
            flat = lerp(saturate(flat * 1.25 + 0.06), flat * 0.12, dotMask);
        }
        return lerp(flat, float3(0.06, 0.05, 0.08), Outline(uv) * subject);
    }

    // Tones and dots are chosen on the picture as displayed, like a print: in linear light the mid-greys sit so low
    // that they would all fall to ink.
    float3 ComicDisplayed(float3 c, float2 uv)
    {
    #ifdef UNITY_COLORSPACE_GAMMA
        return Comic(saturate(c), uv);
    #else
        return GammaToLinearSpace(Comic(LinearToGammaSpace(saturate(c)), uv));
    #endif
    }

    float4 FragFinal(v2f i) : SV_Target
    {
        float2 uv = Distort(i.uv);
        float2 from = uv - 0.5;
        float shift = _Lens.y * 0.012;
        float4 c = tex2D(_MainTex, uv);
        if (shift > 0)
        {
            c.r = tex2D(_MainTex, uv - from * shift).r;
            c.b = tex2D(_MainTex, uv + from * shift).b;
        }
        c.rgb += tex2D(_BloomTex, uv).rgb * _Bloom.z;
        if (_Halftone.x > 0) c.rgb = ComicDisplayed(c.rgb, uv);
        if (_Lens.z != 0)
        {
            // Darker edges, or brighter ones below zero.
            float2 d = (i.uv - 0.5) * float2(Aspect(), 1);
            float shade = pow(saturate(1 - dot(d, d) * 1.8), 2.2);
            c.rgb = _Lens.z > 0 ? c.rgb * lerp(1, shade, _Lens.z) : lerp(c.rgb, 1, (1 - shade) * -_Lens.z);
        }
        if (_Lens.w > 0)
        {
            // About a pixel wide at 900 pixels high, bigger on larger captures, the same on every render.
            float2 cell = floor(i.uv * _Screen.xy / max(1, _Screen.y / 900));
            float n = Hash(cell + _Halftone.w) + Hash(cell * 1.7 + 3.1 + _Halftone.w) - 1;
            c.rgb += n * _Lens.w * 0.14 * (1 - Luma(saturate(c.rgb)) * 0.55);
        }
        c.rgb = saturate(c.rgb);
        c.a = 1;
        return c;
    }
    ENDCG

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragOcclusion
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragApplyOcclusion
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragDepthOfField
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragPrefilter
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragDown
            ENDCG }
        Pass { Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragUp
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment FragFinal
            #pragma target 3.0
            ENDCG }
    }
}
