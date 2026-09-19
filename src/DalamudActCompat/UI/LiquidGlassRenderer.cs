using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace DalamudActCompat.UI;

// Runs inside the normal ImGui renderer, after lower windows and before this
// pane's text. No Present detour, game-memory offsets or CPU screenshot readback.
internal sealed unsafe class LiquidGlassRenderer : IDisposable
{
    private const int MaxSurfaces = 512;
    private static readonly ImDrawCallback Callback = DrawCallback;
    private readonly object gate = new();
    private readonly Action<Exception> reportError;
    private readonly Request* requests = (Request*)NativeMemory.AllocZeroed((nuint)(MaxSurfaces * sizeof(Request)));
    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* pixelShader;
    private ID3D11PixelShader* blurShader;
    private ID3D11Buffer* constants;
    private ID3D11SamplerState* sampler;
    private ID3D11RasterizerState* rasterizer;
    private ID3D11BlendState* blend;
    private ID3D11DepthStencilState* depth;
    private ID3D11Texture2D* snapshot;
    private ID3D11ShaderResourceView* snapshotView;
    private ID3D11Texture2D* blurTemp;
    private ID3D11ShaderResourceView* blurTempView;
    private ID3D11RenderTargetView* blurTempTarget;
    private ID3D11Texture2D* blurred;
    private ID3D11ShaderResourceView* blurredView;
    private ID3D11RenderTargetView* blurredTarget;
    private uint textureWidth, textureHeight;
    private DXGI_FORMAT textureFormat;
    private nint deviceIdentity;
    private int requestCount;
    private bool disposed, failed;

    internal bool Ready => device != null && !failed && !disposed;
    internal int RenderedSurfaces { get; private set; }
    internal long CopiedPixels { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Request
    {
        public Vector2 Min, Max, Origin, Pointer;
        public Vector4 Clip;
        public float Radius, Scrim, Alpha, Hover, Scale, Refraction, Dispersion, Blur;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public Vector4 Bounds, Capture, Optics, Material, PointerClip, BlurPass;
        public fixed float BlurTaps[25 * 4];
    }

    public LiquidGlassRenderer(Action<Exception> reportError) => this.reportError = reportError;

    public void BeginFrame(nint handle, bool enabled)
    {
        lock (gate)
        {
            if (disposed) return;
            // Draw callbacks finish before the next UI build; storage is kept alive
            // even when ImGui culls a window and never executes its callbacks.
            requestCount = 0;
            RenderedSurfaces = 0;
            CopiedPixels = 0;
            if (!enabled) { ReleaseDevice(); failed = false; return; }
            if (handle == 0 || failed && deviceIdentity == handle) return;
            if (Ready && device->GetDeviceRemovedReason().FAILED)
            {
                Fail(new InvalidOperationException("The Direct3D device was removed."));
                return;
            }
            if (deviceIdentity == handle && Ready) return;
            ReleaseDevice(); failed = false; deviceIdentity = handle;
            try { Initialize(handle); }
            catch (Exception error) { Fail(error); }
        }
    }

    internal bool Enqueue(ImDrawListPtr list, Vector2 min, Vector2 max, float radius, float scrim, bool interactive)
    {
        lock (gate)
        {
            if (!Ready || requestCount == MaxSurfaces) return false;
            var viewport = ImGui.GetWindowViewport();
            var clipMin = Vector2.Max(list.GetClipRectMin(), min);
            var clipMax = Vector2.Min(list.GetClipRectMax(), max);
            if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y) return true;
            var request = requests + requestCount++;
            *request = new Request
            {
                Min = min, Max = max, Origin = viewport.Pos, Pointer = ImGui.GetIO().MousePos,
                Clip = new(clipMin.X, clipMin.Y, clipMax.X, clipMax.Y), Radius = radius,
                Scrim = scrim, Alpha = ImGui.GetStyle().Alpha, Hover = interactive ? 1 : 0,
                Scale = Math.Max(.75f, ImGui.GetFontSize() / 17f),
                Refraction = 24, Dispersion = 2, Blur = 3,
            };
            list.AddCallback(Callback, request);
            return true;
        }
    }

    private static void DrawCallback(ImDrawList* list, ImDrawCmd* command)
    {
        // Dispose clears the owner first. Never dereference command storage after
        // unload, and never let a managed exception cross the native renderer.
        var owner = DactTheme.GlassRenderer;
        if (owner is null) return;
        lock (owner.gate)
        {
            if (!owner.Ready) return;
            try { owner.Render(*(Request*)command->UserCallbackData); }
            catch (Exception error) { owner.Fail(error); }
        }
    }

    internal void Render(Request request)
    {
        if (!Ready) return;
        ID3D11RenderTargetView* target = null;
        ID3D11Resource* resource = null;
        ID3D11Texture2D* targetTexture = null;
        try
        {
            context->OMGetRenderTargets(1, &target, null);
            if (target == null) return;
            target->GetResource(&resource);
            var iid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            if (resource->QueryInterface(&iid, (void**)&targetTexture).FAILED) return;
            D3D11_TEXTURE2D_DESC description;
            targetTexture->GetDesc(&description);
            // Dalamud renders UI into a single-sample 2D target. Unknown surfaces
            // use the ordinary pane instead of an invalid copy/resolve operation.
            if (description.SampleDesc.Count != 1 || description.ArraySize != 1)
                throw new NotSupportedException("Liquid glass requires a single-sample 2D UI render target.");
            var min = request.Min - request.Origin;
            var max = request.Max - request.Origin;
            var margin = 48 * request.Scale;
            var left = (uint)Math.Clamp(MathF.Floor(min.X - margin), 0, description.Width);
            var top = (uint)Math.Clamp(MathF.Floor(min.Y - margin), 0, description.Height);
            var right = (uint)Math.Clamp(MathF.Ceiling(max.X + margin), 0, description.Width);
            var bottom = (uint)Math.Clamp(MathF.Ceiling(max.Y + margin), 0, description.Height);
            if (right <= left || bottom <= top) return;
            EnsureSnapshot(right - left, bottom - top, description.Format);
            using var saved = new PipelineState(context);
            ID3D11ShaderResourceView* empty = null;
            context->PSSetShaderResources(0, 1, &empty);
            var box = new D3D11_BOX((int)left, (int)top, 0, (int)right, (int)bottom, 1);
            context->CopySubresourceRegion((ID3D11Resource*)snapshot, 0, 0, 0, 0, resource, 0, &box);
            var pointer = request.Pointer - request.Origin;
            var parameters = new Parameters
            {
                Bounds = new(min.X, min.Y, max.X - min.X, max.Y - min.Y),
                Capture = new(left, top, textureWidth, textureHeight),
                Optics = new(request.Radius, request.Refraction * request.Scale, request.Dispersion * request.Scale, request.Blur * request.Scale),
                Material = new(request.Scrim, request.Alpha, request.Hover, 0),
                PointerClip = new(pointer.X, pointer.Y, right - left, bottom - top),
            };
            context->IASetInputLayout(null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vertexShader, null, 0);
            var buffer = constants; var srv = snapshotView; var sampling = sampler;
            context->PSSetConstantBuffers(0, 1, &buffer);
            context->PSSetShaderResources(0, 1, &srv);
            context->PSSetSamplers(0, 1, &sampling);
            context->RSSetState(rasterizer);
            context->OMSetDepthStencilState(depth, 0);
            if (request.Blur > .01f)
            {
                SetBlurKernel(ref parameters, request.Blur * request.Scale);
                var viewport = new D3D11_VIEWPORT(0, 0, right - left, bottom - top);
                context->RSSetViewports(1, &viewport);
                var area = new RECT(0, 0, (int)(right - left), (int)(bottom - top));
                context->RSSetScissorRects(1, &area);
                context->OMSetBlendState(null, null, uint.MaxValue);
                context->PSSetShader(blurShader, null, 0);
                var output = blurTempTarget;
                context->OMSetRenderTargets(1, &output, null);
                parameters.BlurPass.X = 1;
                context->UpdateSubresource((ID3D11Resource*)constants, 0, null, &parameters, 0, 0);
                context->Draw(3, 0);
                // Switch the output before binding the horizontal result as input;
                // D3D11 must never see the same texture as an RTV and an SRV.
                output = blurredTarget;
                context->OMSetRenderTargets(1, &output, null);
                srv = blurTempView; context->PSSetShaderResources(0, 1, &srv);
                parameters.BlurPass.X = 0; parameters.BlurPass.Y = 1;
                context->UpdateSubresource((ID3D11Resource*)constants, 0, null, &parameters, 0, 0);
                context->Draw(3, 0);
                saved.RestoreOutput();
                srv = blurredView; context->PSSetShaderResources(0, 1, &srv);
            }
            context->PSSetShader(pixelShader, null, 0);
            context->UpdateSubresource((ID3D11Resource*)constants, 0, null, &parameters, 0, 0);
            var clip = request.Clip - new Vector4(request.Origin.X, request.Origin.Y, request.Origin.X, request.Origin.Y);
            var rectangle = new RECT((int)Math.Max(0, clip.X), (int)Math.Max(0, clip.Y),
                (int)Math.Min(description.Width, clip.Z), (int)Math.Min(description.Height, clip.W));
            context->RSSetScissorRects(1, &rectangle);
            context->OMSetBlendState(blend, null, uint.MaxValue);
            context->OMSetDepthStencilState(depth, 0);
            context->Draw(3, 0);
            RenderedSurfaces++;
            CopiedPixels += (long)(right - left) * (bottom - top);
        }
        finally
        {
            Release((IUnknown*)targetTexture); Release((IUnknown*)resource); Release((IUnknown*)target);
        }
    }

    private static void SetBlurKernel(ref Parameters parameters, float sigma)
    {
        // Radius and weights are calculated in physical pixels. Scaling tap
        // spacing would reintroduce skipped texels at larger UI font scales.
        sigma = Math.Clamp(sigma, .1f, 8);
        var radius = Math.Min(24, (int)MathF.Ceiling(sigma * 3 / 2) * 2);
        float Weight(int offset) => MathF.Exp(-offset * offset / (2 * sigma * sigma));
        var total = 1f;
        for (var i = 1; i <= radius; i++) total += 2 * Weight(i);
        fixed (float* taps = parameters.BlurTaps)
        {
            taps[0] = 0; taps[1] = 1 / total;
            var count = 1;
            for (var i = 1; i <= radius; i += 2)
            {
                var a = Weight(i); var b = Weight(i + 1); var weight = a + b;
                var offset = i + b / weight;
                taps[count * 4] = offset; taps[count++ * 4 + 1] = weight / total;
                taps[count * 4] = -offset; taps[count++ * 4 + 1] = weight / total;
            }
            parameters.BlurPass = new(0, 0, count, 0);
        }
    }

    private void Initialize(nint handle)
    {
        ID3D11Device* resolved = null;
        var iid = new Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140");
        Check(((IUnknown*)handle)->QueryInterface(&iid, (void**)&resolved)); device = resolved;
        ID3D11DeviceContext* immediate = null; device->GetImmediateContext(&immediate); context = immediate;
        using var source = typeof(LiquidGlassRenderer).Assembly.GetManifestResourceStream("DACT.LiquidGlass.hlsl")!;
        using var reader = new StreamReader(source);
        var shader = reader.ReadToEnd();
        var vs = Compile(shader, "VS", "vs_5_0");
        try { ID3D11VertexShader* value = null; Check(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), null, &value)); vertexShader = value; }
        finally { vs->Release(); }
        var ps = Compile(shader, "PS", "ps_5_0");
        try { ID3D11PixelShader* value = null; Check(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), null, &value)); pixelShader = value; }
        finally { ps->Release(); }
        var blur = Compile(shader, "BlurPS", "ps_5_0");
        try { ID3D11PixelShader* value = null; Check(device->CreatePixelShader(blur->GetBufferPointer(), blur->GetBufferSize(), null, &value)); blurShader = value; }
        finally { blur->Release(); }
        var bufferDesc = new D3D11_BUFFER_DESC { ByteWidth = (uint)sizeof(Parameters), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER };
        ID3D11Buffer* cb = null; Check(device->CreateBuffer(&bufferDesc, null, &cb)); constants = cb;
        var samplerDesc = new D3D11_SAMPLER_DESC { Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP, AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP, MaxLOD = float.MaxValue, ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER };
        ID3D11SamplerState* ss = null; Check(device->CreateSamplerState(&samplerDesc, &ss)); sampler = ss;
        var rasterDesc = new D3D11_RASTERIZER_DESC { FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID, CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE, ScissorEnable = true, DepthClipEnable = true };
        ID3D11RasterizerState* rs = null; Check(device->CreateRasterizerState(&rasterDesc, &rs)); rasterizer = rs;
        var blendDesc = new D3D11_BLEND_DESC();
        blendDesc.RenderTarget[0] = new D3D11_RENDER_TARGET_BLEND_DESC { BlendEnable = true,
            SrcBlend = D3D11_BLEND.D3D11_BLEND_SRC_ALPHA, DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD,
            SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE, DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA,
            BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD, RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL };
        ID3D11BlendState* bs = null; Check(device->CreateBlendState(&blendDesc, &bs)); blend = bs;
        var depthDesc = new D3D11_DEPTH_STENCIL_DESC { DepthEnable = false, DepthWriteMask = D3D11_DEPTH_WRITE_MASK.D3D11_DEPTH_WRITE_MASK_ZERO,
            DepthFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS, StencilEnable = false };
        ID3D11DepthStencilState* ds = null; Check(device->CreateDepthStencilState(&depthDesc, &ds)); depth = ds;
    }

    private void EnsureSnapshot(uint width, uint height, DXGI_FORMAT format)
    {
        if (snapshot != null && textureWidth >= width && textureHeight >= height && textureFormat == format) return;
        // Grow both dimensions monotonically for this active device, so alternating
        // wide/short and narrow/tall panes cannot churn allocations every frame.
        if (textureFormat == format) { width = Math.Max(width, textureWidth); height = Math.Max(height, textureHeight); }
        ReleaseSnapshot();
        textureWidth = (width + 127) / 128 * 128; textureHeight = (height + 127) / 128 * 128; textureFormat = format;
        var desc = new D3D11_TEXTURE2D_DESC { Width = textureWidth, Height = textureHeight, MipLevels = 1, ArraySize = 1,
            Format = format, SampleDesc = new(1, 0), Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT, BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE };
        ID3D11Texture2D* texture = null; Check(device->CreateTexture2D(&desc, null, &texture)); snapshot = texture;
        ID3D11ShaderResourceView* view = null; Check(device->CreateShaderResourceView((ID3D11Resource*)snapshot, null, &view)); snapshotView = view;
        desc.BindFlags |= (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET;
        texture = null; Check(device->CreateTexture2D(&desc, null, &texture)); blurTemp = texture;
        view = null; Check(device->CreateShaderResourceView((ID3D11Resource*)blurTemp, null, &view)); blurTempView = view;
        ID3D11RenderTargetView* target = null; Check(device->CreateRenderTargetView((ID3D11Resource*)blurTemp, null, &target)); blurTempTarget = target;
        texture = null; Check(device->CreateTexture2D(&desc, null, &texture)); blurred = texture;
        view = null; Check(device->CreateShaderResourceView((ID3D11Resource*)blurred, null, &view)); blurredView = view;
        target = null; Check(device->CreateRenderTargetView((ID3D11Resource*)blurred, null, &target)); blurredTarget = target;
    }

    private static ID3DBlob* Compile(string source, string entry, string profile)
    {
        var code = Encoding.UTF8.GetBytes(source); var name = Encoding.ASCII.GetBytes(entry + '\0'); var target = Encoding.ASCII.GetBytes(profile + '\0');
        ID3DBlob* result = null; ID3DBlob* errors = null;
        fixed (byte* s = code, e = name, p = target)
        {
            var hr = D3DCompile(s, (nuint)code.Length, null, null, null, e, p, 1u << 15, 0, &result, &errors);
            try
            {
                if (hr < 0)
                {
                    Release((IUnknown*)result);
                    throw new InvalidOperationException(errors == null ? $"Glass shader compilation failed: {hr:X8}" : Marshal.PtrToStringUTF8((nint)errors->GetBufferPointer()));
                }
            }
            finally { Release((IUnknown*)errors); }
        }
        return result;
    }

    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)]
    private static extern int D3DCompile(void* source, nuint length, byte* sourceName, void* defines, void* include,
        byte* entry, byte* profile, uint flags, uint effectFlags, ID3DBlob** code, ID3DBlob** errors);

    private static void Check(HRESULT hr) { if (hr.FAILED) Marshal.ThrowExceptionForHR(hr); }
    private static void Release(IUnknown* value) { if (value != null) value->Release(); }
    private void Fail(Exception error) { failed = true; ReleaseDevice(); try { reportError(error); } catch { /* Logging must not escape a native callback. */ } }
    private void ReleaseSnapshot()
    {
        Release((IUnknown*)snapshotView); snapshotView = null; Release((IUnknown*)snapshot); snapshot = null;
        Release((IUnknown*)blurTempTarget); blurTempTarget = null; Release((IUnknown*)blurTempView); blurTempView = null; Release((IUnknown*)blurTemp); blurTemp = null;
        Release((IUnknown*)blurredTarget); blurredTarget = null; Release((IUnknown*)blurredView); blurredView = null; Release((IUnknown*)blurred); blurred = null;
        textureWidth = textureHeight = 0;
    }
    private void ReleaseDevice()
    {
        ReleaseSnapshot();
        Release((IUnknown*)vertexShader); vertexShader = null; Release((IUnknown*)pixelShader); pixelShader = null;
        Release((IUnknown*)blurShader); blurShader = null;
        Release((IUnknown*)constants); constants = null; Release((IUnknown*)sampler); sampler = null;
        Release((IUnknown*)rasterizer); rasterizer = null; Release((IUnknown*)blend); blend = null;
        Release((IUnknown*)depth); depth = null; Release((IUnknown*)context); context = null; Release((IUnknown*)device); device = null;
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; ReleaseDevice(); NativeMemory.Free(requests); }
    }

    // Blur uses offscreen targets and its own viewport; restore both before the
    // pane composite and all touched state before returning to ImGui.
    private struct PipelineState : IDisposable
    {
        private readonly ID3D11DeviceContext* ctx;
        private ID3D11InputLayout* layout;
        private D3D_PRIMITIVE_TOPOLOGY topology;
        private ID3D11VertexShader* vs;
        private ID3D11PixelShader* ps;
        private ID3D11Buffer* cb;
        private ID3D11ShaderResourceView* srv;
        private ID3D11SamplerState* ss;
        private ID3D11RasterizerState* rs;
        private ID3D11BlendState* bs;
        private ID3D11DepthStencilState* ds;
        private Vector4 factor;
        private uint mask, stencil, rectCount;
        private fixed int rects[64];
        // The plugin and Dalamud renderer run in a 64-bit process.
        private fixed ulong targets[8];
        private ID3D11DepthStencilView* depthView;
        private fixed float viewports[16 * 6];
        private uint viewportCount;

        public PipelineState(ID3D11DeviceContext* ctx)
        {
            this = default;
            this.ctx = ctx;
            ID3D11InputLayout* l = null; ctx->IAGetInputLayout(&l); layout = l;
            D3D_PRIMITIVE_TOPOLOGY t; ctx->IAGetPrimitiveTopology(&t); topology = t;
            ID3D11VertexShader* v = null; ctx->VSGetShader(&v, null, null); vs = v;
            ID3D11PixelShader* p = null; ctx->PSGetShader(&p, null, null); ps = p;
            ID3D11Buffer* b = null; ctx->PSGetConstantBuffers(0, 1, &b); cb = b;
            ID3D11ShaderResourceView* s = null; ctx->PSGetShaderResources(0, 1, &s); srv = s;
            ID3D11SamplerState* sampling = null; ctx->PSGetSamplers(0, 1, &sampling); ss = sampling;
            ID3D11RasterizerState* raster = null; ctx->RSGetState(&raster); rs = raster;
            ID3D11BlendState* blending = null; Vector4 f; uint m; ctx->OMGetBlendState(&blending, (float*)&f, &m); bs = blending; factor = f; mask = m;
            ID3D11DepthStencilState* d = null; uint reference; ctx->OMGetDepthStencilState(&d, &reference); ds = d; stencil = reference;
            uint count = 16; fixed (int* r = rects) ctx->RSGetScissorRects(&count, (RECT*)r); rectCount = count;
            ID3D11DepthStencilView* dv = null;
            fixed (ulong* rt = targets) ctx->OMGetRenderTargets(8, (ID3D11RenderTargetView**)rt, &dv);
            depthView = dv;
            count = 16; fixed (float* vp = viewports) ctx->RSGetViewports(&count, (D3D11_VIEWPORT*)vp); viewportCount = count;
        }
        public void RestoreOutput()
        {
            fixed (ulong* rt = targets) ctx->OMSetRenderTargets(8, (ID3D11RenderTargetView**)rt, depthView);
            fixed (float* vp = viewports) ctx->RSSetViewports(viewportCount, (D3D11_VIEWPORT*)vp);
        }
        public void Dispose()
        {
            RestoreOutput();
            ctx->IASetInputLayout(layout); ctx->IASetPrimitiveTopology(topology);
            ctx->VSSetShader(vs, null, 0); ctx->PSSetShader(ps, null, 0);
            var b = cb; var s = srv; var sampling = ss;
            ctx->PSSetConstantBuffers(0, 1, &b); ctx->PSSetShaderResources(0, 1, &s); ctx->PSSetSamplers(0, 1, &sampling);
            ctx->RSSetState(rs); fixed (int* r = rects) ctx->RSSetScissorRects(rectCount, (RECT*)r);
            var f = factor; ctx->OMSetBlendState(bs, (float*)&f, mask); ctx->OMSetDepthStencilState(ds, stencil);
            Release((IUnknown*)layout); Release((IUnknown*)vs); Release((IUnknown*)ps); Release((IUnknown*)cb);
            Release((IUnknown*)srv); Release((IUnknown*)ss); Release((IUnknown*)rs); Release((IUnknown*)bs); Release((IUnknown*)ds);
            fixed (ulong* rt = targets) for (var i = 0; i < 8; i++) Release((IUnknown*)rt[i]);
            Release((IUnknown*)depthView);
        }
    }
}
