using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace UmbraSync.Services.Housing;

[StructLayout(LayoutKind.Explicit, Size = 0x18)]
public unsafe struct CharacterLookAtUpdateParam
{
    [FieldOffset(0x00)] public void* VTable;
    [FieldOffset(0x08)] public float TransitionParam;
    [FieldOffset(0x0C)] public int TargetSubType;
    [FieldOffset(0x10)] public byte Flags;
}

public sealed unsafe class LookAtService : IDisposable
{
    private readonly ILogger<LookAtService> _logger;

    [Signature("40 55 57 41 54 48 8D 6C 24", DetourName = nameof(LookAtDetour))]
    private readonly Hook<LookAtDelegate> _lookAt = null!;
    private delegate void LookAtDelegate(LookAtContainer* ctrl);

    [Signature("4C 8B DC 53 48 81 EC ?? ?? ?? ?? 45 0F 29 43", DetourName = nameof(ResetLookAtDetour))]
    private readonly Hook<ResetLookAtDelegate> _resetLookAt = null!;
    private delegate void ResetLookAtDelegate(CharacterLookAtController* ctrl, int param, float transition);

    [Signature("E8 ?? ?? ?? ?? 8B D7 48 8D 8B")]
    private readonly delegate* unmanaged<CharacterLookAtController*, CharacterLookAtTargetParam*, int, CharacterLookAtUpdateParam*, void> _setupLookAt = null!;

    private readonly Dictionary<ulong, ulong> _lookingAt = new();
    private readonly System.Threading.Lock _lock = new();

    public LookAtService(ILogger<LookAtService> logger, IGameInteropProvider interop)
    {
        _logger = logger;
        try
        {
            interop.InitializeFromAttributes(this);
            _lookAt?.Enable();
            _resetLookAt?.Enable();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LookAtService : signatures introuvables, suivi du regard désactivé.");
        }
    }

    public void LookAt(nint source, nint target)
    {
        if (source == nint.Zero || target == nint.Zero) return;
        var src = ((BattleChara*)source)->GetGameObjectId().Id;
        var tgt = ((BattleChara*)target)->GetGameObjectId().Id;
        lock (_lock) _lookingAt[src] = tgt;
    }

    public void LookAtNothing(nint source)
    {
        if (source == nint.Zero) return;
        var src = ((BattleChara*)source)->GetGameObjectId().Id;
        lock (_lock) _lookingAt.Remove(src);
    }

    public void Clear()
    {
        lock (_lock) _lookingAt.Clear();
    }

    private void LookAtDetour(LookAtContainer* cntnr)
    {
        _lookAt.Original(cntnr);

        if (_setupLookAt == null) return;
        if (cntnr->Controller.OwnerObject == null) return;
        // Déjà un look-at natif (ex. cible) → on ne touche pas.
        if (cntnr->Controller.Params[0].TargetParam.TargetId.Id != 0) return;

        var srcId = cntnr->Controller.OwnerObject->GetGameObjectId().Id;
        lock (_lock)
        {
            if (!_lookingAt.TryGetValue(srcId, out var target)) return;
            // Le jeu réécrit les params chaque frame → on les re-pose après l'original.
            for (var i = 0; i < cntnr->Controller.ParamCount; i++)
            {
                var param = cntnr->Controller.Params[i].TargetParam;
                param.TargetId.Id = target;
                param.Type = CharacterLookAtTargetParam.TargetInfoType.GameObjectId;
                _setupLookAt(&cntnr->Controller, &param, i, null);
            }
        }
    }

    private void ResetLookAtDetour(CharacterLookAtController* ctrl, int param, float transition)
    {
        if (ctrl->OwnerObject != null)
        {
            lock (_lock)
            {
                if (_lookingAt.ContainsKey(ctrl->OwnerObject->GetGameObjectId().Id)) return;
            }
        }
        _resetLookAt.Original(ctrl, param, transition);
    }

    public void Dispose()
    {
        _lookAt?.Dispose();
        _resetLookAt?.Dispose();
    }
}
