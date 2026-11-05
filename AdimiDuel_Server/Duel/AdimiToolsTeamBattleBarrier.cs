using NetworkMessages.FromServer;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Server.Duel;

// Server-authored movement using the stock local-frame packet. The v1.4.8
// global-frame codec swaps axes on its read path and must not be used here.
internal sealed class AdimiToolsTeamBattleBarrier
{
    private const float RiseMeters = 3f;
    private const double TravelSeconds = 1.5d;
    private const double StepSeconds = 0.05d;
    private readonly SynchedMissionObject _synched;
    private readonly MatrixFrame _originalLocalFrame;
    private readonly MatrixFrame _loweredWorldFrame;
    private MatrixFrame _startWorldFrame;
    private MatrixFrame _targetWorldFrame;
    private double _startedAt;
    private double _nextStepAt;
    private bool _targetRaised;
    private bool _moving;

    public AdimiToolsTeamBattleBarrier(GameEntity root)
    {
        Entity = root ?? throw new ArgumentNullException(nameof(root));
        SynchedMissionObject? synched = root.GetFirstScriptOfType<SynchedMissionObject>();
        if (synched == null || synched.GetType() != typeof(SynchedMissionObject) || synched.Id.Id < 0)
        {
            throw new ArgumentException("A team-battle barrier needs the original SynchedMissionObject and a stable scene ID.", nameof(root));
        }

        _synched = synched;
        _originalLocalFrame = root.GetFrame();
        _loweredWorldFrame = root.GetGlobalFrame();
    }

    public GameEntity Entity { get; }

    public bool IsMoving => _moving;

    public bool IsSynchronized => _synched.SynchronizeCompleted;

    public bool IsRaised { get; private set; }

    public void Move(bool raised, double now, bool immediately = false)
    {
        if (!double.IsFinite(now))
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        // Read the actual pose rather than ticking a previous failed movement.
        // Cancellation can therefore start a fresh return or force restoration
        // after an interpolation/write failure.
        _startWorldFrame = Entity.GetGlobalFrame();
        _targetWorldFrame = _loweredWorldFrame;
        _targetWorldFrame.origin.x = _startWorldFrame.origin.x;
        _targetWorldFrame.origin.y = _startWorldFrame.origin.y;
        if (raised)
        {
            _targetWorldFrame.origin.z += RiseMeters;
        }

        _targetRaised = raised;
        _startedAt = now;
        _nextStepAt = now;
        _moving = true;
        IsRaised = false;
        if (immediately)
        {
            ApplyFrame(_targetWorldFrame, forceBroadcast: true, restoreOriginalLocal: !raised);
            _moving = false;
            IsRaised = raised;
        }
    }

    public void Tick(double now)
    {
        if (!_moving)
        {
            return;
        }

        if (!double.IsFinite(now))
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        double finishesAt = _startedAt + TravelSeconds;
        if (now + 0.000001d < _nextStepAt && now < finishesAt)
        {
            return;
        }

        float progress = (float)Math.Clamp((now - _startedAt) / TravelSeconds, 0d, 1d);
        MatrixFrame frame = _targetWorldFrame;
        if (progress < 1f)
        {
            frame.origin.z = _startWorldFrame.origin.z
                + ((_targetWorldFrame.origin.z - _startWorldFrame.origin.z) * progress);
        }

        bool finished = progress >= 1f;
        ApplyFrame(frame, forceBroadcast: finished, restoreOriginalLocal: finished && !_targetRaised);
        _nextStepAt = now + StepSeconds;
        if (finished)
        {
            _moving = false;
            IsRaised = _targetRaised;
        }
    }

    private void ApplyFrame(MatrixFrame worldFrame, bool forceBroadcast, bool restoreOriginalLocal)
    {
        GameEntity? parent = Entity.Parent;
        MatrixFrame localFrame = restoreOriginalLocal ? _originalLocalFrame : parent != null
            ? parent.GetGlobalFrame().TransformToLocalNonOrthogonal(in worldFrame)
            : worldFrame;
        bool alreadyAtTarget = Entity.GetFrame().Equals(localFrame);
        _synched.SetFrameSynched(ref localFrame);
        if (alreadyAtTarget && forceBroadcast)
        {
            // The stock setter skips equal frames. Reassert completed poses so
            // clients converge even when the last frame changed only locally.
            GameNetwork.BeginBroadcastModuleEvent();
            try
            {
                GameNetwork.WriteMessage(new SetMissionObjectFrame(_synched.Id, ref localFrame));
            }
            finally
            {
                GameNetwork.EndBroadcastModuleEvent(GameNetwork.EventBroadcastFlags.AddToMissionRecord);
            }
        }
    }
}
