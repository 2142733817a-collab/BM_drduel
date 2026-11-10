using System;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.Screens;

namespace AdimiDuel_Training
{
    // The isolated mission has no player agent. A custom camera follows the
    // midpoint of the two fighters so a user can watch the real game scene.
    internal sealed class TrainingCameraView : MissionView
    {
        private Camera _camera, _previous;
        private MissionScreen _screen;
        private bool _previousAllowInput;
        private bool _failed;

        public override void OnMissionScreenTick(float dt)
        {
            base.OnMissionScreenTick(dt);
            if (_failed || MissionScreen == null || MissionScreen.CombatCamera == null) return;
            try
            {
                var logic = Mission.GetMissionBehavior<TrainingMissionLogic>();
                if (logic == null || !logic.CameraReady) return;
                if (_camera == null)
                {
                    _screen = MissionScreen;
                    _previous = _screen.CustomCamera;
                    _previousAllowInput = _screen.AllowInputWithCustomCamera;
                    _camera = Camera.CreateCamera();
                    _camera.FillParametersFrom(_previous ?? _screen.CombatCamera);
                }
                if (!ReferenceEquals(_screen, MissionScreen)) return;
                if (_screen.CustomCamera != null && _screen.CustomCamera != _previous && _screen.CustomCamera != _camera) return;
                Vec3 target = logic.CameraTarget + Vec3.Up;
                Vec3 position = target + new Vec3(-5f, -8f, 5.5f);
                _camera.LookAt(position, target, Vec3.Up);
                _screen.CustomCamera = _camera;
                _screen.AllowInputWithCustomCamera = false;
            }
            catch (Exception error)
            {
                _failed = true;
                SubModule.Log("Training camera unavailable: " + error);
                SubModule.Notify("训练镜头初始化失败，详情已写入游戏日志。");
                RestoreCamera();
            }
        }

        public override void OnMissionScreenFinalize()
        {
            RestoreCamera();
            base.OnMissionScreenFinalize();
        }

        private void RestoreCamera()
        {
            Camera owned = _camera;
            if (_screen != null && _screen.CustomCamera == owned)
            {
                _screen.CustomCamera = _previous;
                _screen.AllowInputWithCustomCamera = _previousAllowInput;
            }
            _camera = null;
            _screen = null;
            _previous = null;
            if (owned != null) owned.ReleaseCamera();
        }
    }
}
