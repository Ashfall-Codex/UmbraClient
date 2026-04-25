using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiSeStringRenderer;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using UmbraSync.Localization;
using UmbraSync.UI;

namespace UmbraSync.UI.Components;

public sealed class ChatIconPicker
{
    private ushort _savedRaw;
    private string _searchText = string.Empty;

    public ushort SelectedIcon { get; set; }

    public bool HasUnsavedChanges => SelectedIcon != _savedRaw;
    public void SnapshotSaved() => _savedRaw = SelectedIcon;

    public void Draw()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, Loc.Get("Profile.ChatIcon.Label"));

        var iconSize = 18f * ImGuiHelpers.GlobalScale;
        var noneLabel = Loc.Get("Profile.ChatIcon.None");

        // Inline preview of the currently selected icon next to the combo.
        if (SelectedIcon != 0)
        {
            DrawIcon(SelectedIcon, iconSize);
            ImGui.SameLine(0, 6f);
        }

        var preview = SelectedIcon == 0
            ? noneLabel
            : AvailableIcons.FirstOrDefault(i => i.Value == SelectedIcon).Name ?? $"#{SelectedIcon}";

        ImGui.SetNextItemWidth(280f);
        if (ImGui.BeginCombo("##chatIconPick", preview))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##chatIconSearch", Loc.Get("Profile.ChatIcon.Search"), ref _searchText, 64);

            if (ImGui.Selectable(noneLabel, SelectedIcon == 0))
                SelectedIcon = 0;

            var filtered = string.IsNullOrWhiteSpace(_searchText)
                ? AvailableIcons.AsEnumerable()
                : AvailableIcons.Where(i => i.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

            foreach (var (value, name) in filtered)
            {
                DrawIcon(value, iconSize);
                ImGui.SameLine(0, 6f);
                if (ImGui.Selectable(name, SelectedIcon == value))
                    SelectedIcon = value;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.Get("Profile.ChatIcon.Clear")))
            SelectedIcon = 0;

        if (SelectedIcon != 0)
        {
            ImGui.SameLine();
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(ImGuiColors.DalamudYellow, FontAwesomeIcon.ExclamationTriangle.ToIconString());
            UiSharedService.AttachToolTip(Loc.Get("Profile.ChatIcon.LimitationWarning"));
        }
    }

    private static void DrawIcon(ushort rawIcon, float size)
    {
        var seStr = new SeString(new IconPayload((BitmapFontIcon)rawIcon)).Encode();
        var style = new SeStringDrawParams { FontSize = size };
        ImGuiHelpers.SeStringWrapped(seStr, style);
    }

    private static readonly Dictionary<BitmapFontIcon, string> FrenchNames = new()
    {
        { BitmapFontIcon.ControllerDPadUp, "Croix directionnelle (haut)" },
        { BitmapFontIcon.ControllerDPadDown, "Croix directionnelle (bas)" },
        { BitmapFontIcon.ControllerDPadLeft, "Croix directionnelle (gauche)" },
        { BitmapFontIcon.ControllerDPadRight, "Croix directionnelle (droite)" },
        { BitmapFontIcon.ControllerDPadUpDown, "Croix directionnelle (haut/bas)" },
        { BitmapFontIcon.ControllerDPadLeftRight, "Croix directionnelle (gauche/droite)" },
        { BitmapFontIcon.ControllerDPadAll, "Croix directionnelle (toutes directions)" },
        { BitmapFontIcon.ControllerButton0, "Bouton manette 0" },
        { BitmapFontIcon.ControllerButton1, "Bouton manette 1" },
        { BitmapFontIcon.ControllerButton2, "Bouton manette 2" },
        { BitmapFontIcon.ControllerButton3, "Bouton manette 3" },
        { BitmapFontIcon.ControllerShoulderLeft, "Gâchette supérieure gauche" },
        { BitmapFontIcon.ControllerShoulderRight, "Gâchette supérieure droite" },
        { BitmapFontIcon.ControllerTriggerLeft, "Gâchette inférieure gauche" },
        { BitmapFontIcon.ControllerTriggerRight, "Gâchette inférieure droite" },
        { BitmapFontIcon.ControllerAnalogLeftStickIn, "Stick analogique gauche (clic)" },
        { BitmapFontIcon.ControllerAnalogRightStickIn, "Stick analogique droit (clic)" },
        { BitmapFontIcon.ControllerStart, "Bouton Start" },
        { BitmapFontIcon.ControllerBack, "Bouton Back" },
        { BitmapFontIcon.ControllerAnalogLeftStick, "Stick analogique gauche" },
        { BitmapFontIcon.ControllerAnalogLeftStickUpDown, "Stick analogique gauche (haut/bas)" },
        { BitmapFontIcon.ControllerAnalogLeftStickLeftRight, "Stick analogique gauche (gauche/droite)" },
        { BitmapFontIcon.ControllerAnalogRightStick, "Stick analogique droit" },
        { BitmapFontIcon.ControllerAnalogRightStickUpDown, "Stick analogique droit (haut/bas)" },
        { BitmapFontIcon.ControllerAnalogRightStickLeftRight, "Stick analogique droit (gauche/droite)" },
        { BitmapFontIcon.LaNoscea, "Noscea" },
        { BitmapFontIcon.BlackShroud, "Forêt du sud" },
        { BitmapFontIcon.Thanalan, "Thanalan" },
        { BitmapFontIcon.AutoTranslateBegin, "Auto-traduction (début)" },
        { BitmapFontIcon.AutoTranslateEnd, "Auto-traduction (fin)" },
        { BitmapFontIcon.ElementFire, "Élément Feu" },
        { BitmapFontIcon.ElementIce, "Élément Glace" },
        { BitmapFontIcon.ElementWind, "Élément Vent" },
        { BitmapFontIcon.ElementEarth, "Élément Terre" },
        { BitmapFontIcon.ElementLightning, "Élément Foudre" },
        { BitmapFontIcon.ElementWater, "Élément Eau" },
        { BitmapFontIcon.LevelSync, "Synchronisation de niveau" },
        { BitmapFontIcon.Warning, "Avertissement" },
        { BitmapFontIcon.Ishgard, "Ishgard" },
        { BitmapFontIcon.Aetheryte, "Aethéryte" },
        { BitmapFontIcon.Aethernet, "Aethernet" },
        { BitmapFontIcon.GoldStar, "Étoile dorée" },
        { BitmapFontIcon.SilverStar, "Étoile argentée" },
        { BitmapFontIcon.GreenDot, "Point vert" },
        { BitmapFontIcon.SwordUnsheathed, "Épée dégainée" },
        { BitmapFontIcon.SwordSheathed, "Épée rengainée" },
        { BitmapFontIcon.Dice, "Dé" },
        { BitmapFontIcon.FlyZone, "Zone de vol" },
        { BitmapFontIcon.FlyZoneLocked, "Zone de vol verrouillée" },
        { BitmapFontIcon.NoCircle, "Interdit" },
        { BitmapFontIcon.NewAdventurer, "Nouvel aventurier" },
        { BitmapFontIcon.Mentor, "Mentor" },
        { BitmapFontIcon.MentorPvE, "Mentor JcE" },
        { BitmapFontIcon.MentorCrafting, "Mentor artisanat" },
        { BitmapFontIcon.MentorPvP, "Mentor JcJ" },
        { BitmapFontIcon.Tank, "Tank" },
        { BitmapFontIcon.Healer, "Soigneur" },
        { BitmapFontIcon.DPS, "DPS" },
        { BitmapFontIcon.Crafter, "Artisan" },
        { BitmapFontIcon.Gatherer, "Récolteur" },
        { BitmapFontIcon.AnyClass, "Toute classe" },
        { BitmapFontIcon.CrossWorld, "Inter-monde" },
        { BitmapFontIcon.FateSlay, "ALEA - Combat" },
        { BitmapFontIcon.FateBoss, "ALEA - Boss" },
        { BitmapFontIcon.FateGather, "ALEA - Récolte" },
        { BitmapFontIcon.FateDefend, "ALEA - Défense" },
        { BitmapFontIcon.FateEscort, "ALEA - Escorte" },
        { BitmapFontIcon.FateSpecial1, "ALEA - Spécial 1" },
        { BitmapFontIcon.Returner, "Revenant" },
        { BitmapFontIcon.FarEast, "Extrême-Orient" },
        { BitmapFontIcon.GyrAbania, "Gyr Abania" },
        { BitmapFontIcon.FateSpecial2, "ALEA - Spécial 2" },
        { BitmapFontIcon.PriorityWorld, "Monde prioritaire" },
        { BitmapFontIcon.ElementalLevel, "Niveau élémentaire" },
        { BitmapFontIcon.ExclamationRectangle, "Exclamation" },
        { BitmapFontIcon.NotoriousMonster, "Monstre notoire" },
        { BitmapFontIcon.Recording, "Enregistrement" },
        { BitmapFontIcon.Alarm, "Alarme" },
        { BitmapFontIcon.ArrowUp, "Flèche vers le haut" },
        { BitmapFontIcon.ArrowDown, "Flèche vers le bas" },
        { BitmapFontIcon.Crystarium, "Cristarium" },
        { BitmapFontIcon.MentorProblem, "Mentor (problème)" },
        { BitmapFontIcon.FateUnknownGold, "ALEA - Inconnu (or)" },
        { BitmapFontIcon.OrangeDiamond, "Losange orange" },
        { BitmapFontIcon.FateCrafting, "ALEA - Artisanat" },
        { BitmapFontIcon.FanFestival, "Fan Festival" },
        { BitmapFontIcon.Sharlayan, "Sharlayan" },
        { BitmapFontIcon.Ilsabard, "Ilsabard" },
        { BitmapFontIcon.Garlemald, "Garlemald" },
        { BitmapFontIcon.IslandSanctuary, "Sanctuaire insulaire" },
        { BitmapFontIcon.DamagePhysical, "Dégâts physiques" },
        { BitmapFontIcon.DamageMagical, "Dégâts magiques" },
        { BitmapFontIcon.DamageSpecial, "Dégâts spéciaux" },
        { BitmapFontIcon.GoldStarProblem, "Étoile dorée (problème)" },
        { BitmapFontIcon.BlueStar, "Étoile bleue" },
        { BitmapFontIcon.BlueStarProblem, "Étoile bleue (problème)" },
        { BitmapFontIcon.PlayStationPlus, "PlayStation Plus" },
        { BitmapFontIcon.Disconnecting, "Déconnexion" },
        { BitmapFontIcon.DoNotDisturb, "Ne pas déranger" },
        { BitmapFontIcon.Meteor, "Météore" },
        { BitmapFontIcon.RolePlaying, "Jeu de rôle" },
        { BitmapFontIcon.Gladiator, "Gladiateur" },
        { BitmapFontIcon.Pugilist, "Pugiliste" },
        { BitmapFontIcon.Marauder, "Maraudeur" },
        { BitmapFontIcon.Lancer, "Maître d'hast" },
        { BitmapFontIcon.Archer, "Archer" },
        { BitmapFontIcon.Conjurer, "Élémentaliste" },
        { BitmapFontIcon.Thaumaturge, "Occultiste" },
        { BitmapFontIcon.Carpenter, "Menuisier" },
        { BitmapFontIcon.Blacksmith, "Forgeron" },
        { BitmapFontIcon.Armorer, "Armurier" },
        { BitmapFontIcon.Goldsmith, "Orfèvre" },
        { BitmapFontIcon.Leatherworker, "Tanneur" },
        { BitmapFontIcon.Weaver, "Couturier" },
        { BitmapFontIcon.Alchemist, "Alchimiste" },
        { BitmapFontIcon.Culinarian, "Cuisinier" },
        { BitmapFontIcon.Miner, "Mineur" },
        { BitmapFontIcon.Botanist, "Botaniste" },
        { BitmapFontIcon.Fisher, "Pêcheur" },
        { BitmapFontIcon.Paladin, "Paladin" },
        { BitmapFontIcon.Monk, "Moine" },
        { BitmapFontIcon.Warrior, "Guerrier" },
        { BitmapFontIcon.Dragoon, "Chevalier dragon" },
        { BitmapFontIcon.Bard, "Barde" },
        { BitmapFontIcon.WhiteMage, "Mage blanc" },
        { BitmapFontIcon.BlackMage, "Mage noir" },
        { BitmapFontIcon.Arcanist, "Arcaniste" },
        { BitmapFontIcon.Summoner, "Invocateur" },
        { BitmapFontIcon.Scholar, "Érudit" },
        { BitmapFontIcon.Rogue, "Surineur" },
        { BitmapFontIcon.Ninja, "Ninja" },
        { BitmapFontIcon.Machinist, "Machiniste" },
        { BitmapFontIcon.DarkKnight, "Chevalier noir" },
        { BitmapFontIcon.Astrologian, "Astromancien" },
        { BitmapFontIcon.Samurai, "Samouraï" },
        { BitmapFontIcon.RedMage, "Mage rouge" },
        { BitmapFontIcon.BlueMage, "Mage bleu" },
        { BitmapFontIcon.Gunbreaker, "Pistosabreur" },
        { BitmapFontIcon.Dancer, "Danseur" },
        { BitmapFontIcon.Reaper, "Faucheur" },
        { BitmapFontIcon.Sage, "Sage" },
        { BitmapFontIcon.WaitingForDutyFinder, "En attente (Roulette)" },
        { BitmapFontIcon.Tural, "Tural" },
        { BitmapFontIcon.Viper, "Vipère" },
        { BitmapFontIcon.Pictomancer, "Pictomancien" },
        { BitmapFontIcon.VentureDeliveryMoogle, "Mog livreur de mandats" },
        { BitmapFontIcon.WatchingCutscene, "Visionnage de cinématique" },
        { BitmapFontIcon.Away, "Absent" },
        { BitmapFontIcon.CameraMode, "Mode caméra" },
        { BitmapFontIcon.LookingForParty, "Recherche d'équipe" },
        { BitmapFontIcon.GroupFinder, "Recherche de groupe" },
        { BitmapFontIcon.PartyLeader, "Chef d'équipe" },
        { BitmapFontIcon.PartyMember, "Membre d'équipe" },
        { BitmapFontIcon.CrossWorldPartyLeader, "Chef d'équipe inter-monde" },
        { BitmapFontIcon.CrossWorldPartyMember, "Membre d'équipe inter-monde" },
        { BitmapFontIcon.EventTutorial, "Tutoriel d'événement" },
    };

    private static readonly (ushort Value, string Name)[] AvailableIcons = BuildAvailableIcons();

    private static (ushort Value, string Name)[] BuildAvailableIcons()
    {
        return Enum.GetValues<BitmapFontIcon>()
            .Where(v => v != BitmapFontIcon.None)
            .Select(v => ((ushort)v, FrenchNames.TryGetValue(v, out var fr) ? fr : v.ToString()))
            .OrderBy(t => t.Item2, StringComparer.InvariantCultureIgnoreCase)
            .ToArray();
    }
}
