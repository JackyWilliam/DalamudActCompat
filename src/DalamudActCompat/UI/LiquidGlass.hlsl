Texture2D backdrop : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Glass : register(b0)
{
    float4 bounds;       // panel xy / size in render-target pixels
    float4 capture;      // copied-region origin / allocated texture size
    float4 optics;       // radius / refraction px / dispersion px / blur sigma
    float4 material;     // scrim / alpha / hover / time
    float4 pointerClip;  // pointer xy / copied-region size
    float4 blurPass;     // pixel direction xy / tap count / unused
    float4 blurTaps[25]; // paired adjacent-texel offset / normalized weight
};

float4 VS(uint id : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

float distanceToGlass(float2 p)
{
    float2 q = abs(p) - (bounds.zw * .5 - optics.x);
    return length(max(q, 0)) + min(max(q.x, q.y), 0) - optics.x;
}

float3 readCapture(float2 p)
{
    // Clamp to the copied rectangle, not the larger reusable allocation. This
    // prevents stale pixels appearing at a viewport edge or after shrinking.
    float2 uv = clamp(p, .5, pointerClip.zw - .5) / capture.zw;
    return backdrop.SampleLevel(linearClamp, uv, 0).rgb;
}

float4 BlurPS(float4 position : SV_POSITION) : SV_TARGET
{
    // Filter every source texel before refraction. Pairing adjacent taps via
    // bilinear sampling saves reads without the holes of a spaced 5x5 kernel.
    float3 result = 0;
    [loop] for (int i = 0; i < (int)blurPass.z; i++)
        result += readCapture(position.xy + blurPass.xy * blurTaps[i].x) * blurTaps[i].y;
    return float4(result, 1);
}

float3 readBackdrop(float2 p) { return readCapture(p - capture.xy); }

float4 PS(float4 position : SV_POSITION) : SV_TARGET
{
    float2 p = position.xy - bounds.xy - bounds.zw * .5;
    float d = distanceToGlass(p);
    float coverage = saturate(.5 - d);
    clip(coverage - .001);
    float2 normal = normalize(float2(distanceToGlass(p + float2(.5, 0)) - distanceToGlass(p - float2(.5, 0)),
                                    distanceToGlass(p + float2(0, .5)) - distanceToGlass(p - float2(0, .5))) + 1e-6);
    // Curvature is concentrated in the bevel. The flat center stays readable;
    // the edge bends real background features inward like a rounded glass lens.
    float bevel = pow(saturate(1 + d / max(12, optics.x * 1.5)), 2);
    float2 displaced = position.xy - normal * optics.y * bevel;
    float3 color = readBackdrop(displaced);
    if (bevel > .015)
    {
        float2 dispersion = normal * optics.z * bevel;
        color.r = readBackdrop(displaced + dispersion).r;
        color.b = readBackdrop(displaced - dispersion).b;
    }
    // A milky white body gives dark text stable contrast while retaining the
    // game's colors and lens distortion underneath, including on dark scenes.
    color = lerp(color, float3(.975, .985, 1), material.x);
    float rim = exp(-abs(d + 1.2) * 1.25);
    float directional = pow(saturate(dot(normal, normalize(float2(-.6, -.8)))), 3);
    float pointerLight = material.z * exp(-length(position.xy - pointerClip.xy) / 95);
    float sheen = (.022 + pointerLight * .035) * saturate(.5 - p.y / max(1, bounds.w));
    color += sheen + rim * (.08 + .36 * directional + pointerLight * .25);
    // A second inner contour gives the curved edge depth without a flat white outline.
    color *= 1 - exp(-abs(d + 3.8)) * .10;
    return float4(color, coverage * material.y);
}
