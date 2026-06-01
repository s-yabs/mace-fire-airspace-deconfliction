using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using BSI.MACE;
using BSI.MACE.CallForFire;
using BSI.MACE.PlugInNS;

namespace MaceFireAirspace;

public sealed class MaceFireAirspace : IMACEPlugIn
{
    private const string LayerName = "Fire Airspace";
    private static readonly TimeSpan DefaultPostFireBuffer = TimeSpan.FromSeconds(10);

    private IMACEPlugInHost? _host;
    private IMission? _mission;
    private FireAirspaceControl? _control;
    private Form? _window;
    private Timer? _timer;
    private readonly List<CallForFireMissionSnapshot> _missions = new();
    private readonly List<AirspaceVolume> _activeVolumes = new();
    private readonly List<ConflictRecord> _conflictHistory = new();
    private readonly SeparationSettings _separationSettings = new();
    private readonly List<IMapPrimitive> _mapPrimitives = new();
    private int _timerTicks;
    private int _nextMissionDisplayIndex;
    private string _lastOverlaySignature = "";

    public string Name => "Fire Airspace Deconfliction";

    public bool Initialize(IMACEPlugInHost host)
    {
        _host = host;
        _mission = host.Mission;

        if (_mission == null)
        {
            return false;
        }

        _mission.CallForFire.CallForFireEvent += OnCallForFireEvent;
        _mission.WeaponFire += OnWeaponFire;
        _mission.WeaponDetonation += OnWeaponDetonation;
        _mission.Logger.EventLogged += OnEventLogged;

        _timer = new Timer { Interval = 1000 };
        _timer.Tick += OnTimer;
        _timer.Start();

        host.AddButton(this, "Info/Status Windows", Name, "Show fire airspace deconfliction", SystemIcons.Warning);
        LogInfo("Initialized.");
        return true;
    }

    public void Show()
    {
        if (_host == null)
        {
            return;
        }

        if (_window == null || _window.IsDisposed)
        {
            _control = new FireAirspaceControl();
            _control.RefreshRequested += (_, _) => RefreshControl();
            _control.SettingsChanged += (_, settings) =>
            {
                _separationSettings.Horizontal_nm = settings.Horizontal_nm;
                _separationSettings.Vertical_ft = settings.Vertical_ft;
                UpdateDeconfliction();
                UpdateMapOverlay();
                RefreshControl();
            };
            _window = new Form
            {
                Text = Name,
                Width = 1180,
                Height = 720,
                StartPosition = FormStartPosition.CenterParent
            };
            _window.Controls.Add(_control);
            _control.Dock = DockStyle.Fill;
            _host.RestoreForm(this, _window);
        }

        RefreshControl();
        _window.Show(_host.MainWindow);
        _window.BringToFront();
    }

    public void Close()
    {
        if (_mission != null)
        {
            _mission.CallForFire.CallForFireEvent -= OnCallForFireEvent;
            _mission.WeaponFire -= OnWeaponFire;
            _mission.WeaponDetonation -= OnWeaponDetonation;
            _mission.Logger.EventLogged -= OnEventLogged;
        }

        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimer;
            _timer.Dispose();
            _timer = null;
        }

        _window?.Close();
        _window = null;
        _control = null;
        _activeVolumes.Clear();
        _conflictHistory.Clear();
        ClearMapOverlay();
        LogInfo("Closed.");
    }

    private void OnCallForFireEvent(object? sender, EventArgs e)
    {
        if (e is not ICallForFire.CallForFireEventArgs args)
        {
            return;
        }

        if (args.Missions.Count == 0)
        {
            RefreshControl();
            return;
        }

        for (var i = 0; i < args.Missions.Count; i++)
        {
            var snapshot = CallForFireMissionSnapshot.FromMission(args.Missions[i], _mission?.Map, -1);
            if (snapshot.IsPlaceholder)
            {
                continue;
            }

            MergeMissionSnapshot(snapshot);
        }

        SynchronizeAimedOverlays();
        UpdateMapOverlay();
        RefreshControl();
    }

    private void MergeMissionSnapshot(CallForFireMissionSnapshot snapshot)
    {
        var key = GetMissionKey(snapshot);
        var existing = _missions.FirstOrDefault(m => GetMissionKey(m) == key);
        if (existing != null)
        {
            snapshot.DisplayIndex = existing.DisplayIndex;
            var index = _missions.IndexOf(existing);
            _missions[index] = snapshot;
            return;
        }

        snapshot.DisplayIndex = AllocateDisplayIndex(snapshot);
        _missions.Add(snapshot);
    }

    private static string GetMissionKey(CallForFireMissionSnapshot mission)
    {
        if (mission.RequestId > 0 && !string.IsNullOrWhiteSpace(mission.TargetNumber))
        {
            return $"request-target:{mission.RequestId}:{mission.TargetNumber}";
        }

        if (!string.IsNullOrWhiteSpace(mission.TargetNumber))
        {
            return $"target:{mission.TargetNumber}";
        }

        return $"fallback:{mission.RequestId}:{mission.BatteryId}:{mission.BatteryName}:{mission.TargetLocationText}:{mission.Round}:{mission.NumberOfRounds}";
    }

    private int AllocateDisplayIndex(CallForFireMissionSnapshot snapshot)
    {
        if (snapshot.RequestId >= 1 && snapshot.RequestId <= 4)
        {
            var formStart = (snapshot.RequestId - 1) * 8;
            var usedMissionSlots = _missions
                .Where(m => m.DisplayIndex >= formStart && m.DisplayIndex < formStart + 8)
                .Select(m => m.DisplayIndex - formStart)
                .ToHashSet();

            for (var missionSlot = 0; missionSlot < 8; missionSlot++)
            {
                if (!usedMissionSlots.Contains(missionSlot))
                {
                    return formStart + missionSlot;
                }
            }
        }

        while (_missions.Any(m => m.DisplayIndex == _nextMissionDisplayIndex))
        {
            _nextMissionDisplayIndex++;
        }

        return _nextMissionDisplayIndex++;
    }

    private void OnWeaponFire(object? sender, EventArgs e)
    {
        if (_mission == null || e is not IMission.WeaponFireEventArgs args || args.FiringEntity == null)
        {
            return;
        }

        var snapshot = FindMatchingMission(args.FiringEntity, args.TargetLocation);
        var target = snapshot?.TargetPoint ?? args.TargetLocation ?? args.TargetEntity?.Position;

        if (target == null)
        {
            LogInfo($"Weapon fire from {args.FiringEntity.Name}; no target location available for airspace.");
            return;
        }

        var volume = AirspaceGeometry.BuildVolume(
            snapshot,
            args.FiringEntity,
            target,
            _mission.MissionTime,
            DefaultPostFireBuffer,
            true);

        var existing = _activeVolumes.FirstOrDefault(v => v.SourceKey == volume.SourceKey);
        if (existing == null)
        {
            _activeVolumes.Add(volume);
        }
        else
        {
            existing.NotBefore = volume.NotBefore;
            existing.StartTime = volume.StartTime;
            existing.FiringEntityId = volume.FiringEntityId;
            existing.FiringEntityName = volume.FiringEntityName;
            existing.GunPoint = volume.GunPoint;
            existing.TargetPoint = volume.TargetPoint;
            existing.Polygon.Clear();
            existing.Polygon.AddRange(volume.Polygon);
            existing.FootprintPolygons.Clear();
            existing.FootprintPolygons.AddRange(volume.FootprintPolygons);
            existing.LowerAltitudeMsl_m = volume.LowerAltitudeMsl_m;
            existing.UpperAltitudeMsl_m = volume.UpperAltitudeMsl_m;
            existing.LateralBuffer_m = volume.LateralBuffer_m;
            existing.IsExecuted = true;
        }

        UpdateDeconfliction();
        UpdateMapOverlay();
        RefreshControl();
        LogInfo($"Activated fire airspace for {volume.DisplayName}.");
    }

    private void OnWeaponDetonation(object? sender, EventArgs e)
    {
        if (_mission == null || e is not IMission.WeaponDetonationEventArgs args || args.FiringEntity == null)
        {
            return;
        }

        var now = _mission.MissionTime;
        var snapshot = FindMatchingMission(args.FiringEntity, args.DetonationLocation);
        var sourceKey = snapshot != null ? $"cff:{snapshot.RequestId}" : "";
        foreach (var volume in _activeVolumes.Where(v => v.FiringEntityId == args.FiringEntity.ID || v.SourceKey == sourceKey))
        {
            var tof = args.TimeOfFlight_s > 0 ? TimeSpan.FromSeconds(args.TimeOfFlight_s) : TimeSpan.Zero;
            volume.NotBefore = now.Add(tof).Add(DefaultPostFireBuffer);
        }

        UpdateMapOverlay();
        RefreshControl();
    }

    private void OnEventLogged(object? sender, EventArgs e)
    {
        if (_mission == null || e is not ILogger.EventLogEventArgs args)
        {
            return;
        }

        var text = $"{args.Name} {args.Message}";
        if (text.IndexOf("deconfliction", StringComparison.OrdinalIgnoreCase) < 0
            && text.IndexOf("airspace", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return;
        }

        _conflictHistory.Add(new ConflictRecord
        {
            Airspace = "MACE Airspace Event",
            Aircraft = string.IsNullOrWhiteSpace(args.Name) ? "MACE Event" : args.Name,
            EntityId = 0,
            FirstSeen = args.MissionTime,
            LastSeen = args.MissionTime,
            Severity = args.Message
        });
        RefreshControl();
    }

    private void SynchronizeAimedOverlays()
    {
        if (_mission == null)
        {
            return;
        }

        var currentMissionKeys = _missions.Select(m => $"cff:{m.RequestId}").ToHashSet();
        _activeVolumes.RemoveAll(v => v.RequestId > 0 && !currentMissionKeys.Contains(v.SourceKey));

        foreach (var mission in _missions)
        {
            var sourceKey = $"cff:{mission.RequestId}";
            if (mission.IsTerminal)
            {
                _activeVolumes.RemoveAll(v => v.SourceKey == sourceKey);
                continue;
            }

            if (!mission.ShouldDrawAimedOverlay || mission.TargetPoint == null)
            {
                continue;
            }

            var battery = FindBatteryEntity(mission);
            if (battery == null)
            {
                continue;
            }

            var volume = AirspaceGeometry.BuildVolume(
                mission,
                battery,
                mission.TargetPoint,
                _mission.MissionTime,
                TimeSpan.FromHours(6),
                mission.IsExecuting);

            var existing = _activeVolumes.FirstOrDefault(v => v.SourceKey == sourceKey);
            if (existing == null)
            {
                _activeVolumes.Add(volume);
                continue;
            }

            existing.NotBefore = volume.NotBefore;
            existing.StartTime = volume.StartTime;
            existing.FiringEntityId = volume.FiringEntityId;
            existing.FiringEntityName = volume.FiringEntityName;
            existing.GunPoint = volume.GunPoint;
            existing.TargetPoint = volume.TargetPoint;
            existing.Polygon.Clear();
            existing.Polygon.AddRange(volume.Polygon);
            existing.FootprintPolygons.Clear();
            existing.FootprintPolygons.AddRange(volume.FootprintPolygons);
            existing.LowerAltitudeMsl_m = volume.LowerAltitudeMsl_m;
            existing.UpperAltitudeMsl_m = volume.UpperAltitudeMsl_m;
            existing.LateralBuffer_m = volume.LateralBuffer_m;
            existing.IsExecuted = existing.IsExecuted || volume.IsExecuted;
        }
    }

    private IPhysicalEntity? FindBatteryEntity(CallForFireMissionSnapshot mission)
    {
        if (_mission == null)
        {
            return null;
        }

        if (mission.BatteryId != 0 && _mission.PhysicalEntities.TryGetValue(mission.BatteryId, out var directBattery))
        {
            return directBattery;
        }

        if (mission.BatteryAggregateId != 0 && _mission.AggregateEntities.TryGetValue(mission.BatteryAggregateId, out var aggregate))
        {
            return aggregate.PrimaryPhysicalEntity ?? aggregate.PhysicalEntities.Values.FirstOrDefault();
        }

        return _mission.PhysicalEntities.Values.FirstOrDefault(entity =>
            string.Equals(entity.Name, mission.BatteryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.Label, mission.BatteryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entity.GetAggregate()?.Name, mission.BatteryName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        if (_mission == null)
        {
            return;
        }

        _timerTicks++;
        var before = _activeVolumes.Count;
        var now = _mission.MissionTime;
        _activeVolumes.RemoveAll(v => v.NotBefore <= now);

        if (_timerTicks % 5 == 0)
        {
            UpdateDeconfliction();
        }

        if (_activeVolumes.Count != before)
        {
            UpdateMapOverlay();
        }

        RefreshControl();
    }

    private CallForFireMissionSnapshot? FindMatchingMission(IPhysicalEntity firingEntity, IGeoPoint? target)
    {
        var candidates = _missions
            .Where(m =>
                m.BatteryId == firingEntity.ID
                || m.BatteryAggregateId == firingEntity.GetAggregate()?.ID
                || string.Equals(m.BatteryName, firingEntity.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.BatteryName, firingEntity.Label, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (target != null)
        {
            return candidates
                .OrderBy(m => m.TargetPoint == null ? double.MaxValue : AirspaceGeometry.DistanceMeters(m.TargetPoint, target))
                .FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private void UpdateDeconfliction()
    {
        if (_mission == null)
        {
            return;
        }

        var aircraft = _mission.PhysicalEntities.Values
            .Where(DeconflictionEngine.IsLikelyAircraft)
            .ToList();

        foreach (var volume in _activeVolumes)
        {
            volume.Conflicts.Clear();
            volume.Conflicts.AddRange(DeconflictionEngine.FindConflicts(volume, aircraft, _separationSettings));
            foreach (var conflict in volume.Conflicts)
            {
                UpsertConflictHistory(volume, conflict);
            }
        }
    }

    private void UpsertConflictHistory(AirspaceVolume volume, AirspaceConflict conflict)
    {
        if (_mission == null)
        {
            return;
        }

        var now = _mission.MissionTime;
        var record = _conflictHistory.FirstOrDefault(c => c.Airspace == volume.DisplayName && c.EntityId == conflict.EntityId);
        if (record == null)
        {
            record = new ConflictRecord
            {
                Airspace = volume.DisplayName,
                Aircraft = conflict.EntityName,
                EntityId = conflict.EntityId,
                FirstSeen = now
            };
            _conflictHistory.Add(record);
        }

        record.LastSeen = now;
        record.DistanceToVolume_m = conflict.DistanceToVolume_m;
        record.DistanceToVolume_nm = conflict.DistanceToVolume_m / 1852.0;
        record.VerticalSeparation_ft = conflict.VerticalSeparation_ft;
        record.AltitudeMsl_ft = conflict.AltitudeMsl_m * 3.280839895;
        record.Severity = conflict.Severity;
    }

    private void UpdateMapOverlay()
    {
        if (_mission?.Map == null)
        {
            return;
        }

        var signature = string.Join("|", _activeVolumes
            .OrderBy(v => v.SourceKey)
            .Select(v => $"{v.SourceKey}:{v.NotBefore.Ticks}:{v.IsExecuted}:{v.Conflicts.Count}:{v.Polygon.Count}:{v.FootprintPolygons.Count}"));
        if (signature == _lastOverlaySignature)
        {
            return;
        }

        ClearMapOverlay();
        foreach (var volume in _activeVolumes)
        {
            AddVolumeOverlay(volume, GetOverlayColor(volume));
        }

        _lastOverlaySignature = signature;
    }

    private static Color GetOverlayColor(AirspaceVolume volume)
    {
        if (volume.Conflicts.Count > 0 || volume.IsExecuted)
        {
            return Color.Red;
        }

        return Color.Yellow;
    }

    private void AddVolumeOverlay(AirspaceVolume volume, Color color)
    {
        AddLine(volume.GunPoint, volume.TargetPoint, color, 4);
        AddPolyline(volume.Polygon, color, true);
        foreach (var footprint in volume.FootprintPolygons)
        {
            AddPolyline(footprint, color, true);
        }
    }

    private void AddPolyline(List<IGeoPoint> points, Color color, bool close)
    {
        if (points.Count < 2)
        {
            return;
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            AddLine(points[i], points[i + 1], color, 3);
        }

        if (close)
        {
            AddLine(points[points.Count - 1], points[0], color, 3);
        }
    }

    private void AddLine(IGeoPoint start, IGeoPoint end, Color color, int width)
    {
        if (_mission?.Map == null)
        {
            return;
        }

        var primitive = new MapPrimitive(start.Geographic, end.Geographic, color, width)
        {
            StrokeColor = color,
            StrokeStyle = IMapPrimitive.PrimitiveStrokeStyleEnum.Solid,
            StrokeWidth = width,
            ZOrder = 1000
        };

        _mission.Map.AddMapPrimitive(primitive);
        _mapPrimitives.Add(primitive);
    }

    private void ClearMapOverlay()
    {
        if (_mission?.Map != null)
        {
            foreach (var primitive in _mapPrimitives.ToList())
            {
                _mission.Map.RemoveMapPrimitive(primitive);
            }
        }

        _mapPrimitives.Clear();
        _lastOverlaySignature = "";
    }

    private void RefreshControl()
    {
        _control?.SetData(_missions, _activeVolumes, _conflictHistory, _separationSettings);
    }

    private void LogInfo(string message)
    {
        _mission?.Logger?.InfoMessage(Name, message);
    }
}
