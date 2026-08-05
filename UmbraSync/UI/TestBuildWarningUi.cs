using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin;
using Microsoft.Extensions.Logging;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;

namespace UmbraSync.UI;

public sealed class TestBuildWarningUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly UiSharedService _uiShared;
    private readonly string _versionLabel;

    public TestBuildWarningUi(ILogger<TestBuildWarningUi> logger, MareMediator mediator,
        MareConfigService configService, UiSharedService uiShared,
        PerformanceCollectorService performanceCollectorService, IDalamudPluginInterface pluginInterface)
        : base(logger, mediator, Loc.Get("TestBuildWarning.WindowTitle"), performanceCollectorService)
    {
        _configService = configService;
        _uiShared = uiShared;

        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        _versionLabel = version.ToString();
        bool isTestBuild = pluginInterface.IsDev || pluginInterface.IsTesting;

        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.AlwaysAutoResize;
        SizeConstraints = new()
        {
            MinimumSize = new(440, 0),
            MaximumSize = new(440, 9999),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;

        if (isTestBuild
            && !string.Equals(_configService.Current.LastTestBuildWarningVersionSeen, _versionLabel, StringComparison.Ordinal))
        {
            IsOpen = true;
        }
    }

    public override void OnOpen()
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    protected override void DrawInternal()
    {
        using (_uiShared.UidFont.Push())
        {
            var header = Loc.Get("TestBuildWarning.Header");
            var headerWidth = ImGui.CalcTextSize(header).X;
            var headerAvail = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (headerAvail - headerWidth) / 2f));
            ImGui.TextColored(ImGuiColors.DalamudRed, header);
        }
        ImGui.Separator();
        ImGuiHelpers.ScaledDummy(4f);

        UiSharedService.ColorTextWrapped(Loc.Get("TestBuildWarning.Body"), ImGuiColors.DalamudWhite);

        ImGuiHelpers.ScaledDummy(8f);

        var signature = Loc.Get("TestBuildWarning.Signature");
        var heartIcon = FontAwesomeIcon.Heart.ToIconString();
        var sigTextWidth = ImGui.CalcTextSize(signature).X;
        float heartWidth;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            heartWidth = ImGui.CalcTextSize(heartIcon).X;
        var sigSpacing = ImGui.GetStyle().ItemSpacing.X;
        var sigAvail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (sigAvail - (sigTextWidth + sigSpacing + heartWidth)) / 2f));
        ImGui.TextColored(ImGuiColors.DalamudGrey, signature);
        ImGui.SameLine();
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(ImGuiColors.DalamudGrey, heartIcon);

        ImGuiHelpers.ScaledDummy(8f);
        ImGui.Separator();

        var buttonLabel = Loc.Get("TestBuildWarning.Acknowledge");
        var buttonWidth = 180f * ImGuiHelpers.GlobalScale;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (avail - buttonWidth) / 2f));
        if (ImGui.Button(buttonLabel, new Vector2(buttonWidth, 0)))
        {
            _configService.Current.LastTestBuildWarningVersionSeen = _versionLabel;
            _configService.Save();
            IsOpen = false;
        }
    }
}
