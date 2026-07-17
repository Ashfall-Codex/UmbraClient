using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Microsoft.Extensions.Logging;
using System.Numerics;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;
using EquipmentSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.EquipmentSlot;
using WeaponSlot = FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot;
using EmoteController = FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController;

namespace UmbraSync.Services.Housing;


public sealed unsafe class NativeNpcSpawner
{
    private readonly ILogger<NativeNpcSpawner> _logger;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;

    public NativeNpcSpawner(ILogger<NativeNpcSpawner> logger, IObjectTable objectTable, IFramework framework)
    {
        _logger = logger;
        _objectTable = objectTable;
        _framework = framework;
    }

    private const int EquipSlotCount = 10; // Head..LFinger

    public NpcAppearance ReadAppearance(nint characterAddress)
    {
        var app = new NpcAppearance();
        if (characterAddress == nint.Zero) return app;
        var chara = (Character*)characterAddress;

        int cSize = chara->DrawData.CustomizeData.Data.Length;
        app.Customize = new byte[cSize];
        for (int i = 0; i < cSize; i++) app.Customize[i] = chara->DrawData.CustomizeData.Data[i];

        app.ModelCharaId = chara->ModelContainer.ModelCharaId;

        app.Equipment = new NpcEquipPiece[EquipSlotCount];
        for (int i = 0; i < EquipSlotCount; i++)
        {
            var e = chara->DrawData.Equipment((EquipmentSlot)i);
            app.Equipment[i] = new NpcEquipPiece { Id = e.Id, Variant = e.Variant, Stain0 = e.Stain0, Stain1 = e.Stain1 };
        }

        app.MainHand = ReadWeapon(chara, WeaponSlot.MainHand);
        app.OffHand = ReadWeapon(chara, WeaponSlot.OffHand);

        app.HideHeadgear = chara->DrawData.IsHatHidden;
        app.HideWeapon = chara->DrawData.IsWeaponHidden;
        app.WeaponDrawn = chara->IsWeaponDrawn;
        app.VisorToggled = chara->DrawData.IsVisorToggled;
        return app;
    }

    private static NpcWeapon ReadWeapon(Character* chara, WeaponSlot slot)
    {
        var model = chara->DrawData.Weapon(slot);
        var w = (WeaponModelId*)&model;
        return new NpcWeapon { Id = w->Id, Type = w->Type, Variant = w->Variant, Stain0 = w->Stain0, Stain1 = w->Stain1 };
    }

    private static void ApplyWeapon(Character* chara, WeaponSlot slot, NpcWeapon? w)
    {
        if (w == null || w.Id == 0) return;
        var wep = new WeaponModelId { Id = w.Id, Type = w.Type, Variant = w.Variant, Stain0 = w.Stain0, Stain1 = w.Stain1 };
        chara->DrawData.LoadWeapon(slot, wep, 0, 0, 0, 0, false);
    }

    private void ApplyAppearance(BattleChara* bc, NpcAppearance app)
    {
        var chara = (Character*)bc;
        if (app.Customize is { Length: > 0 })
        {
            int n = Math.Min(app.Customize.Length, chara->DrawData.CustomizeData.Data.Length);
            for (int i = 0; i < n; i++) chara->DrawData.CustomizeData.Data[i] = app.Customize[i];
        }
        chara->ModelContainer.ModelCharaId = app.ModelCharaId;
        if (app.Equipment != null)
        {
            for (int i = 0; i < app.Equipment.Length && i < EquipSlotCount; i++)
            {
                var p = app.Equipment[i];
                if (p == null) continue;
                chara->DrawData.Equipment((EquipmentSlot)i) = new EquipmentModelId
                {
                    Id = p.Id,
                    Variant = p.Variant,
                    Stain0 = p.Stain0,
                    Stain1 = p.Stain1,
                };
            }
        }
        ApplyWeapon(chara, WeaponSlot.MainHand, app.MainHand);
        ApplyWeapon(chara, WeaponSlot.OffHand, app.OffHand);
    }

    private static void ApplyDisplayFlags(BattleChara* bc, NpcAppearance app)
    {
        var chara = (Character*)bc;
        chara->DrawData.HideHeadgear(0, app.HideHeadgear);
        chara->DrawData.HideWeapons(app.HideWeapon);
        chara->DrawData.IsVisorToggled = app.VisorToggled;
        chara->Timeline.IsWeaponDrawn = app.WeaponDrawn;
    }

    public IGameObject? Spawn(string name, Vector3 position, float rotation, NpcAppearance appearance, ushort emote = 0)
    {
        var objectManager = ClientObjectManager.Instance();
        var objectIndex = objectManager->CreateBattleCharacter();
        if (objectIndex == 0xFFFFFFFF)
        {
            _logger.LogWarning("CreateBattleCharacter a échoué (plus de slot d'objet libre)");
            return null;
        }

        var gameObject = objectManager->GetObjectByIndex((ushort)objectIndex);
        if (gameObject == null)
        {
            _logger.LogWarning("GetObjectByIndex a renvoyé null pour l'index {Index}", objectIndex);
            return null;
        }

        var bc = (BattleChara*)gameObject;
        bc->Character.CharacterSetup.SetupBNpc(0);
        bc->Character.GameObject.ObjectKind = ObjectKind.Pc;
        bc->Character.GameObject.SubKind = (byte)BattleNpcSubKind.Player;
        bc->Character.GameObject.TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;

        var localPlayer = _objectTable.LocalPlayer;
        if (localPlayer != null)
        {
            var player = (BattleChara*)localPlayer.Address;
            bc->Character.HomeWorld = player->Character.HomeWorld;
            bc->Character.CurrentWorld = player->Character.CurrentWorld;
        }

        bc->Character.GameObject.SetPosition(position.X, position.Y, position.Z);
        bc->Character.GameObject.SetRotation(rotation);
        
        ApplyAppearance(bc, appearance);

        SetName(bc, name);

        var handle = _objectTable.CreateObjectReference((nint)bc);
        if (handle == null)
        {
            _logger.LogWarning("CreateObjectReference a renvoyé null");
            return null;
        }

        DrawWhenReady(bc, appearance, emote);
        _logger.LogInformation("Acteur natif spawné à l'index {Index} (adresse {Addr:X})", objectIndex, (nint)bc);
        return handle;
    }

    public void Despawn(nint address)
    {
        if (address == nint.Zero) return;
        var go = (GameObject*)address;
        var objectManager = ClientObjectManager.Instance();
        var index = objectManager->GetIndexByObject(go);
        if (index != 0xFFFFFFFF)
        {
            objectManager->DeleteObjectByIndex((ushort)index, 0);
            _logger.LogInformation("Acteur natif détruit (index {Index})", index);
        }
        else
        {
            _logger.LogWarning("Impossible de retrouver l'index de l'acteur à {Addr:X}", address);
        }
    }

    private static void SetName(BattleChara* bc, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        var dst = bc->Character.GameObject.Name;
        int len = Math.Min(bytes.Length, dst.Length - 1);
        for (int i = 0; i < dst.Length; i++) dst[i] = 0;
        for (int i = 0; i < len; i++) dst[i] = bytes[i];
    }

    // Fire-and-forget encadré : on n'attend pas le tick, mais tout échec est loggé
    private void RunOnTickSafe(Action action, int delayTicks = 0)
    {
        _ = _framework.RunOnTick(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Action différée sur le framework thread échouée");
            }
        }, delayTicks: delayTicks);
    }

    private void DrawWhenReady(BattleChara* bc, NpcAppearance appearance, ushort emote)
    {
        RunOnTickSafe(() =>
        {
            if (bc->Character.GameObject.IsReadyToDraw())
            {
                bc->Character.GameObject.EnableDraw();
                ApplyDisplayFlags(bc, appearance);
                RunOnTickSafe(() => ApplyDisplayFlags(bc, appearance), delayTicks: 2);
                if (emote != 0)
                    RunOnTickSafe(() => PlayEmote(bc, emote), delayTicks: 30);
            }
            else
            {
                DrawWhenReady(bc, appearance, emote);
            }
        });
    }

    private static void PlayEmote(BattleChara* bc, ushort emoteRowId)
    {
        if (emoteRowId == 0) return;
        var opt = new EmoteController.PlayEmoteOption { TargetId = 0, Flags = 1 };
        bc->EmoteController.PlayEmote(emoteRowId, &opt);
    }

    public static void PlayEmote(nint address, ushort emote)
    {
        if (address == nint.Zero || emote == 0) return;
        PlayEmote((BattleChara*)address, emote);
    }

    public static bool IsEmoting(nint address)
    {
        if (address == nint.Zero) return false;
        return ((BattleChara*)address)->EmoteController.IsEmoting();
    }


    public static void SetVisible(nint address, bool visible)
    {
        if (address == nint.Zero) return;
        var go = &((BattleChara*)address)->Character.GameObject;
        if (visible)
        {
            if (go->IsReadyToDraw()) go->EnableDraw();
        }
        else
        {
            go->DisableDraw();
        }
    }

    public static void PlayTimeline(nint address, ushort timelineId)
    {
        if (address == nint.Zero || timelineId == 0) return;
        var chara = (Character*)address;
        chara->SetMode(CharacterModes.AnimLock, 0);
        chara->Timeline.BaseOverride = timelineId;
    }

    public enum MoveAnim : ushort { Idle = 3, Walking = 13, Running = 22 }

    public static Vector3 GetPosition(nint address)
    {
        if (address == nint.Zero) return default;
        var p = ((BattleChara*)address)->Character.GameObject.Position;
        return new Vector3(p.X, p.Y, p.Z);
    }

    public static float GetRotation(nint address)
        => address == nint.Zero ? 0f : ((BattleChara*)address)->Character.GameObject.Rotation;

    public static void SetTransform(nint address, Vector3 position, float rotation)
    {
        if (address == nint.Zero) return;
        var go = &((BattleChara*)address)->Character.GameObject;
        go->SetPosition(position.X, position.Y, position.Z);
        go->SetRotation(rotation);
    }

    public static void SetMovementAnim(nint address, MoveAnim anim)
    {
        if (address == nint.Zero) return;
        var chara = (Character*)address;
        ushort code = (ushort)anim;
        if (chara->Timeline.IsWeaponDrawn)
            code = anim switch { MoveAnim.Idle => 34, MoveAnim.Walking => 41, MoveAnim.Running => 50, _ => code };

        if (anim == MoveAnim.Idle)
        {
            if (chara->Mode != CharacterModes.Normal)
            {
                chara->SetMode(CharacterModes.Normal, 0);
                chara->Timeline.BaseOverride = 0;
            }
        }
        else if (chara->Timeline.BaseOverride != code)
        {
            chara->SetMode(CharacterModes.AnimLock, 0);
            chara->Timeline.BaseOverride = code;
        }
    }

}
