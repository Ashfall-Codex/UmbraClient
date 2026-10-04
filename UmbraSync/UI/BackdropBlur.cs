using System.Numerics;
using System.Text;
using Microsoft.Extensions.Logging;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace UmbraSync.UI;


public sealed unsafe class BackdropBlur : IDisposable
{
    private const int Levels = 5;
    public float Strength { get; set; } = 3f;
    private const float GrainAmount = 0.035f;

    private readonly IUiBuilder uiBuilder;
    private readonly ITextureProvider textureProvider;
    private readonly ILogger<BackdropBlur> logger;
    private ComPtr<ID3D11Device> device;

    private Task<IDalamudTextureWrap>? captureTask;
    private IDalamudTextureWrap? capture;

    private ComPtr<ID3D11VertexShader> vertexShader;
    private ComPtr<ID3D11PixelShader> downShader;
    private ComPtr<ID3D11PixelShader> upShader;
    private ComPtr<ID3D11SamplerState> sampler;
    private ComPtr<ID3D11Buffer> constants;

    private readonly ComPtr<ID3D11Texture2D>[] textures = new ComPtr<ID3D11Texture2D>[Levels];
    private readonly ComPtr<ID3D11RenderTargetView>[] targets = new ComPtr<ID3D11RenderTargetView>[Levels];
    private readonly ComPtr<ID3D11ShaderResourceView>[] views = new ComPtr<ID3D11ShaderResourceView>[Levels];
    private readonly uint[] widths = new uint[Levels];
    private readonly uint[] heights = new uint[Levels];

    private bool pipelineReady;
    private bool failed;
    private long renderedFrame = -1;

    private const int IdleFramesBeforeRelease = 120;

    public bool Ready { get; private set; }

    public BackdropBlur(ILogger<BackdropBlur> logger, IUiBuilder uiBuilder, ITextureProvider textureProvider)
    {
        this.logger = logger;
        this.uiBuilder = uiBuilder;
        this.textureProvider = textureProvider;
    }

    private bool EnsureDevice()
    {
        if (device.Get() != null) return true;

        var handle = uiBuilder.DeviceHandle;
        if (handle == 0) return false;

        var iid = IID.IID_ID3D11Device;
        Check(((IUnknown*)handle)->QueryInterface(&iid, (void**)device.GetAddressOf()), "QueryInterface(ID3D11Device)");
        return true;
    }

    public void EnsureRendered()
    {
        var frame = ImGui.GetFrameCount();
        if (frame == renderedFrame) return;
        renderedFrame = frame;
        Ready = false;

        if (failed) return;

        try
        {
            if (!EnsureDevice() || !EnsureCapture()) return;
            if (!pipelineReady) CreatePipeline();
            Render();
            Ready = true;
        }
        catch (Exception ex)
        {

            failed = true;
            logger.LogError(ex, "Flou d'arrière-plan désactivé après une erreur");
        }
    }

    public void DrawBehind(Vector2 min, Vector2 max, float rounding, float opacity = 1f)
    {
        if (!Ready) return;

        var viewport = ImGui.GetMainViewport();
        if (ImGui.GetWindowViewport().ID != viewport.ID) return;
        var size = viewport.Size;
        if (size.X <= 0 || size.Y <= 0) return;

        var uv0 = (min - viewport.Pos) / size;
        var uv1 = (max - viewport.Pos) / size;
        var texture = new ImTextureID((nint)views[0].Get());
        ImGui.GetBackgroundDrawList(viewport).AddImageRounded(texture, min, max, uv0, uv1,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, Math.Clamp(opacity, 0f, 1f))), rounding);
    }


    public void ReleaseIfIdle()
    {
        if (capture == null && captureTask == null) return;
        if (ImGui.GetFrameCount() - renderedFrame > IdleFramesBeforeRelease)
            Release();
    }

    public void Release()
    {
        capture?.Dispose();
        capture = null;
        captureTask?.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
        }, TaskScheduler.Default);
        captureTask = null;
        Ready = false;
    }

    private bool EnsureCapture()
    {
        if (capture != null) return true;

        if (captureTask == null)
        {
            captureTask = textureProvider.CreateFromImGuiViewportAsync(new ImGuiViewportTextureArgs
            {
                ViewportId = ImGui.GetMainViewport().ID,
                AutoUpdate = true,
                TakeBeforeImGuiRender = true,
                KeepTransparency = false,
            }, "UmbraSync.BackdropBlur");
            return false;
        }

        if (!captureTask.IsCompleted) return false;
        if (captureTask.IsFaulted)
            throw captureTask.Exception?.GetBaseException() ?? new InvalidOperationException("Capture impossible.");

        capture = captureTask.Result;
        captureTask = null;
        return true;
    }

    private void Render()
    {
        var source = (ID3D11ShaderResourceView*)capture!.Handle.Handle;
        if (source == null) throw new InvalidOperationException("Capture sans texture.");

        EnsureTargets((uint)capture.Width, (uint)capture.Height);

        ID3D11DeviceContext* context;
        device.Get()->GetImmediateContext(&context);
        try
        {
            using var saved = new SavedState(context);

            context->IASetInputLayout(null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vertexShader.Get(), null, 0);
            context->OMSetBlendState(null, null, 0xFFFFFFFF);
            context->OMSetDepthStencilState(null, 0);
            context->RSSetState(null);
            var samplerPtr = sampler.Get();
            context->PSSetSamplers(0, 1, &samplerPtr);
            var buffer = constants.Get();
            context->PSSetConstantBuffers(0, 1, &buffer);

            var strength = Math.Clamp(Strength, 1f, Levels - 1.001f);
            var levels = (int)MathF.Floor(strength) + 1;
            var spread = 1f + (strength - MathF.Floor(strength));
            var input = source;
            uint inputWidth = (uint)capture.Width, inputHeight = (uint)capture.Height;
            for (var i = 0; i < levels; i++)
            {
                Pass(context, downShader.Get(), input, inputWidth, inputHeight, i, spread, 0f);
                input = views[i].Get();
                inputWidth = widths[i];
                inputHeight = heights[i];
            }

            for (var i = levels - 1; i > 0; i--)
                Pass(context, upShader.Get(), views[i].Get(), widths[i], heights[i], i - 1, spread,
                    i == 1 ? GrainAmount : 0f);
        }
        finally
        {
            context->Release();
        }
    }

    private void Pass(ID3D11DeviceContext* context, ID3D11PixelShader* shader,
        ID3D11ShaderResourceView* input, uint inputWidth, uint inputHeight, int target, float spread, float grain)
    {
        ID3D11ShaderResourceView* none = null;
        context->PSSetShaderResources(0, 1, &none);

        var rtv = targets[target].Get();
        context->OMSetRenderTargets(1, &rtv, null);

        var viewport = new D3D11_VIEWPORT
        {
            Width = widths[target],
            Height = heights[target],
            MaxDepth = 1f,
        };
        context->RSSetViewports(1, &viewport);

        var parameters = new Vector4(0.5f * spread / inputWidth, 0.5f * spread / inputHeight, grain, 0f);
        context->UpdateSubresource((ID3D11Resource*)constants.Get(), 0, null, &parameters, 0, 0);

        context->PSSetShader(shader, null, 0);
        context->PSSetShaderResources(0, 1, &input);
        context->Draw(3, 0);

        context->PSSetShaderResources(0, 1, &none);
    }

    private void EnsureTargets(uint width, uint height)
    {
        for (var i = 0; i < Levels; i++)
        {
            var w = Math.Max(1u, width >> (i + 1));
            var h = Math.Max(1u, height >> (i + 1));
            if (textures[i].Get() != null && widths[i] == w && heights[i] == h) continue;

            views[i].Dispose();
            targets[i].Dispose();
            textures[i].Dispose();

            var desc = new D3D11_TEXTURE2D_DESC
            {
                Width = w,
                Height = h,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC(1, 0),
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            };
            Check(device.Get()->CreateTexture2D(&desc, null, textures[i].GetAddressOf()), "CreateTexture2D");
            Check(device.Get()->CreateRenderTargetView((ID3D11Resource*)textures[i].Get(), null, targets[i].GetAddressOf()), "CreateRenderTargetView");
            Check(device.Get()->CreateShaderResourceView((ID3D11Resource*)textures[i].Get(), null, views[i].GetAddressOf()), "CreateShaderResourceView");
            widths[i] = w;
            heights[i] = h;
        }
    }

    private void CreatePipeline()
    {
        using (var blob = Compile("vs_main", "vs_5_0"))
            Check(device.Get()->CreateVertexShader(blob.Get()->GetBufferPointer(), blob.Get()->GetBufferSize(), null, vertexShader.GetAddressOf()), "CreateVertexShader");
        using (var blob = Compile("ps_down", "ps_5_0"))
            Check(device.Get()->CreatePixelShader(blob.Get()->GetBufferPointer(), blob.Get()->GetBufferSize(), null, downShader.GetAddressOf()), "CreatePixelShader(down)");
        using (var blob = Compile("ps_up", "ps_5_0"))
            Check(device.Get()->CreatePixelShader(blob.Get()->GetBufferPointer(), blob.Get()->GetBufferSize(), null, upShader.GetAddressOf()), "CreatePixelShader(up)");

        var samplerDesc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_NEVER,
            MaxLOD = float.MaxValue,
        };
        Check(device.Get()->CreateSamplerState(&samplerDesc, sampler.GetAddressOf()), "CreateSamplerState");

        var bufferDesc = new D3D11_BUFFER_DESC
        {
            ByteWidth = 16,
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
        };
        Check(device.Get()->CreateBuffer(&bufferDesc, null, constants.GetAddressOf()), "CreateBuffer");

        pipelineReady = true;
    }

    // Dual Kawase (Marius Bjørge, « Bandwidth-Efficient Rendering », SIGGRAPH 2015).
    private const string ShaderSource = """
        Texture2D source : register(t0);
        SamplerState linearClamp : register(s0);
        cbuffer Constants : register(b0) { float2 halfPixel; float grain; float padding; };

        struct VsOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };

        VsOut vs_main(uint id : SV_VertexID)
        {
            VsOut o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.position = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        float3 tap(float2 uv) { return source.Sample(linearClamp, uv).rgb; }

        float4 ps_down(VsOut i) : SV_Target
        {
            float2 h = halfPixel;
            float3 sum = tap(i.uv) * 4;
            sum += tap(i.uv - h);
            sum += tap(i.uv + h);
            sum += tap(i.uv + float2(h.x, -h.y));
            sum += tap(i.uv - float2(h.x, -h.y));
            return float4(sum / 8, 1);
        }

        float4 ps_up(VsOut i) : SV_Target
        {
            float2 h = halfPixel;
            float3 sum = tap(i.uv + float2(-h.x * 2, 0));
            sum += tap(i.uv + float2(-h.x, h.y)) * 2;
            sum += tap(i.uv + float2(0, h.y * 2));
            sum += tap(i.uv + float2(h.x, h.y)) * 2;
            sum += tap(i.uv + float2(h.x * 2, 0));
            sum += tap(i.uv + float2(h.x, -h.y)) * 2;
            sum += tap(i.uv + float2(0, -h.y * 2));
            sum += tap(i.uv + float2(-h.x, -h.y)) * 2;
            // Bruit fixe par pixel (pas d'animation, sinon le verre « grésille »).
            float noise = frac(sin(dot(i.position.xy, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
            return float4(saturate(sum / 12 + noise * grain), 1);
        }
        """;

    private static ComPtr<ID3DBlob> Compile(string entryPoint, string profile)
    {
        var source = Encoding.UTF8.GetBytes(ShaderSource);
        var entry = Encoding.ASCII.GetBytes(entryPoint + "\0");
        var target = Encoding.ASCII.GetBytes(profile + "\0");

        ComPtr<ID3DBlob> code = default;
        ComPtr<ID3DBlob> errors = default;
        HRESULT hr;
        fixed (byte* sourcePtr = source)
        fixed (byte* entryPtr = entry)
        fixed (byte* targetPtr = target)
        {
            hr = DirectX.D3DCompile(sourcePtr, (nuint)source.Length, null, null, null,
                (sbyte*)entryPtr, (sbyte*)targetPtr, D3DCOMPILE.D3DCOMPILE_OPTIMIZATION_LEVEL3, 0,
                code.GetAddressOf(), errors.GetAddressOf());
        }

        if (hr.FAILED)
        {
            var message = errors.Get() != null
                ? Encoding.UTF8.GetString((byte*)errors.Get()->GetBufferPointer(), (int)errors.Get()->GetBufferSize())
                : string.Empty;
            errors.Dispose();
            code.Dispose();
            throw new InvalidOperationException($"D3DCompile {entryPoint} : 0x{hr.Value:X8} {message}");
        }

        errors.Dispose();
        return code;
    }

    private static void Check(HRESULT hr, string what)
    {
        if (hr.FAILED)
            throw new InvalidOperationException($"{what} : 0x{hr.Value:X8}");
    }

    public void Dispose()
    {
        try
        {
            Release();
            for (var i = 0; i < Levels; i++)
            {
                views[i].Dispose();
                targets[i].Dispose();
                textures[i].Dispose();
            }
            vertexShader.Dispose();
            downShader.Dispose();
            upShader.Dispose();
            sampler.Dispose();
            constants.Dispose();
            device.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Erreur pendant la libération du flou d'arrière-plan");
        }
    }

    private readonly struct SavedState : IDisposable
    {
        private readonly ID3D11DeviceContext* context;
        private readonly ID3D11RenderTargetView* renderTarget;
        private readonly ID3D11DepthStencilView* depthStencil;
        private readonly D3D11_VIEWPORT viewport;
        private readonly uint viewportCount;

        public SavedState(ID3D11DeviceContext* context)
        {
            this.context = context;
            ID3D11RenderTargetView* rtv;
            ID3D11DepthStencilView* dsv;
            context->OMGetRenderTargets(1, &rtv, &dsv);
            renderTarget = rtv;
            depthStencil = dsv;

            var count = 1u;
            D3D11_VIEWPORT vp;
            context->RSGetViewports(&count, &vp);
            viewport = vp;
            viewportCount = count;
        }

        public void Dispose()
        {
            var rtv = renderTarget;
            context->OMSetRenderTargets(rtv != null ? 1u : 0u, &rtv, depthStencil);
            if (viewportCount > 0)
            {
                var vp = viewport;
                context->RSSetViewports(1, &vp);
            }
            if (renderTarget != null) renderTarget->Release();
            if (depthStencil != null) depthStencil->Release();
        }
    }
}
