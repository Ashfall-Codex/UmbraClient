using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.Logging;
using System.Numerics;
using UmbraSync.Services;
using UmbraSync.Services.Housing;
using UmbraSync.Services.Mediator;
using CameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;

namespace UmbraSync.UI;

public sealed class HousingNpcGizmoOverlayUi : WindowMediatorSubscriberBase
{
    private readonly HousingNpcScenarioService _service;
    private readonly HousingNpcSceneEditorUi _editor;
    private readonly IGameGui _gameGui;

    private Matrix4x4 _gizmoMatrix = Matrix4x4.Identity;
    private Vector3 _gizmoScale = Vector3.One;
    private bool _wasManipulating;

    public HousingNpcGizmoOverlayUi(ILogger<HousingNpcGizmoOverlayUi> logger, MareMediator mediator,
        HousingNpcScenarioService service, HousingNpcSceneEditorUi editor, IGameGui gameGui,
        PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "###HousingNpcGizmoOverlay", performanceCollectorService)
    {
        _service = service;
        _editor = editor;
        _gameGui = gameGui;

        IsOpen = true;
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoNav;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        ImGuiHelpers.ForceNextWindowMainViewport();
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos);
        ImGui.SetNextWindowSize(ImGui.GetMainViewport().Size);
    }

    protected override void DrawInternal()
    {
        if (!_editor.IsOpen || _service.CurrentLocation == null) return;

        var scenes = _service.ScenesForCurrentRoom();
        if (scenes.Count == 0) return;

        var drawing = ImGui.GetWindowDrawList();

        foreach (var scene in scenes)
        {
            foreach (var entry in scene.Entries)
            {
                var world = new Vector3(entry.X, entry.Y, entry.Z);
                if (!_gameGui.WorldToScreen(world, out var screen)) continue;

                bool isSelected = string.Equals(entry.Id, _service.SelectedEntryId, StringComparison.Ordinal);
                uint ring = isSelected ? ImGui.GetColorU32(new Vector4(1f, 0.8f, 0.1f, 1f)) : ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f));

                drawing.AddCircle(screen, 8f, ring, 16, isSelected ? 3f : 2f);
                drawing.AddCircleFilled(screen, 5f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.85f)));
                if (!string.IsNullOrWhiteSpace(entry.DisplayName))
                    drawing.AddText(screen + new Vector2(12f, -6f), ring, entry.DisplayName);

                if (!isSelected) continue;

                var position = world;
                float rotation = entry.Rotation;
                if (DrawGizmo("##npcGizmo" + entry.Id, ref position, ref rotation,
                        ImGuizmoOperation.Translate | ImGuizmoOperation.RotateY))
                    _service.SetEntryTransformLive(scene.Id, entry.Id, position, rotation);
            }
        }

        bool manipulating = ImGuizmo.IsUsing();
        if (_wasManipulating && !manipulating) _ = _service.PersistAndRefreshAsync();
        _wasManipulating = manipulating;
    }

    private bool DrawGizmo(string id, ref Vector3 position, ref float rotation,
        ImGuizmoOperation operation, float controlScale = 1.5f)
    {
        if (!TryGetPatchedProjections(out var view, out var projection, out var cameraPosition)) return false;

        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetIO().DisplaySize;

        ImGuizmo.BeginFrame();
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.SetID((int)ImGui.GetID(id));
        ImGuizmo.Enable(true);
        
        float distance = MathF.Max(Vector3.Distance(cameraPosition, position), 0.1f);
        ImGuizmo.SetGizmoSizeClipSpace(Math.Clamp(controlScale / distance, 0.02f, 0.07f));
        ImGuizmo.SetRect(pos.X, pos.Y, size.X, size.Y);

        var translation = position;
        var euler = new Vector3(0f, rotation * (180f / MathF.PI), 0f);
        ImGuizmo.RecomposeMatrixFromComponents(ref translation.X, ref euler.X, ref _gizmoScale.X, ref _gizmoMatrix.M11);

        var snap = Vector3.Zero;
        bool modified = Manipulate(ref view.M11, ref projection.M11, operation, ImGuizmoMode.Local, ref _gizmoMatrix.M11, ref snap.X);
        if (modified)
        {
            position = new Vector3(_gizmoMatrix.M41, _gizmoMatrix.M42, _gizmoMatrix.M43);
            rotation = MathF.Atan2(_gizmoMatrix.M31, _gizmoMatrix.M33);
        }

        ImGuizmo.SetID(-1);
        return modified;
    }

    private static unsafe bool Manipulate(ref float view, ref float projection, ImGuizmoOperation op,
        ImGuizmoMode mode, ref float matrix, ref float snap)
    {
        fixed (float* v = &view, p = &projection, m = &matrix, s = &snap)
        {
            return ImGuizmo.Manipulate(v, p, op, mode, m, null, s, null, null);
        }
    }
    
    private static unsafe bool TryGetPatchedProjections(out Matrix4x4 view, out Matrix4x4 projection, out Vector3 cameraPosition)
    {
        view = default;
        projection = default;
        cameraPosition = default;

        var manager = CameraManager.Instance();
        if (manager == null) return false;
        var active = manager->GetActiveCamera();
        if (active == null) return false;

        var sceneCamera = &active->CameraBase.SceneCamera;
        var renderCamera = sceneCamera->RenderCamera;
        if (renderCamera == null) return false;

        view = sceneCamera->ViewMatrix;
        projection = renderCamera->ProjectionMatrix;

        float far = renderCamera->FarPlane;
        float near = renderCamera->NearPlane;
        float clip = far / (far - near);
        projection.M43 = -(clip * near);
        projection.M33 = -((far + near) / (far - near));
        view.M44 = 1f;

        cameraPosition = new Vector3(sceneCamera->Position.X, sceneCamera->Position.Y, sceneCamera->Position.Z);
        return true;
    }
}
