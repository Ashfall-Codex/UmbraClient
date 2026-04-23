using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Microsoft.Extensions.Logging;
using UmbraSync.Interop.Ipc;
using UmbraSync.Localization;
using UmbraSync.MareConfiguration;
using UmbraSync.Services;

namespace UmbraSync.UI.Components;

public sealed class HonorificEditor
{
    private readonly ILogger _logger;
    private readonly IpcManager _ipcManager;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly RpConfigService _rpConfigService;

    private string _title = string.Empty;
    private bool _isPrefix;
    private Vector3 _color = Vector3.One;
    private Vector3 _glow = Vector3.One;
    private bool _hasGlow;
    private bool _loaded;

    private string _savedTitle = string.Empty;
    private bool _savedIsPrefix;
    private Vector3 _savedColor = Vector3.One;
    private Vector3 _savedGlow = Vector3.One;
    private bool _savedHasGlow;

    private bool _restoreAttempted;
    private DateTime _lastRestoreAttempt = DateTime.MinValue;

    public HonorificEditor(ILogger logger, IpcManager ipcManager, DalamudUtilService dalamudUtil, RpConfigService rpConfigService)
    {
        _logger = logger;
        _ipcManager = ipcManager;
        _dalamudUtil = dalamudUtil;
        _rpConfigService = rpConfigService;
    }

    public bool HasUnsavedChanges =>
        !string.Equals(_title, _savedTitle, StringComparison.Ordinal)
        || _isPrefix != _savedIsPrefix
        || _color != _savedColor
        || _hasGlow != _savedHasGlow
        || (_hasGlow && _glow != _savedGlow);

    public void SnapshotSaved()
    {
        _savedTitle = _title;
        _savedIsPrefix = _isPrefix;
        _savedColor = _color;
        _savedGlow = _glow;
        _savedHasGlow = _hasGlow;
    }

    public void ResetRestoreState()
    {
        _restoreAttempted = false;
        _lastRestoreAttempt = DateTime.MinValue;
    }

    public void Draw()
    {
        if (!_ipcManager.Honorific.APIAvailable)
            return;

        if (!_loaded)
            _ = RefreshFromIpcAsync();

        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("EditProfile.Honorific.Section"));
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        ImGui.InputTextWithHint("##honorificTitle", Loc.Get("EditProfile.Honorific.TitleHint"), ref _title, 100);

        ImGui.Checkbox(Loc.Get("EditProfile.Honorific.IsPrefix"), ref _isPrefix);
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(12f, 0);
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("EditProfile.Honorific.Color"));
        ImGui.SameLine();
        ImGui.ColorEdit3("##honorificColor", ref _color, ImGuiColorEditFlags.NoInputs);
        ImGui.SameLine();
        ImGuiHelpers.ScaledDummy(12f, 0);
        ImGui.SameLine();
        ImGui.Checkbox(Loc.Get("EditProfile.Honorific.Glow"), ref _hasGlow);
        if (_hasGlow)
        {
            ImGui.SameLine();
            ImGui.ColorEdit3("##honorificGlow", ref _glow, ImGuiColorEditFlags.NoInputs);
        }
    }

    public async Task RefreshFromIpcAsync()
    {
        if (!_ipcManager.Honorific.APIAvailable) return;
        try
        {
            var b64 = await _ipcManager.Honorific.GetTitle().ConfigureAwait(false);
            RefreshFromData(b64);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error refreshing Honorific data");
        }
    }

    public void RefreshFromData(string b64Data)
    {
        _loaded = true;
        if (string.IsNullOrEmpty(b64Data))
        {
            _title = string.Empty;
            _isPrefix = false;
            _color = Vector3.One;
            _glow = Vector3.One;
            return;
        }

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64Data));
            _logger.LogInformation("Honorific raw JSON: {json}", json);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            _title = root.TryGetProperty("Title", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            _isPrefix = root.TryGetProperty("IsPrefix", out var p) && p.GetBoolean();

            if (root.TryGetProperty("Color", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                _color = new Vector3(
                    (c.TryGetProperty("X", out var cx) || c.TryGetProperty("x", out cx)) ? cx.GetSingle() : 1f,
                    (c.TryGetProperty("Y", out var cy) || c.TryGetProperty("y", out cy)) ? cy.GetSingle() : 1f,
                    (c.TryGetProperty("Z", out var cz) || c.TryGetProperty("z", out cz)) ? cz.GetSingle() : 1f);
            }
            else _color = Vector3.One;

            if (root.TryGetProperty("Glow", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                _hasGlow = true;
                _glow = new Vector3(
                    (g.TryGetProperty("X", out var gx) || g.TryGetProperty("x", out gx)) ? gx.GetSingle() : 1f,
                    (g.TryGetProperty("Y", out var gy) || g.TryGetProperty("y", out gy)) ? gy.GetSingle() : 1f,
                    (g.TryGetProperty("Z", out var gz) || g.TryGetProperty("z", out gz)) ? gz.GetSingle() : 1f);
            }
            else
            {
                _hasGlow = false;
                _glow = Vector3.One;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing Honorific data");
        }
    }

    public async Task ApplyAsync()
    {
        if (!_ipcManager.Honorific.APIAvailable) return;
        try
        {
            string json;
            if (string.IsNullOrWhiteSpace(_title))
            {
                json = string.Empty;
            }
            else
            {
                var colorObj = new { X = _color.X, Y = _color.Y, Z = _color.Z };
                object? glowObj = _hasGlow
                    ? new { X = _glow.X, Y = _glow.Y, Z = _glow.Z }
                    : null;
                json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Title = _title.Trim(),
                    IsPrefix = _isPrefix,
                    IsOriginal = false,
                    Color = colorObj,
                    Glow = glowObj
                });
            }

            var b64 = string.IsNullOrEmpty(json) ? string.Empty : Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
            await _ipcManager.Honorific.SetTitleAsync(ptr, b64).ConfigureAwait(false);
            _logger.LogInformation("Applied Honorific title: {title}", _title);

            var charName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(charName) && worldId != 0)
            {
                SaveBackup(json, charName, worldId);
            }
            _restoreAttempted = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error applying Honorific title");
        }
    }

    public async Task TrySaveBackupFromBase64Async(string b64Data)
    {
        if (string.IsNullOrEmpty(b64Data)) return;
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64Data));
            var charName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(charName) || worldId == 0) return;
            SaveBackup(json, charName, worldId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error saving Honorific backup");
        }
    }

    public async Task RunRestoreLoopAsync()
    {
        if (_restoreAttempted) return;
        await Task.Delay(2000).ConfigureAwait(false);
        for (int i = 0; i < 6; i++)
        {
            if (_restoreAttempted) break;
            await TryRestoreAsync().ConfigureAwait(false);
            if (_restoreAttempted) break;
            await Task.Delay(4000).ConfigureAwait(false);
        }
    }

    private async Task TryRestoreAsync()
    {
        if (_restoreAttempted) return;
        if (!_ipcManager.Honorific.APIAvailable) return;
        if ((DateTime.UtcNow - _lastRestoreAttempt).TotalSeconds < 2) return;
        _lastRestoreAttempt = DateTime.UtcNow;

        try
        {
            var charName = await _dalamudUtil.GetPlayerNameAsync().ConfigureAwait(false);
            var worldId = await _dalamudUtil.GetHomeWorldIdAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(charName) || worldId == 0) return;

            var profile = _rpConfigService.GetCharacterProfile(charName, worldId);
            if (string.IsNullOrEmpty(profile.HonorificBackupJson)) return;

            var currentB64 = await _ipcManager.Honorific.GetTitle().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(currentB64) && !IsOriginalTitle(currentB64))
            {
                _restoreAttempted = true;
                return;
            }

            var ptr = await _dalamudUtil.GetPlayerPointerAsync().ConfigureAwait(false);
            if (ptr == IntPtr.Zero) return;

            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(profile.HonorificBackupJson));
            await _ipcManager.Honorific.SetTitleAsync(ptr, b64).ConfigureAwait(false);
            _logger.LogInformation("Restored Honorific title from backup for {char}@{world}", charName, worldId);
            _restoreAttempted = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error restoring Honorific title from backup");
        }
    }

    private void SaveBackup(string titleJson, string charName, uint worldId)
    {
        var profile = _rpConfigService.GetCharacterProfile(charName, worldId);
        if (string.IsNullOrEmpty(titleJson))
        {
            if (!string.IsNullOrEmpty(profile.HonorificBackupJson))
            {
                _logger.LogInformation("Clearing Honorific backup for {char}@{world}", charName, worldId);
                profile.HonorificBackupJson = string.Empty;
                profile.HonorificBackupTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _rpConfigService.Save();
            }
            return;
        }

        if (string.Equals(profile.HonorificBackupJson, titleJson, StringComparison.Ordinal)) return;
        profile.HonorificBackupJson = titleJson;
        profile.HonorificBackupTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _rpConfigService.Save();
    }

    private static bool IsOriginalTitle(string b64Json)
    {
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64Json));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("IsOriginal", out var isOriginal)
                && isOriginal.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}
