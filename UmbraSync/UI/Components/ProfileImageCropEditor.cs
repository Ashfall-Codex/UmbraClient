using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Localization;

namespace UmbraSync.UI.Components;

public sealed class ProfileImageCropEditor : IDisposable
{
    private const string PopupId = "###UmbraSyncProfileImageCrop";
    private const float WheelZoomStep = 1.15f;

    private readonly ILogger _logger;
    private readonly UiSharedService _uiSharedService;
    private readonly List<IDalamudTextureWrap> _staleTextures = [];

    private Task<LoadedImage?>? _loadTask;
    private Task<byte[]?>? _renderTask;
    private ProfileImageSource? _source;
    private IDalamudTextureWrap? _texture;
    private ProfileImageKind _kind;
    private ProfileImageCrop _crop = ProfileImageCrop.Centered;
    private Action<byte[]>? _onApplied;
    private Action? _onFailed;
    private bool _open;
    private bool _renderFailed;

    private sealed record LoadedImage(ProfileImageSource Source, IDalamudTextureWrap Texture);

    public ProfileImageCropEditor(ILogger logger, UiSharedService uiSharedService)
    {
        _logger = logger;
        _uiSharedService = uiSharedService;
    }

    public void Open(string path, ProfileImageKind kind, Action<byte[]> onApplied, Action onFailed)
    {
        Close();
        _kind = kind;
        _onApplied = onApplied;
        _onFailed = onFailed;
        _loadTask = Task.Run(() => LoadAsync(path));
    }

    public void Draw()
    {
        DisposeStaleTextures();

        if (UiSharedService.TryConsumeUiTask<LoadedImage?>(ref _loadTask, _logger, out LoadedImage? loaded))
        {
            if (loaded == null)
            {
                Action? onFailed = _onFailed;
                Close();
                onFailed?.Invoke();
                return;
            }

            _source = loaded.Source;
            _texture = loaded.Texture;
            _crop = ProfileImageCrop.Centered;
            _renderFailed = false;
            _open = true;
            ImGui.OpenPopup(PopupId);
        }

        if (_source == null || _texture == null) return;

        string title = Loc.Get(_kind == ProfileImageKind.Banner ? "ProfileImageCrop.TitleBanner" : "ProfileImageCrop.TitlePortrait");
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        if (!ImGui.BeginPopupModal(title + PopupId, ref _open, UiSharedService.PopupWindowFlags | ImGuiWindowFlags.AlwaysAutoResize))
        {
            // Fermée par la croix, ou par ImGui quand la fenêtre hôte a disparu.
            Close();
            return;
        }

        bool done = false;
        if (UiSharedService.TryConsumeUiTask<byte[]?>(ref _renderTask, _logger, out byte[]? rendered))
        {
            if (rendered != null)
            {
                _onApplied?.Invoke(rendered);
                done = true;
            }
            else
            {
                _renderFailed = true;
            }
        }

        if (!done)
            done = DrawContent(_source, _texture);

        if (done) ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
        if (done) Close();
    }

    // Renvoie vrai quand l'utilisateur annule.
    private bool DrawContent(ProfileImageSource source, IDalamudTextureWrap texture)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float aspect = ProfileImageProcessor.TargetAspect(_kind);
        Vector2 canvasSize = _kind == ProfileImageKind.Banner
            ? new Vector2(600f, 600f / aspect) * scale
            : new Vector2(320f, 320f) * scale;
        bool busy = _renderTask != null;

        Vector2 canvasMin = ImGui.GetCursorScreenPos();
        Vector2 canvasMax = canvasMin + canvasSize;
        float rounding = UiSharedService.RadiusCard * scale;
        (Vector2 uv0, Vector2 uv1) = _crop.GetUv(source.Width, source.Height, aspect);
        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        drawList.AddImageRounded(texture.Handle, canvasMin, canvasMax, uv0, uv1, ImGui.ColorConvertFloat4ToU32(Vector4.One), rounding);
        drawList.AddRect(canvasMin, canvasMax, UiSharedService.Color(UiSharedService.ThemeCardBorder), rounding, ImDrawFlags.None, scale);

        ImGui.InvisibleButton("##cropCanvas", canvasSize);
        if (!busy)
        {
            if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
                _crop = _crop.Pan(ImGui.GetIO().MouseDelta / canvasSize, source.Width, source.Height, aspect);

            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
                float wheel = ImGui.GetIO().MouseWheel;
                if (MathF.Abs(wheel) > float.Epsilon)
                {
                    Vector2 anchor = (ImGui.GetMousePos() - canvasMin) / canvasSize;
                    _crop = _crop.ZoomAt(_crop.Zoom * MathF.Pow(WheelZoomStep, wheel), anchor, source.Width, source.Height, aspect);
                }
            }
        }

        float contentLeft = ImGui.GetCursorPosX();
        UiSharedService.ColorTextWrapped(Loc.Get("ProfileImageCrop.Hint"), ImGuiColors.DalamudGrey, contentLeft + canvasSize.X);
        ImGuiHelpers.ScaledDummy(4f);

        string resetLabel = Loc.Get("ProfileImageCrop.Reset");
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float resetWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Crosshairs, resetLabel);
        using (ImRaii.Disabled(busy))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("ProfileImageCrop.Zoom"));
            ImGui.SameLine();
            float zoom = _crop.Zoom;
            ImGui.SetNextItemWidth(MathF.Max(80f * scale, contentLeft + canvasSize.X - ImGui.GetCursorPosX() - resetWidth - spacing));
            ThemedSlider.AlignWithFramePaddedLabel();
            if (ThemedSlider.Float("##cropZoom", ref zoom, ProfileImageCrop.MinZoom, ProfileImageCrop.MaxZoom, "%.1f×"))
                _crop = _crop.ZoomAt(zoom, new Vector2(0.5f, 0.5f), source.Width, source.Height, aspect);

            ImGui.SameLine(contentLeft + canvasSize.X - resetWidth);
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Crosshairs, resetLabel, isInPopup: true))
                _crop = ProfileImageCrop.Centered;
        }

        if (_renderFailed)
            UiSharedService.ColorTextWrapped(Loc.Get("ProfileImageCrop.RenderError"), ImGuiColors.DalamudRed, contentLeft + canvasSize.X);

        ImGuiHelpers.ScaledDummy(4f);
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(2f);

        // Action principale à droite et à l'accent, annulation juste avant elle.
        string cancelLabel = Loc.Get("Common.Cancel");
        string applyLabel = Loc.Get(busy ? "ProfileImageCrop.Processing" : "ProfileImageCrop.Apply");
        float cancelWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Times, cancelLabel);
        float applyWidth = _uiSharedService.GetIconTextButtonSize(FontAwesomeIcon.Check, applyLabel);
        ImGui.SetCursorPosX(contentLeft + canvasSize.X - cancelWidth - applyWidth - spacing);

        bool cancelled = false;
        using (ImRaii.Disabled(busy))
        {
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Times, cancelLabel, isInPopup: true))
                cancelled = true;
            ImGui.SameLine();
            if (_uiSharedService.IconTextButton(FontAwesomeIcon.Check, applyLabel, buttonColor: UiSharedService.AccentColor))
                StartRender(source);
        }

        if (!busy && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                cancelled = true;
            else if (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
                StartRender(source);
        }

        return cancelled;
    }

    private void StartRender(ProfileImageSource source)
    {
        if (_renderTask != null) return;

        ProfileImageCrop crop = _crop;
        ProfileImageKind kind = _kind;
        _renderFailed = false;
        _renderTask = Task.Run(() =>
        {
            try
            {
                return ProfileImageProcessor.TryRender(source, crop, kind);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to render a cropped profile image");
                return null;
            }
        });
    }

    private async Task<LoadedImage?> LoadAsync(string path)
    {
        ProfileImageSource? source = null;
        try
        {
            if (new FileInfo(path).Length > ProfileImageProcessor.MaxSourceBytes) return null;

            byte[] file = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            source = ProfileImageProcessor.TryLoad(file);
            if (source == null) return null;

            IDalamudTextureWrap texture = await _uiSharedService.TextureProvider
                .CreateFromRawAsync(RawImageSpecification.Rgba32(source.Width, source.Height), source.GetRgba())
                .ConfigureAwait(false);
            LoadedImage loaded = new(source, texture);
            source = null;
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load a profile image for cropping");
            return null;
        }
        finally
        {
            source?.Dispose();
        }
    }

    private void Close()
    {
        // Un chargement abandonné en cours de route libère lui-même ce qu'il a produit.
        if (_loadTask is { } load)
        {
            _loadTask = null;
            _ = load.ContinueWith(t =>
            {
                if (!t.IsCompletedSuccessfully || t.Result is not { } abandoned) return;
                abandoned.Texture.Dispose();
                abandoned.Source.Dispose();
            }, TaskScheduler.Default);
        }

        if (_texture != null)
        {
            _staleTextures.Add(_texture);
            _texture = null;
        }

        if (_source is { } source)
        {
            _source = null;
            if (_renderTask is { } render)
                _ = render.ContinueWith(_ => source.Dispose(), TaskScheduler.Default);
            else
                source.Dispose();
        }

        _renderTask = null;
        _open = false;
        _renderFailed = false;
        _onApplied = null;
        _onFailed = null;
    }

    private void DisposeStaleTextures()
    {
        foreach (IDalamudTextureWrap texture in _staleTextures)
            texture.Dispose();
        _staleTextures.Clear();
    }

    public void Dispose()
    {
        try
        {
            Close();
            DisposeStaleTextures();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to release the profile image crop editor");
        }
    }
}
