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
    float4 effect;       // rain material / visible time / UI scale / unused
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

float3 rainHash(float2 cell)
{
    float3 p = frac(float3(cell.x, cell.y, cell.x) * float3(.1031, .1030, .0973));
    p += dot(p, p.yxz + 33.33);
    return frac((p.xxy + p.yzz) * p.zyx);
}

// The lens field is local to the pane, so dragging a window does not make its
// drops swim. Each jittered cell contains a small asymmetric, adhering drop;
// two sizes break up the grid without a full-screen particle simulation.
float4 adheringDrop(float2 p, float cellSize, float seed, float density)
{
    float2 cell = floor(p / cellSize);
    float3 n = rainHash(cell + seed);
    float2 center = (cell + .2 + n.xy * .6) * cellSize;
    float radius = cellSize * lerp(.045, .145, n.z * n.z);
    float2 q = (p - center) / float2(radius, radius * (1.05 + n.x * 1.2));
    q.x += q.y * (n.y - .5) * .4;
    q.x *= 1 + .20 * sin(q.y * 3 + n.x * 8);
    float r = length(q);
    float mask = (1 - smoothstep(.87, 1.12, r)) * step(n.y, density);
    return float4(q, r, mask);
}

float4 slidingDrop(float2 p, out float trail)
{
    float2 cellSize = float2(105, 230);
    float2 cell = floor(p / cellSize);
    float3 n = rainHash(cell + 91);
    // Staggered cycles leave most of the glass quiet. A head accelerates down
    // its short run, followed by a narrow, fading wet path instead of rain lines.
    float phase = frac(effect.y / 24 + n.z);
    float fall = smoothstep(.18, .96, phase);
    float x = (cell.x + .22 + n.x * .56) * cellSize.x;
    float y = (cell.y + .08 + .84 * fall) * cellSize.y;
    float bend = sin((p.y - cell.y * cellSize.y) * .037 + n.x * 9) * 1.8;
    float2 q = (p - float2(x + bend, y)) / float2(2.5 + n.y * 1.7, 5 + n.x * 3);
    float r = length(q);
    float alive = smoothstep(.02, .16, phase) * (1 - smoothstep(.94, 1, phase));
    float behind = y - p.y;
    trail = (1 - smoothstep(.3, 1.7, abs(p.x - x - bend))) *
        smoothstep(3, 15, behind) * (1 - smoothstep(20, 95, behind)) * alive;
    return float4(q, r, (1 - smoothstep(.88, 1.1, r)) * alive);
}

float3 rainGlass(float2 screen)
{
    float2 p = (screen - bounds.xy) / effect.z;
    float trail;
    float4 moving = slidingDrop(p, trail);
    float4 a = adheringDrop(p, 27, 0, .80);
    float4 b = adheringDrop(p + 83, 15, 37, .32);
    float4 drop = a.w > b.w ? a : b;
    drop.w *= 1 - trail * .8;
    drop = moving.w > drop.w ? moving : drop;
    float2 q = drop.xy;
    float r = drop.z;
    float2 normal = q / sqrt(max(.16, 1 - min(.96, dot(q, q))));
    float3 behind = readBackdrop(screen);
    // A cool, dark scrim keeps light UI text readable on a sunny game scene.
    // Refraction samples the actual blurred backdrop, rather than a stock photo.
    float3 fog = lerp(behind * .68, float3(.115, .18, .22), .55);
    float3 lens = lerp(readBackdrop(screen - normal * 9 * effect.z) * .82,
        float3(.15, .22, .26), .35);
    float rim = smoothstep(.55, .94, r);
    lens *= 1 - rim * .63;
    float glint = exp(-dot((q - float2(-.36, -.45)) * float2(4, 5),
                          (q - float2(-.36, -.45)) * float2(4, 5)));
    float lowerRim = exp(-abs(r - .77) * 15) * saturate(q.y * .7 - q.x * .4);
    lens += glint * .55 + lowerRim * .30;
    return lerp(fog + trail * .022, lens, drop.w);
}

float4 PS(float4 position : SV_POSITION) : SV_TARGET
{
    float2 p = position.xy - bounds.xy - bounds.zw * .5;
    float d = distanceToGlass(p);
    float coverage = saturate(.5 - d);
    clip(coverage - .001);
    if (effect.x > .5) return float4(rainGlass(position.xy), coverage * material.y);
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
