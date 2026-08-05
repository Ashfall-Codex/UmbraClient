namespace UmbraSync.Services.Housing;


public sealed class NpcAppearance
{
    public byte[] Customize { get; set; } = System.Array.Empty<byte>();
    public int ModelCharaId { get; set; }
    public NpcEquipPiece[] Equipment { get; set; } = System.Array.Empty<NpcEquipPiece>();
    public NpcWeapon? MainHand { get; set; }
    public NpcWeapon? OffHand { get; set; }

    // États d'affichage capturés depuis le perso source.
    public bool HideHeadgear { get; set; }
    public bool HideWeapon { get; set; } 
    public bool WeaponDrawn { get; set; }
    public bool VisorToggled { get; set; } 
}

public sealed class NpcWeapon
{
    public ushort Id { get; set; }
    public ushort Type { get; set; }
    public ushort Variant { get; set; }
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
}

public sealed class NpcEquipPiece
{
    public ushort Id { get; set; }
    public byte Variant { get; set; }
    public byte Stain0 { get; set; }
    public byte Stain1 { get; set; }
}
