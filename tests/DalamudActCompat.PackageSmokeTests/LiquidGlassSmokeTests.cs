using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.UI;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

// Test-only device and readback. The production compositor never reads pixels
// back to the CPU and never installs or loads a plugin into the running game.
internal sealed unsafe class LiquidGlassSmokeTests : IDisposable
{
    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private ID3D11Texture2D* target;
    private ID3D11Texture2D* staging;
    private ID3D11RenderTargetView* view;
    private int width, height;
    internal LiquidGlassRenderer Renderer { get; }
    internal nint Device => (nint)device;

    public LiquidGlassSmokeTests()
    {
        ID3D11Device* d = null; ID3D11DeviceContext* c = null; D3D_FEATURE_LEVEL level;
        Check(D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, 0, 0, null, 0, 7, &d, &level, &c));
        device = d; context = c;
        Renderer = new(error => throw new Exception("Glass shader failed.", error));
        Renderer.BeginFrame(Device, true);
        Assert(Renderer.Ready, "Real D3D11 glass initialization failed.");
    }

    internal void Run()
    {
        const int w = 640, h = 400;
        var source = new byte[w * h * 3]; PaintBackdrop(source, w, h);
        var request = new LiquidGlassRenderer.Request { Min = new(70, 50), Max = new(570, 350), Radius = 30,
            Origin = Vector2.Zero, Pointer = new(80, 70), Clip = new(70, 50, 570, 350),
            Alpha = 1, Scale = 1, Scrim = .56f, Refraction = 24, Dispersion = 2, Blur = 3 };
        byte[] Render(LiquidGlassRenderer.Request value)
        {
            var pixels = (byte[])source.Clone();
            Process(pixels, w, h, () => Renderer.Render(value));
            return pixels;
        }
        var plain = Render(request with { Refraction = 0, Dispersion = 0, Blur = 0 });
        var blurred = Render(request with { Refraction = 0, Dispersion = 0 });
        var glass = Render(request);
        var dispersed = Render(request with { Dispersion = 0 });
        var previousStrength = Render(request with { Refraction = 12 });
        double Difference(byte[] a, byte[] b, int x1, int y1, int x2, int y2)
        {
            long sum = 0;
            for (var y = y1; y < y2; y++) for (var x = x1; x < x2; x++) for (var channel = 0; channel < 3; channel++)
                sum += Math.Abs(a[(y * w + x) * 3 + channel] - b[(y * w + x) * 3 + channel]);
            return sum / (double)((x2 - x1) * (y2 - y1) * 3);
        }
        Assert(Difference(plain, blurred, 150, 110, 480, 290) > 3, "Blur did not sample and soften the backdrop.");
        Assert(Difference(blurred, glass, 72, 105, 93, 290) > 1, "Refraction did not move real backdrop features at the bevel.");
        Assert(Difference(previousStrength, glass, 72, 105, 93, 290) > .5, "Stronger refraction did not change the edge lens.");
        Assert(Difference(dispersed, glass, 72, 105, 93, 290) > .1, "Chromatic dispersion did not separate edge samples.");
        Assert(Difference(blurred, glass, 160, 110, 480, 290) < .1, "Refraction distorted the flat center.");
        Assert(Difference(source, glass, 0, 0, 65, h) == 0 && Difference(source, glass, 70, 50, 74, 54) == 0,
            "Glass leaked past the scissor or rounded corners.");
        var clipped = Render(request with { Clip = new(70, 50, 300, 350) });
        Assert(Difference(source, clipped, 301, 0, w, h) == 0, "Parent clip was ignored by the shader.");
        var hovered = Render(request with { Hover = 1 });
        Assert(Difference(glass, hovered, 80, 60, 150, 95) > 0, "Pointer light did not react to interaction.");
        var transparent = Render(request with { Alpha = 0 });
        Assert(Difference(source, transparent, 0, 0, w, h) == 0, "Window fade alpha was ignored.");
        var movedViewport = Render(request with { Origin = new(300, 200), Min = request.Min + new Vector2(300, 200),
            Max = request.Max + new Vector2(300, 200), Pointer = request.Pointer + new Vector2(300, 200), Clip = request.Clip + new Vector4(300, 200, 300, 200) });
        Assert(glass.SequenceEqual(movedViewport), "Detached viewport origin shifted the sampled backdrop.");
        // Deliberately resize and move partly out of the viewport, then return to
        // the original source to catch stale capture pixels and allocation reuse.
        var tiny = new byte[180 * 140 * 3]; PaintBackdrop(tiny, 180, 140);
        Process(tiny, 180, 140, () => Renderer.Render(request with { Min = new(-20, -20), Max = new(170, 130), Clip = new(0, 0, 180, 140) }));
        var again = Render(request);
        Assert(glass.SequenceEqual(again), "Resize/reuse contaminated later frames.");
        Renderer.BeginFrame(Device, false);
        Assert(!Renderer.Ready, "Switching away from glass retained GPU resources.");
        Renderer.BeginFrame(Device, true);
        Assert(Renderer.Ready && Render(request).SequenceEqual(glass), "Re-enabling the effect changed its pixels.");
        HighFrequencyBlur();
        WhiteBody();
        Benchmark();
        Console.WriteLine("Liquid glass GPU: real shader compile/render, blur, bevel refraction, center preservation, rounded/parent clipping, hover, fade, resize and re-enable passed.");
    }

    private void WhiteBody()
    {
        const int w = 240, h = 180;
        var black = new byte[w * h * 3];
        var white = Enumerable.Repeat((byte)255, black.Length).ToArray();
        var request = new LiquidGlassRenderer.Request { Min = new(10), Max = new(w - 10, h - 10),
            Clip = new(10, 10, w - 10, h - 10), Radius = 14, Alpha = 1, Scale = 1, Blur = 3, Scrim = .56f };
        Process(black, w, h, () => Renderer.Render(request));
        Process(white, w, h, () => Renderer.Render(request));
        var center = (h / 2 * w + w / 2) * 3;
        Assert(black[center] >= 135 && black[center + 1] >= 135 && black[center + 2] >= 135,
            "The white glass body leaves dark text against a dark backdrop.");
        Assert(white[center] - black[center] > 85, "The white body became opaque and hid the backdrop.");
    }

    private void HighFrequencyBlur()
    {
        // A sparse kernel can soften large squares while preserving pixel-sized
        // stripes. Exercise multiple periods, directions and UI scales with no
        // tint, so a white overlay cannot conceal the sampling regression.
        const int w = 240, h = 180;
        foreach (var scale in new[] { 1f, 1.4f, 2f })
        foreach (var period in new[] { 3, 2, 4, 5, 6 })
        foreach (var vertical in new[] { false, true })
        {
            var pixels = new byte[w * h * 3];
            for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
            {
                var value = (byte)(((vertical ? y : x) % period) < period / 2 ? 0 : 255);
                for (var c = 0; c < 3; c++) pixels[(y * w + x) * 3 + c] = value;
            }
            var request = new LiquidGlassRenderer.Request { Min = new(10), Max = new(w - 10, h - 10),
                Clip = new(10, 10, w - 10, h - 10), Radius = 14, Alpha = 1, Scale = scale, Blur = 3 };
            Process(pixels, w, h, () => Renderer.Render(request));
            long contrast = 0; var count = 0;
            for (var y = 60; y < 120; y++) for (var x = 70; x < 170; x++)
            {
                var next = vertical ? ((y + 1) * w + x) * 3 : (y * w + x + 1) * 3;
                contrast += Math.Abs(pixels[(y * w + x) * 3] - pixels[next]); count++;
            }
            var residual = contrast / (double)count;
            Assert(residual < 3, $"Pixel grain survived blur: period={period}, vertical={vertical}, scale={scale}, adjacent contrast={residual:F2}/255.");
        }
        Console.WriteLine("Liquid glass pixel grain: 30 stripe-period/direction/scale cases passed without tint masking.");
    }

    internal void Install(NativeUiRasterizer raster)
    {
        DactTheme.GlassRenderer = Renderer;
        raster.PaintBackdrop = PaintBackdrop;
        raster.RenderCallback = (command, pixels, w, h) => Process(pixels, w, h, () =>
        {
            var callback = (delegate* unmanaged<ImDrawList*, ImDrawCmd*, void>)command.UserCallback;
            callback(null, command.Handle);
        });
    }

    private void Process(byte[] rgb, int w, int h, Action render)
    {
        EnsureTarget(w, h);
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { rgba[i * 4] = rgb[i * 3]; rgba[i * 4 + 1] = rgb[i * 3 + 1]; rgba[i * 4 + 2] = rgb[i * 3 + 2]; rgba[i * 4 + 3] = 255; }
        fixed (byte* bytes = rgba) context->UpdateSubresource((ID3D11Resource*)target, 0, null, bytes, (uint)w * 4, 0);
        var rt = view; context->OMSetRenderTargets(1, &rt, null);
        var viewport = new D3D11_VIEWPORT(0, 0, w, h); context->RSSetViewports(1, &viewport);
        // Non-default sentinels catch leaked pipeline state after each glass pass.
        var clip = new RECT(3, 4, w - 3, h - 4); context->RSSetScissorRects(1, &clip);
        context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
        render();
        D3D_PRIMITIVE_TOPOLOGY topology; context->IAGetPrimitiveTopology(&topology);
        uint count = 1; RECT after; context->RSGetScissorRects(&count, &after);
        Assert(topology == D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_LINELIST && after.left == 3 && after.top == 4 && after.right == w - 3,
            "Glass failed to restore pipeline/scissor state.");
        ID3D11RenderTargetView* restored = null; context->OMGetRenderTargets(1, &restored, null);
        var sameTarget = restored == view; if (restored != null) restored->Release();
        D3D11_VIEWPORT restoredViewport; count = 1; context->RSGetViewports(&count, &restoredViewport);
        Assert(sameTarget && count == 1 && restoredViewport.Width == w && restoredViewport.Height == h &&
            restoredViewport.TopLeftX == 0 && restoredViewport.TopLeftY == 0 && restoredViewport.MaxDepth == 1,
            "Offscreen blur leaked its render target or viewport into the following UI.");
        context->CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)target);
        D3D11_MAPPED_SUBRESOURCE mapped; Check(context->Map((ID3D11Resource*)staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped));
        try
        {
            for (var y = 0; y < h; y++)
            {
                var row = (byte*)mapped.pData + y * mapped.RowPitch;
                for (var x = 0; x < w; x++) for (var channel = 0; channel < 3; channel++) rgb[(y * w + x) * 3 + channel] = row[x * 4 + channel];
            }
        }
        finally { context->Unmap((ID3D11Resource*)staging, 0); }
    }

    private void Benchmark()
    {
        ID3D11Query* begin = null; ID3D11Query* end = null; ID3D11Query* disjoint = null;
        try
        {
            var desc = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP };
            Check(device->CreateQuery(&desc, &begin)); Check(device->CreateQuery(&desc, &end));
            desc.Query = D3D11_QUERY.D3D11_QUERY_TIMESTAMP_DISJOINT; Check(device->CreateQuery(&desc, &disjoint));
            const int w = 1120, h = 840, iterations = 40;
            var pixels = new byte[w * h * 3]; PaintBackdrop(pixels, w, h);
            var beginQuery = begin; var endQuery = end; var disjointQuery = disjoint;
            Process(pixels, w, h, () =>
            {
                var request = new LiquidGlassRenderer.Request { Min = new(30), Max = new(1090, 810), Radius = 14, Clip = new(30, 30, 1090, 810),
                    Alpha = 1, Scale = 1, Scrim = .56f, Refraction = 24, Dispersion = 2, Blur = 3 };
                Renderer.Render(request);
                context->Begin((ID3D11Asynchronous*)disjointQuery); context->End((ID3D11Asynchronous*)beginQuery);
                for (var i = 0; i < iterations; i++) Renderer.Render(request);
                context->End((ID3D11Asynchronous*)endQuery); context->End((ID3D11Asynchronous*)disjointQuery);
            });
            ulong startTime = 0, endTime = 0; D3D11_QUERY_DATA_TIMESTAMP_DISJOINT timing;
            Check(context->GetData((ID3D11Asynchronous*)begin, &startTime, sizeof(ulong), 0));
            Check(context->GetData((ID3D11Asynchronous*)end, &endTime, sizeof(ulong), 0));
            Check(context->GetData((ID3D11Asynchronous*)disjoint, &timing, (uint)sizeof(D3D11_QUERY_DATA_TIMESTAMP_DISJOINT), 0));
            Assert(!timing.Disjoint && timing.Frequency > 0 && endTime > startTime, "GPU timestamp measurement was unavailable.");
            var milliseconds = (endTime - startTime) * 1000d / timing.Frequency / iterations;
            Console.WriteLine($"Liquid glass GPU timing: 1060x780 pane, capture + two-pass continuous Gaussian blur + refractive bevel, {milliseconds:F3} ms average over {iterations} passes (local hardware; excludes CPU test readback).");
        }
        finally { if (begin != null) begin->Release(); if (end != null) end->Release(); if (disjoint != null) disjoint->Release(); }
    }

    // A deterministic, high-frequency backdrop makes blur/refraction measurable
    // and is clearly a test scene rather than an unverified in-game screenshot.
    internal static void PaintBackdrop(byte[] pixels, int w, int h)
    {
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            var stripe = ((x / 16 + y / 16) & 1) == 0 ? 23 : -23;
            var r = 55 + 90 * x / w + stripe;
            var g = 95 + 50 * y / h + stripe;
            var b = 145 - 45 * x / w + stripe;
            var i = (y * w + x) * 3;
            pixels[i] = (byte)r; pixels[i + 1] = (byte)g; pixels[i + 2] = (byte)b;
        }
    }

    private void EnsureTarget(int w, int h)
    {
        if (w == width && h == height && target != null) return;
        ReleaseTarget(); width = w; height = h;
        var desc = new D3D11_TEXTURE2D_DESC { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM, SampleDesc = new(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET };
        ID3D11Texture2D* value = null; Check(device->CreateTexture2D(&desc, null, &value)); target = value;
        ID3D11RenderTargetView* v = null; Check(device->CreateRenderTargetView((ID3D11Resource*)target, null, &v)); view = v;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        value = null; Check(device->CreateTexture2D(&desc, null, &value)); staging = value;
    }

    private void ReleaseTarget()
    {
        context->OMSetRenderTargets(0, null, null);
        if (view != null) { view->Release(); view = null; }
        if (target != null) { target->Release(); target = null; }
        if (staging != null) { staging->Release(); staging = null; }
    }
    public void Dispose()
    {
        DactTheme.GlassRenderer = null; Renderer.Dispose(); ReleaseTarget(); context->Release(); device->Release();
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Check(HRESULT result) { if (result.FAILED) Marshal.ThrowExceptionForHR(result); }
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern HRESULT D3D11CreateDevice(void* adapter, D3D_DRIVER_TYPE driver, nint software, uint flags,
        D3D_FEATURE_LEVEL* requestedLevels, uint levelCount, uint sdk, ID3D11Device** device, D3D_FEATURE_LEVEL* selectedLevel, ID3D11DeviceContext** context);
}
