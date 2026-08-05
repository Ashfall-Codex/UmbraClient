using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Services;
using UmbraSync.Services.Mediator;
using UmbraSync.UI;
using UmbraSync.WebAPI;

namespace UmbraSync.UI;

/// Fenêtre qui demande à UmbraServer de générer un code de lien Ashfall Connect
public sealed class AshfallLinkCodeUi : WindowMediatorSubscriberBase
{
    private const string ConnectLinkUrl = "https://connect.ashfall-codex.dev/link";
    private static readonly Vector4 Ember       = new(0.831f, 0.384f, 0.165f, 1f);
    private static readonly Vector4 Gold        = new(0.831f, 0.686f, 0.416f, 1f);

    private readonly AshfallConnectService _connectService;
    private readonly UiSharedService _uiShared;
    private CancellationTokenSource? _cts;
    private string? _code;
    private DateTimeOffset _expiresAt;
    private bool _loading;
    private string? _error;
    private bool _justCopied;
    private DateTime _copiedAt;
    private string? _linkedTo;

    public AshfallLinkCodeUi(ILogger<AshfallLinkCodeUi> logger, MareMediator mediator,
        PerformanceCollectorService perf, AshfallConnectService connectService, UiSharedService uiShared)
        : base(logger, mediator, "Lier mon compte Ashfall Connect###AshfallLinkCodeUi", perf)
    {
        _connectService = connectService;
        _uiShared = uiShared;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(440, 340);
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
        _linkedTo = null;
    }

    private async Task GenerateAsync()
    {
        if (_cts != null) await _cts.CancelAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _loading = true;
        _error = null;
        _code = null;
        _linkedTo = null;
        try
        {
            var result = await _connectService.GenerateLinkCodeAsync(_cts.Token).ConfigureAwait(false);
            _code = result.Code;
            _expiresAt = result.ExpiresAt;
            _ = Task.Run(async () =>
            {
                try { await PollStatusAsync(_cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Ashfall: polling status failed"); }
            }, _cts.Token);
        }
        catch (OperationCanceledException) { /* annulation attendue (fermeture / régénération) */ }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task PollStatusAsync(CancellationToken ct)
    {
        var code = _code;
        if (string.IsNullOrEmpty(code)) return;
        try
        {
            while (!ct.IsCancellationRequested && string.Equals(_code, code, StringComparison.Ordinal) && _linkedTo is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || !string.Equals(_code, code, StringComparison.Ordinal)) return;

                var status = await _connectService.GetLinkCodeStatusAsync(code, ct).ConfigureAwait(false);
                if (status is null) continue;

                if (string.Equals(status.Status, "consumed", StringComparison.Ordinal))
                {
                    _linkedTo = status.LinkedTo ?? "votre compte Ashfall";
                    return;
                }
                if (string.Equals(status.Status, "expired", StringComparison.Ordinal))
                {
                    _code = null;
                    _error = "Code expiré.";
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* annulation attendue (fermeture / régénération) */ }
    }

    protected override void DrawInternal()
    {
        ImGui.TextWrapped("Un code à 8 caractères est généré ci-dessous. Entrez-le sur Ashfall Connect pour lier ce personnage à votre compte.");
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
            if (_uiShared.IconTextButton(FontAwesomeIcon.Redo, "Réessayer", buttonColor: Ember))
                _ = GenerateAsync();
            return;
        }

        // Cas succès : code consommé → on remplace l'écran du code par un message de succès
        if (!string.IsNullOrEmpty(_linkedTo))
        {
            ImGui.Spacing();
            ImGui.SetWindowFontScale(1.6f);
            using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                var iconStr = FontAwesomeIcon.CheckCircle.ToIconString();
                var iconSize = ImGui.CalcTextSize(iconStr);
                ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - iconSize.X) * 0.5f + ImGui.GetCursorPosX());
                ImGui.Text(iconStr);
            }
            ImGui.SetWindowFontScale(1.0f);
            ImGui.Spacing();

            using (ImRaii.PushColor(ImGuiCol.Text, Gold))
            {
                var msg = "Personnage lié avec succès !";
                var sz = ImGui.CalcTextSize(msg);
                ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - sz.X) * 0.5f + ImGui.GetCursorPosX());
                ImGui.Text(msg);
            }
            ImGui.Spacing();

            var sub = $"Lié à {_linkedTo}";
            var subSize = ImGui.CalcTextSize(sub);
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - subSize.X) * 0.5f + ImGui.GetCursorPosX());
            ImGui.TextColored(ImGuiColors.DalamudGrey, sub);

            ImGui.Spacing();
            ImGui.Spacing();
            var closeBtnWidth = ImGui.GetContentRegionAvail().X;
            if (_uiShared.IconTextButton(FontAwesomeIcon.Times, "Fermer", width: closeBtnWidth, buttonColor: Ember, height: 32))
                IsOpen = false;
            return;
        }

        if (string.IsNullOrEmpty(_code)) return;

        // Code en gros, format XXXX-XXXX
        var displayed = _code.Length == 8 ? $"{_code[..4]}-{_code[4..]}" : _code;
        using (ImRaii.PushFont(UiBuilder.MonoFont))
        using (ImRaii.PushColor(ImGuiCol.Text, Gold))
        {
            ImGui.SetWindowFontScale(1.5f);
            var textSize = ImGui.CalcTextSize(displayed);
            var avail = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPosX((avail.X - textSize.X) * 0.5f + ImGui.GetCursorPosX());
            ImGui.Text(displayed);
            ImGui.SetWindowFontScale(1.0f);
        }
        ImGui.Spacing();

        var remaining = _expiresAt - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var secondsLeft = (int)remaining.TotalSeconds;
        var timerColor = secondsLeft < 60 ? ImGuiColors.DalamudRed
                       : secondsLeft < 120 ? ImGuiColors.DalamudYellow
                       : ImGuiColors.HealerGreen;
        var timerText = $"Valable encore {remaining:mm\\:ss}";
        var timerSize = ImGui.CalcTextSize(timerText);
        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - timerSize.X) * 0.5f + ImGui.GetCursorPosX());
        ImGui.TextColored(timerColor, timerText);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var btnWidth = ImGui.GetContentRegionAvail().X;
        if (_uiShared.IconTextButton(FontAwesomeIcon.Copy, "Copier le code", width: btnWidth, buttonColor: Ember, height: 32))
        {
            ImGui.SetClipboardText(_code);
            _justCopied = true;
            _copiedAt = DateTime.UtcNow;
        }

        if (_justCopied)
        {
            if ((DateTime.UtcNow - _copiedAt).TotalSeconds < 2)
            {
                ImGui.Spacing();
                using (ImRaii.PushColor(ImGuiCol.Text, ImGuiColors.HealerGreen))
                {
                    var iconStr = FontAwesomeIcon.Check.ToIconString();
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        var iconSize = ImGui.CalcTextSize(iconStr);
                        var textSize = ImGui.CalcTextSize("Copié dans le presse-papiers !");
                        var totalWidth = iconSize.X + 6f + textSize.X;
                        ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - totalWidth) * 0.5f + ImGui.GetCursorPosX());
                        ImGui.Text(iconStr);
                    }
                    ImGui.SameLine(0, 6f);
                    ImGui.Text("Copié dans le presse-papiers !");
                }
            }
            else _justCopied = false;
        }

        ImGui.Spacing();

        if (_uiShared.IconTextButton(FontAwesomeIcon.ExternalLinkAlt, "Ouvrir Ashfall Connect", width: btnWidth, buttonColor: Ember, height: 32))
            Util.OpenLink(ConnectLinkUrl);

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Gold))
            ImGui.Text("Étapes :");
        ImGui.BulletText("Copiez le code ci-dessus");
        ImGui.BulletText("Ouvrez Ashfall Connect, connectez-vous");
        ImGui.BulletText("Allez dans « Mes personnages » et collez le code");

        if (secondsLeft == 0)
        {
            _code = null;
            _error = "Code expiré. Cliquez sur \"Réessayer\" pour en générer un nouveau.";
        }
    }
}
