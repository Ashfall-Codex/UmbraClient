using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.WebAPI;

namespace UmbraSync.UI;

/// Fenêtre qui demande à UmbraServer de générer un code de lien Ashfall Connect, l'affiche
/// avec un timer 5 min, et propose Copier + Ouvrir Connect.
public sealed class AshfallLinkCodeUi : WindowMediatorSubscriberBase
{
    private const string ConnectLinkUrl = "https://connect.ashfall-codex.dev/link";
    private static readonly TimeSpan ExpirationDisplay = TimeSpan.FromMinutes(5);

    private readonly AshfallConnectService _connectService;

    private CancellationTokenSource? _cts;
    private string? _code;
    private DateTimeOffset _expiresAt;
    private bool _loading;
    private string? _error;
    private bool _justCopied;
    private DateTime _copiedAt;

    public AshfallLinkCodeUi(ILogger<AshfallLinkCodeUi> logger, MareMediator mediator,
        PerformanceCollectorService perf, AshfallConnectService connectService)
        : base(logger, mediator, "Lier mon compte Ashfall Connect###AshfallLinkCodeUi", perf)
    {
        _connectService = connectService;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(440, 320);
        SizeCondition = ImGuiCond.Always;
    }

    public override void OnOpen()
    {
        Reset();
        _ = GenerateAsync();
    }

    public override void OnClose() => Reset();

    private void Reset()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _code = null;
        _error = null;
        _loading = false;
        _justCopied = false;
    }

    private async Task GenerateAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loading = true;
        _error = null;
        _code = null;
        try
        {
            var result = await _connectService.GenerateLinkCodeAsync(_cts.Token).ConfigureAwait(false);
            _code = result.Code;
            _expiresAt = result.ExpiresAt;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    protected override void DrawInternal()
    {
        ImGui.TextWrapped("Un code à 8 caractères va être généré et affiché ci-dessous. Entrez-le sur Ashfall Connect pour lier ce personnage à votre compte.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (_loading)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Génération du code en cours…");
            return;
        }

        if (!string.IsNullOrEmpty(_error))
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "Échec :");
            ImGui.TextWrapped(_error);
            ImGui.Spacing();
            if (ImGui.Button("Réessayer"))
                _ = GenerateAsync();
            return;
        }

        if (string.IsNullOrEmpty(_code)) return;

        // Code en gros, format XXXX-XXXX
        var displayed = _code.Length == 8 ? $"{_code[..4]}-{_code[4..]}" : _code;
        using (ImRaii.PushFont(UiBuilder.MonoFont))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.ParsedGold))
        {
            var textSize = ImGui.CalcTextSize(displayed);
            var avail = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPosX((avail.X - textSize.X) * 0.5f + ImGui.GetCursorPosX());
            ImGui.SetWindowFontScale(1.8f);
            ImGui.Text(displayed);
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.Spacing();

        // Timer
        var remaining = _expiresAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var secondsLeft = (int)remaining.TotalSeconds;
        var color = secondsLeft < 60 ? ImGuiColors.DalamudRed
                  : secondsLeft < 120 ? ImGuiColors.DalamudYellow
                  : ImGuiColors.HealerGreen;
        ImGui.TextColored(color, $"Valable encore {remaining:mm\\:ss}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Copier le code", new Vector2(-1, 32)))
        {
            ImGui.SetClipboardText(_code);
            _justCopied = true;
            _copiedAt = DateTime.UtcNow;
        }

        if (_justCopied)
        {
            if ((DateTime.UtcNow - _copiedAt).TotalSeconds < 2)
                ImGui.TextColored(ImGuiColors.HealerGreen, "Copié dans le presse-papiers !");
            else _justCopied = false;
        }

        ImGui.Spacing();

        if (ImGui.Button("Ouvrir Ashfall Connect", new Vector2(-1, 32)))
            Util.OpenLink(ConnectLinkUrl);

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Étapes :");
        ImGui.BulletText("Copiez le code ci-dessus");
        ImGui.BulletText("Ouvrez Ashfall Connect, connectez-vous avec Discord");
        ImGui.BulletText("Allez dans « Lier un personnage » et collez le code");

        // Auto-fermeture quand le code expire
        if (secondsLeft == 0)
        {
            _code = null;
            _error = "Code expiré. Cliquez sur \"Réessayer\" pour en générer un nouveau.";
        }
    }
}
