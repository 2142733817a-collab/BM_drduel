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
        if (GameNetwork.IsClient && !GameNetwork.IsServer
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
        Vec3 head = agent.GetEyeGlobalPosition() + new Vec3(0f, 0f, 0.32f);
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
    private const float NameRangeSquared = 35f * 35f;
    private readonly TeamNamesModel _model = new();
    private readonly Dictionary<MissionPeer, TeamNameModel> _shown = new();
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
            ClearNames();
            RemoveLayer();
            Debug.Print($"Adimi Duel Team Names stopped: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public override void OnMissionScreenFinalize()
    {
        ClearNames();
        RemoveLayer();
        _duel = null;
        base.OnMissionScreenFinalize();
    }

    private static bool IsPrivatePlayerTeam(Team? team, Team lobbyTeam)
        => team != null && team.Side == BattleSideEnum.Attacker && !ReferenceEquals(team, lobbyTeam);

    private void UpdateNames()
    {
        MissionPeer? localPeer = GameNetwork.MyPeer?.GetComponent<MissionPeer>();
        Agent? local = localPeer?.ControlledAgent;
        Team? team = local?.Team;
        // Adimi's simultaneous group matches use distinct private attacker teams.
        // The free team and normal Duel's defender teams are excluded.
        if (localPeer == null || local == null || !ReferenceEquals(local, Agent.Main) || !local.IsActive() || local.Health <= 0f
            || team == null || !IsPrivatePlayerTeam(team, Mission.AttackerTeam)
            || !ReferenceEquals(team, localPeer.Team))
        {
            ClearNames();
            return;
        }

        _duel ??= MissionScreen.FindLayer<GauntletLayer>("MultiplayerDuel")
            ?.GetMovieIdentifier("MultiplayerDuel")?.DataSource as MultiplayerDuelVM;

        if (_duel == null || !_duel.IsEnabled)
        {
            ClearNames();
            return;
        }

        MissionPeer[] peers = VirtualPlayer.Peers<MissionPeer>().ToArray();
        // Only the player team match has another private team of human peers
        // as an enemy. TK can make our own team hostile to itself.
        if (!peers.Any(peer => peer.Team is Team opponent && !ReferenceEquals(opponent, team)
            && IsPrivatePlayerTeam(opponent, Mission.AttackerTeam) && team.IsEnemyOf(opponent)))
        {
            ClearNames();
            return;
        }

        var nearby = peers.Where(peer => !peer.IsMine && ReferenceEquals(peer.Team, team)
            && peer.ControlledAgent is { } agent && agent.IsActive() && agent.Health > 0f
            && ReferenceEquals(agent.Team, team) && local.Position.DistanceSquared(agent.Position) <= NameRangeSquared).ToHashSet();
        var markers = _duel.Markers.Targets;
        foreach (MissionPeer peer in _shown.Keys.Where(peer => !nearby.Contains(peer)).ToArray())
        {
            _model.Names.Remove(_shown[peer]);
            _shown.Remove(peer);
        }

        foreach (var marker in _suppressedCards.ToArray())
        {
            if (!markers.Contains(marker) || !nearby.Contains(marker.TargetPeer))
            {
                RestoreCard(marker);
            }
        }

        foreach (MissionPeer peer in nearby)
        {
            if (!_shown.TryGetValue(peer, out TeamNameModel? name))
            {
                EnsureLayer();
                name = new TeamNameModel();
                _shown.Add(peer, name);
                _model.Names.Add(name);
            }

            name.Update(peer.DisplayedName, peer.ControlledAgent!, MissionScreen.CombatCamera, _layer!.UIContext.Scale);
        }

        foreach (var marker in markers.Where(marker => nearby.Contains(marker.TargetPeer)))
        {
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
        if (_suppressedCards.Remove(marker) && _duel?.Markers.Targets.Contains(marker) == true)
        {
            marker.IsEnabled = _duel.Markers.IsEnabled && !marker.IsInDuel;
        }
    }

    private void ClearNames()
    {
        foreach (var marker in _suppressedCards.ToArray())
        {
            RestoreCard(marker);
        }

        _shown.Clear();
        _model.Names.Clear();
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
