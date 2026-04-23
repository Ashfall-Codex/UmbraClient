using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using UmbraSync.Services;

namespace UmbraSync.UI.Components;

public sealed class ProfileIconPicker
{
    private readonly UiSharedService _uiSharedService;
    private readonly Lazy<List<StatusIconInfo>> _statusIcons;

    private uint _iconId;
    private uint _savedIconId;
    private bool _pickerOpen;
    private string _iconIdInput = "0";
    private string _searchText = string.Empty;
    private string _lastSearchText = string.Empty;
    private List<StatusIconInfo>? _filteredIcons;

    public ProfileIconPicker(UiSharedService uiSharedService, Lazy<List<StatusIconInfo>> statusIcons)
    {
        _uiSharedService = uiSharedService;
        _statusIcons = statusIcons;
    }

    public uint IconId
    {
        get => _iconId;
        set
        {
            _iconId = value;
            _iconIdInput = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public bool HasUnsavedChanges => _iconId != _savedIconId;

    public void SnapshotSaved() => _savedIconId = _iconId;

    public void Draw()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Icône du profil (nameplate)");
        var textureProvider = _uiSharedService.TextureProvider;

        var previewSize = 48f * ImGuiHelpers.GlobalScale;
        if (_iconId > 0)
        {
            try
            {
                var wrap = textureProvider.GetFromGameIcon(new GameIconLookup(_iconId)).GetWrapOrEmpty();
                if (wrap.Handle != IntPtr.Zero)
                {
                    var aspect = wrap.Height > 0 ? (float)wrap.Width / wrap.Height : 1f;
                    ImGui.Image(wrap.Handle, new Vector2(previewSize * aspect, previewSize));
                    ImGui.SameLine();
                }
            }
            catch { /* Icon not found */ }
        }
        else
        {
            ImGui.Dummy(new Vector2(previewSize, previewSize));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.TextUnformatted($"Icône : {_iconId}");
        if (ImGui.Button(_pickerOpen ? "Fermer le sélecteur##profileIcon" : "Choisir une icône##profileIcon"))
        {
            _pickerOpen = !_pickerOpen;
        }
        ImGui.SameLine();
        if (ImGui.Button("Retirer##profileIcon"))
        {
            _iconId = 0;
            _iconIdInput = "0";
        }
        ImGui.EndGroup();

        ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("##profileIconIdDirect", ref _iconIdInput, 10, ImGuiInputTextFlags.CharsDecimal)
            && uint.TryParse(_iconIdInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            _iconId = parsed;
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "ID direct");

        if (_pickerOpen)
        {
            ImGuiHelpers.ScaledDummy(new Vector2(0f, 4f));
            DrawGrid(textureProvider);
        }
    }

    private void DrawGrid(ITextureProvider textureProvider)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##profileIconSearch", "Rechercher (nom ou ID)...", ref _searchText, 64);

        var allIcons = _statusIcons.Value;
        if (allIcons.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Aucune icône disponible.");
            return;
        }

        if (_filteredIcons == null || !string.Equals(_lastSearchText, _searchText, StringComparison.Ordinal))
        {
            _lastSearchText = _searchText;
            if (string.IsNullOrWhiteSpace(_searchText))
            {
                _filteredIcons = allIcons;
            }
            else
            {
                var search = _searchText.Trim();
                _filteredIcons = allIcons.Where(i =>
                    i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || i.IconId.ToString().Contains(search, StringComparison.Ordinal)
                ).ToList();
            }
        }

        var icons = _filteredIcons;
        if (icons.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Aucun résultat.");
            return;
        }

        const int iconsPerRow = 10;
        var iconSize = 40f * ImGuiHelpers.GlobalScale;
        var spacing = 4f * ImGuiHelpers.GlobalScale;
        var rowHeight = iconSize + spacing;
        var totalRows = (icons.Count + iconsPerRow - 1) / iconsPerRow;
        var childHeight = 200f * ImGuiHelpers.GlobalScale;

        ImGui.BeginChild("##profileIconGrid", new Vector2(-1, childHeight), true);

        var scrollY = ImGui.GetScrollY();
        var firstVisibleRow = Math.Max(0, (int)(scrollY / rowHeight) - 1);
        var visibleRows = (int)(childHeight / rowHeight) + 3;
        var lastVisibleRow = Math.Min(totalRows - 1, firstVisibleRow + visibleRows);

        if (firstVisibleRow > 0)
            ImGui.Dummy(new Vector2(0, firstVisibleRow * rowHeight));

        for (int row = firstVisibleRow; row <= lastVisibleRow; row++)
        {
            for (int col = 0; col < iconsPerRow; col++)
            {
                int iconIndex = row * iconsPerRow + col;
                if (iconIndex >= icons.Count) break;
                var info = icons[iconIndex];

                if (col > 0) ImGui.SameLine(0, spacing);

                IDalamudTextureWrap? wrap;
                try { wrap = textureProvider.GetFromGameIcon(new GameIconLookup(info.IconId)).GetWrapOrEmpty(); }
                catch { ImGui.Dummy(new Vector2(iconSize, iconSize)); continue; }

                if (wrap.Handle == IntPtr.Zero)
                {
                    ImGui.Dummy(new Vector2(iconSize, iconSize));
                    continue;
                }

                bool isSelected = _iconId == info.IconId;
                if (isSelected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, UiSharedService.ThemeButtonActive);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonActive);
                }
                else
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UiSharedService.ThemeButtonHovered);
                }

                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, Vector2.Zero);
                ImGui.PushID($"profileIcon_{info.IconId}");
                if (ImGui.ImageButton(wrap.Handle, new Vector2(iconSize, iconSize)))
                {
                    _iconId = info.IconId;
                    _iconIdInput = info.IconId.ToString(CultureInfo.InvariantCulture);
                }
                ImGui.PopID();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor(2);

                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted($"#{info.IconId} — {info.Name}");
                    ImGui.EndTooltip();
                }
            }
        }

        var remainingRows = totalRows - lastVisibleRow - 1;
        if (remainingRows > 0)
            ImGui.Dummy(new Vector2(0, remainingRows * rowHeight));

        ImGui.EndChild();
    }
}
