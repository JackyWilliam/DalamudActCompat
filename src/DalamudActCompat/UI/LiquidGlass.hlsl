Texture2D backdrop : register(t0);
SamplerState linearClamp : register(s0);
cbuffer Glass : register(b0)
{
    float4 bounds;       // panel xy / size in render-target pixels
    float4 capture;      // copied-region origin / allocated texture size
    float4 optics;       // radius / refraction px / dispersion px / blur spacing
    float4 material;     // scrim / alpha / hover / time
    float4 pointerClip;  // pointer xy / copied-region size
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

float3 readBackdrop(float2 p)
{
    // Clamp to the copied rectangle, not the larger reusable allocation. This
    // prevents stale pixels appearing at a viewport edge or after shrinking.
    float2 uv = clamp(p - capture.xy, .5, pointerClip.zw - .5) / capture.zw;
    return backdrop.SampleLevel(linearClamp, uv, 0).rgb;
}

float3 blurred(float2 p)
{
    // A bounded 5x5 Gaussian samples the actual current render target. Linear
    // sampling also suppresses shimmer as the pane moves by fractional pixels.
    static const float weights[5] = { .06136, .24477, .38774, .24477, .06136 };
    float3 result = 0;
    [unroll] for (int y = -2; y <= 2; y++)
        [unroll] for (int x = -2; x <= 2; x++)
            result += readBackdrop(p + float2(x, y) * optics.w) * weights[x + 2] * weights[y + 2];
    return result;
}

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
    float3 color = blurred(displaced);
    if (bevel > .015)
    {
        float2 dispersion = normal * optics.z * bevel;
        color.r = blurred(displaced + dispersion).r;
        color.b = blurred(displaced - dispersion).b;
    }
    color = lerp(color, float3(.035, .045, .055), material.x);
    float rim = exp(-abs(d + 1.2) * 1.25);
    float directional = pow(saturate(dot(normal, normalize(float2(-.6, -.8)))), 3);
    float pointerLight = material.z * exp(-length(position.xy - pointerClip.xy) / 95);
    float sheen = (.022 + pointerLight * .035) * saturate(.5 - p.y / max(1, bounds.w));
    color += sheen + rim * (.08 + .36 * directional + pointerLight * .25);
    // A second inner contour gives the curved edge depth without a flat white outline.
    color *= 1 - exp(-abs(d + 3.8)) * .10;
    return float4(color, coverage * material.y);
}
