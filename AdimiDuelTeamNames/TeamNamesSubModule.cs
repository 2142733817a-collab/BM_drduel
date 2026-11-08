using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Multiplayer.ViewModelCollection;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.ScreenSystem;

namespace AdimiDuelTeamNames;

public sealed class TeamNamesSubModule : MBSubModuleBase
{
    public override void OnMissionBehaviorInitialize(Mission mission)
    {
        base.OnMissionBehaviorInitialize(mission);
        if (GameNetwork.IsClient && !GameNetwork.IsServer && mission.SceneName == "duel_empire_day"
            && mission.GetMissionBehavior<MissionMultiplayerGameModeDuelClient>() != null)
        {
            mission.AddMissionBehavior(new TeamNamesView());
        }
    }
}

public sealed class TeamNamesModel : ViewModel
{
    private bool _hasNames;

    [DataSourceProperty]
    public MBBindingList<TeamNameModel> Names { get; } = new();

    [DataSourceProperty]
    public bool HasNames
    {
        get => _hasNames;
        set
        {
            if (_hasNames != value)
            {
                _hasNames = value;
                OnPropertyChangedWithValue(value, nameof(HasNames));
            }
        }
    }
}

public sealed class TeamNameModel : ViewModel
{
    private string _name = string.Empty;
    private float _x;
    private float _y;
    private bool _isVisible;

    [DataSourceProperty]
    public string Name => _name;

    [DataSourceProperty]
    public float X => _x;

    [DataSourceProperty]
    public float Y => _y;

    [DataSourceProperty]
    public bool IsVisible => _isVisible;

    internal void Update(string name, Agent agent, Camera camera, float scale)
    {
        if (_name != name)
        {
            _name = name;
            OnPropertyChangedWithValue(name, nameof(Name));
        }

        float x = 0f, y = 0f, w = 0f;
        Vec3 head = agent.GetWorldPosition().GetGroundVec3() + new Vec3(0f, 0f, agent.GetEyeGlobalHeight() + 0.18f);
        MBWindowManager.WorldToScreen(camera, head, ref x, ref y, ref w);
        bool visible = scale > 0f && w > 0f && x >= 0f && x <= Screen.RealScreenResolutionWidth
            && y >= 0f && y <= Screen.RealScreenResolutionHeight;
        if (_isVisible != visible)
        {
            _isVisible = visible;
            OnPropertyChangedWithValue(visible, nameof(IsVisible));
        }

        if (visible)
        {
            float nextX = x / scale - 140f;
            float nextY = y / scale - 28f;
            if (nextX != _x)
            {
                _x = nextX;
                OnPropertyChangedWithValue(_x, nameof(X));
            }

            if (nextY != _y)
            {
                _y = nextY;
                OnPropertyChangedWithValue(_y, nameof(Y));
            }
        }
    }
}

internal sealed class TeamNamesView : MissionView
{
    private const string MovieName = "AdimiDuelTeamNames";
    private readonly TeamNamesModel _model = new();
    private readonly Dictionary<MissionDuelPeerMarkerVM, TeamNameModel> _shown = new();
    private readonly HashSet<MissionDuelPeerMarkerVM> _suppressedCards = new();
    private MultiplayerDuelVM? _duel;
    private GauntletLayer? _layer;
    private bool _failed;

    public override void OnMissionScreenInitialize()
    {
        base.OnMissionScreenInitialize();
        ViewOrderPriority = 16;
    }

    public override void OnMissionScreenTick(float dt)
    {
        base.OnMissionScreenTick(dt);
        if (_failed)
        {
            return;
        }

        try
        {
            UpdateNames();
        }
        catch (Exception exception)
        {
            _failed = true;
            RestoreCards();
            RemoveLayer();
            Debug.Print($"Adimi Duel Team Names stopped: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public override void OnMissionScreenFinalize()
    {
        RestoreCards();
        RemoveLayer();
        _duel = null;
        base.OnMissionScreenFinalize();
    }

    private static bool IsTeammate(MissionPeer peer, Team team)
        => !peer.IsMine && ReferenceEquals(peer.Team, team)
            && peer.ControlledAgent is { } agent && agent.IsActive() && agent.Health > 0f && ReferenceEquals(agent.Team, team);

    private void UpdateNames()
    {
        MissionPeer? localPeer = GameNetwork.MyPeer?.GetComponent<MissionPeer>();
        Agent? local = localPeer?.ControlledAgent;
        Team? team = local?.Team;
        // Adimi's simultaneous group matches use distinct private attacker teams.
        // The free team and normal Duel's defender teams are excluded.
        if (localPeer == null || local == null || !ReferenceEquals(local, Agent.Main) || !local.IsActive() || local.Health <= 0f
            || team == null || team.Side != BattleSideEnum.Attacker || ReferenceEquals(team, Mission.AttackerTeam)
            || !ReferenceEquals(team, localPeer.Team))
        {
            RestoreCards();
            return;
        }

        _duel ??= MissionScreen.FindLayer<GauntletLayer>("MultiplayerDuel")
            ?.GetMovieIdentifier("MultiplayerDuel")?.DataSource as MultiplayerDuelVM;

        if (_duel == null || !_duel.IsEnabled)
        {
            RestoreCards();
            return;
        }

        var markers = _duel.Markers.Targets;
        foreach (var marker in _shown.Keys.ToArray())
        {
            if (!markers.Contains(marker) || !IsTeammate(marker.TargetPeer, team))
            {
                RestoreCard(marker);
            }
        }

        foreach (var marker in markers)
        {
            if (!IsTeammate(marker.TargetPeer, team))
            {
                continue;
            }

            if (!_shown.TryGetValue(marker, out TeamNameModel? name))
            {
                EnsureLayer();
                name = new TeamNameModel();
                _shown.Add(marker, name);
                _model.Names.Add(name);
            }

            name.Update(marker.TargetPeer.DisplayedName, marker.TargetPeer.ControlledAgent!, MissionScreen.CombatCamera, _layer!.UIContext.Scale);
            marker.IsFocused = false;
            if (marker.IsEnabled)
            {
                marker.IsEnabled = false;
                _suppressedCards.Add(marker);
            }
        }

        _model.HasNames = _shown.Count != 0;
    }

    private void EnsureLayer()
    {
        if (_layer != null)
        {
            return;
        }

        var layer = new GauntletLayer(MovieName, ViewOrderPriority);
        if (layer.LoadMovie(MovieName, _model)?.Movie is not { IsLoaded: true })
        {
            throw new InvalidOperationException($"Missing {MovieName} interface.");
        }

        MissionScreen.AddLayer(layer);
        _layer = layer;
    }

    private void RestoreCard(MissionDuelPeerMarkerVM marker)
    {
        _model.Names.Remove(_shown[marker]);
        _shown.Remove(marker);
        if (_suppressedCards.Remove(marker) && _duel?.Markers.Targets.Contains(marker) == true)
        {
            marker.IsEnabled = _duel.Markers.IsEnabled && !marker.IsInDuel;
        }
    }

    private void RestoreCards()
    {
        foreach (var marker in _shown.Keys.ToArray())
        {
            RestoreCard(marker);
        }

        _model.HasNames = false;
    }

    private void RemoveLayer()
    {
        if (_layer != null)
        {
            MissionScreen.RemoveLayer(_layer);
            _layer = null;
        }
    }
}
