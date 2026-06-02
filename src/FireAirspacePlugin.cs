using System;
using System.Collections.Generic;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
    private readonly HashSet<Control> _hookedAimButtons = new();
    private readonly HashSet<int> _manuallyAimedDisplayIndexes = new();
    private int _timerTicks;
    private int _nextMissionDisplayIndex;
    private int? _pendingAimDisplayIndex;
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
        _hookedAimButtons.Clear();
        _manuallyAimedDisplayIndexes.Clear();
        _pendingAimDisplayIndex = null;
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

        LogCallForFireDiagnostics(sender, args);

        for (var i = 0; i < args.Missions.Count; i++)
        {
            var displayIndex = _pendingAimDisplayIndex
                ?? TryGetBackgroundMissionDisplayIndex(sender, args.Missions[i])
                ?? (args.Missions.Count > 1 ? i : -1);
            var snapshot = CallForFireMissionSnapshot.FromMission(args.Missions[i], _mission?.Map, _mission?.MissionTime ?? DateTime.Now, displayIndex);
            if (snapshot.IsPlaceholder)
            {
                continue;
            }

            MergeMissionSnapshot(snapshot);
            UpdateManualAimState(snapshot);
            _pendingAimDisplayIndex = null;
        }

        SynchronizeAimedOverlays();
        UpdateMapOverlay();
        RefreshControl();
    }

    private void LogCallForFireDiagnostics(object? sender, ICallForFire.CallForFireEventArgs args)
    {
        try
        {
            var outputPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                "MACE",
                "output",
                "MaceFireAirspace-cff-debug.log");

            var text = new StringBuilder();
            text.AppendLine($"[{DateTime.Now:O}] CFF event: sender={sender?.GetType().FullName ?? "<null>"}, missions={args.Missions.Count}");
            text.AppendLine($"Sender detail: {DescribeObject(sender)}");
            text.Append(DescribeBackgroundMissions(sender));
            for (var i = 0; i < args.Missions.Count; i++)
            {
                var mission = args.Missions[i];
                text.AppendLine(
                    $"  index={i}, displayIndex={TryGetBackgroundMissionDisplayIndex(sender, mission)?.ToString() ?? ""}, requestId={mission.RequestID}, status={mission.Status}, targetNumber={mission.TargetNumber ?? ""}, targetLocation={mission.TargetLocation ?? ""}, batteryId={mission.Battery?.ID ?? 0}, battery={mission.Battery?.Name ?? ""}, batteryLabel={mission.Battery?.Label ?? ""}, round={mission.Round ?? ""}, rounds={mission.NumberOfRounds}, maxOrdMslM={mission.MaxOrdinateMSL_m:0.###}, tofS={mission.TimeOfFlight_s:0.###}, gtlDeg={mission.GunTargetLine_deg:0.###}, time={mission.Time ?? ""}, method={mission.MethodOfControl}, sead={mission.SEADMissionType}");
            }

            File.AppendAllText(outputPath, text.ToString());
        }
        catch
        {
            // Diagnostics should never interrupt MACE event handling.
        }
    }

    private static int? TryGetBackgroundMissionDisplayIndex(
        object? sender,
        ICallForFire.CallForFireEventArgs.CallForFireMission mission)
    {
        if (TryGetBackgroundMissionList(sender) is not { } missions)
        {
            return null;
        }

        var bestIndex = -1;
        var bestScore = 0;
        for (var i = 0; i < missions.Count; i++)
        {
            var candidate = missions[i];
            var score = ScoreBackgroundMission(candidate, mission);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return bestScore >= 3 ? bestIndex : null;
    }

    private static int ScoreBackgroundMission(object? candidate, ICallForFire.CallForFireEventArgs.CallForFireMission mission)
    {
        if (candidate == null)
        {
            return 0;
        }

        var score = 0;
        score += ScoreString(candidate, mission.TargetNumber, "TargetNumber", "Target", "targetNumber");
        score += ScoreString(candidate, mission.TargetLocation, "TargetLocation", "targetLocation");
        score += ScoreString(candidate, mission.Round, "Round", "round");
        score += ScoreString(candidate, mission.Status.ToString(), "Status", "status", "SolutionStatus");

        var batteryName = mission.Battery?.Name;
        if (!string.IsNullOrWhiteSpace(batteryName) && DescribeObject(candidate).IndexOf(batteryName, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score++;
        }

        if (mission.NumberOfRounds > 0 && DescribeObject(candidate).IndexOf(mission.NumberOfRounds.ToString(), StringComparison.OrdinalIgnoreCase) >= 0)
        {
            score++;
        }

        return score;
    }

    private static int ScoreString(object candidate, string? expected, params string[] memberNames)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return 0;
        }

        foreach (var memberName in memberNames)
        {
            var value = GetReflectedMemberValue(candidate, memberName)?.ToString();
            if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        return DescribeObject(candidate).IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
    }

    private static List<object?>? TryGetBackgroundMissionList(object? sender)
    {
        var value = sender == null ? null : GetReflectedMemberValue(sender, "backgroundCallForFireMissions");
        if (value is not IEnumerable enumerable || value is string)
        {
            return null;
        }

        return enumerable.Cast<object?>().ToList();
    }

    private static string DescribeBackgroundMissions(object? sender)
    {
        var missions = TryGetBackgroundMissionList(sender);
        if (missions == null)
        {
            return "";
        }

        var text = new StringBuilder();
        text.AppendLine($"Background missions: count={missions.Count}");
        for (var i = 0; i < missions.Count; i++)
        {
            text.AppendLine($"  background[{i}] type={missions[i]?.GetType().FullName ?? "<null>"} {DescribeObject(missions[i], true)}");
        }

        return text.ToString();
    }

    private static string DescribeObject(object? value, bool includeNonPublic = false)
    {
        if (value == null)
        {
            return "<null>";
        }

        try
        {
            var type = value.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic)
            {
                flags |= BindingFlags.NonPublic;
            }

            var details = new List<string>();
            details.AddRange(type
                .GetProperties(flags)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Take(40)
                .Select(p => $"{p.Name}={SafeGetValue(p, value)}"));
            details.AddRange(type
                .GetFields(flags)
                .Take(40)
                .Select(f => $"{f.Name}={SafeGetValue(f, value)}"));
            return string.Join(", ", details);
        }
        catch
        {
            return "<unavailable>";
        }
    }

    private static object? GetReflectedMemberValue(object target, string memberName)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        var type = target.GetType();
        var property = type.GetProperty(memberName, flags);
        if (property != null && property.GetIndexParameters().Length == 0)
        {
            try
            {
                return property.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        var field = type.GetField(memberName, flags);
        if (field != null)
        {
            try
            {
                return field.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string SafeGetValue(PropertyInfo property, object target)
    {
        try
        {
            return property.GetValue(target)?.ToString() ?? "";
        }
        catch
        {
            return "<error>";
        }
    }

    private static string SafeGetValue(FieldInfo field, object target)
    {
        try
        {
            return field.GetValue(target)?.ToString() ?? "";
        }
        catch
        {
            return "<error>";
        }
    }

    private void MergeMissionSnapshot(CallForFireMissionSnapshot snapshot)
    {
        var key = GetMissionKey(snapshot);
        var existing = _missions.FirstOrDefault(m => GetMissionKey(m) == key)
            ?? FindExistingMissionByContent(snapshot);
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

    private void UpdateManualAimState(CallForFireMissionSnapshot snapshot)
    {
        if (snapshot.DisplayIndex < 0)
        {
            return;
        }

        if (snapshot.IsTerminal)
        {
            _manuallyAimedDisplayIndexes.Remove(snapshot.DisplayIndex);
            return;
        }

        if (snapshot.IsAimed)
        {
            _manuallyAimedDisplayIndexes.Add(snapshot.DisplayIndex);
        }
    }

    private CallForFireMissionSnapshot? FindExistingMissionByContent(CallForFireMissionSnapshot snapshot)
    {
        return _missions
            .Where(m => IsSameFireMission(m, snapshot))
            .OrderByDescending(m => m.Status == "Aimed" || m.Status == "Executing")
            .ThenByDescending(m => m.DisplayIndex)
            .FirstOrDefault();
    }

    private static bool IsSameFireMission(CallForFireMissionSnapshot left, CallForFireMissionSnapshot right)
    {
        if (left.BatteryId != 0 && right.BatteryId != 0 && left.BatteryId != right.BatteryId)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.BatteryName)
            && !string.IsNullOrWhiteSpace(right.BatteryName)
            && !string.Equals(left.BatteryName, right.BatteryName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.TargetLocationText)
            && !string.IsNullOrWhiteSpace(right.TargetLocationText)
            && !string.Equals(left.TargetLocationText, right.TargetLocationText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.TargetNumber)
            && !string.IsNullOrWhiteSpace(right.TargetNumber)
            && !string.Equals(left.TargetNumber, right.TargetNumber, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.Round)
            && !string.IsNullOrWhiteSpace(right.Round)
            && !string.Equals(left.Round, right.Round, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (left.NumberOfRounds > 0 && right.NumberOfRounds > 0 && left.NumberOfRounds != right.NumberOfRounds)
        {
            return false;
        }

        return true;
    }

    private static string GetMissionKey(CallForFireMissionSnapshot mission)
    {
        if (mission.DisplayIndex >= 0)
        {
            return $"slot:{mission.DisplayIndex}";
        }

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
        if (snapshot.DisplayIndex >= 0)
        {
            return snapshot.DisplayIndex;
        }

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
        if (snapshot == null)
        {
            return;
        }

        var target = snapshot.TargetPoint ?? args.TargetLocation ?? args.TargetEntity?.Position;

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
        ApplyMissionOverlayState(volume, snapshot);

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
            existing.IsExecuted = volume.IsExecuted;
            existing.HasTargetListed = volume.HasTargetListed;
            existing.IsAimed = volume.IsAimed;
            existing.IsTimedExecutionMission = volume.IsTimedExecutionMission;
            existing.ScheduledExecutionTime = volume.ScheduledExecutionTime;
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
        var sourceKey = snapshot != null ? GetMissionSourceKey(snapshot) : "";
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

        var currentMissionKeys = _missions.Select(GetMissionSourceKey).ToHashSet();
        _activeVolumes.RemoveAll(v => v.SourceKey.StartsWith("cff-slot:", StringComparison.Ordinal) && !currentMissionKeys.Contains(v.SourceKey));

        foreach (var mission in _missions)
        {
            var sourceKey = GetMissionSourceKey(mission);
            if (mission.IsTerminal)
            {
                _activeVolumes.RemoveAll(v => v.SourceKey == sourceKey);
                continue;
            }

            if (!mission.ShouldDrawOverlay || mission.TargetPoint == null)
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
            ApplyMissionOverlayState(volume, mission);

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
            existing.HasTargetListed = volume.HasTargetListed;
            existing.IsAimed = volume.IsAimed;
            existing.IsTimedExecutionMission = volume.IsTimedExecutionMission;
            existing.ScheduledExecutionTime = volume.ScheduledExecutionTime;
        }
    }

    private static string GetMissionSourceKey(CallForFireMissionSnapshot mission)
    {
        return mission.DisplayIndex >= 0
            ? $"cff-slot:{mission.DisplayIndex}"
            : $"cff-request:{mission.RequestId}:{mission.TargetNumber}:{mission.BatteryId}:{mission.TargetLocationText}";
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

    private void ApplyMissionOverlayState(AirspaceVolume volume, CallForFireMissionSnapshot? mission)
    {
        volume.HasTargetListed = mission?.HasTargetListed ?? false;
        volume.IsAimed = mission != null && (mission.IsAimed || _manuallyAimedDisplayIndexes.Contains(mission.DisplayIndex));
        volume.IsTimedExecutionMission = mission?.IsTimedExecutionMission ?? false;
        volume.ScheduledExecutionTime = mission?.ScheduledExecutionTime;
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        if (_mission == null)
        {
            return;
        }

        _timerTicks++;
        HookCallForFireAimButtons();
        var now = _mission.MissionTime;
        _activeVolumes.RemoveAll(v => v.NotBefore <= now);

        if (_timerTicks % 5 == 0)
        {
            UpdateDeconfliction();
        }

        UpdateMapOverlay();

        RefreshControl();
    }

    private void HookCallForFireAimButtons()
    {
        foreach (Form form in Application.OpenForms)
        {
            var formControls = EnumerateControls(form)
                .Where(c => c.GetType().FullName == "RW_ACE.FormCallForFire_Control")
                .ToList();

            for (var missionIndex = 0; missionIndex < formControls.Count; missionIndex++)
            {
                var missionControl = formControls[missionIndex];
                HookMissionButton(missionControl, "btnAim", missionIndex);
                HookMissionButton(missionControl, "btnFire", missionIndex);
                HookMissionButton(missionControl, "btnCeaseFire", missionIndex);
                HookMissionButton(missionControl, "btnEndOfMission", missionIndex);
            }
        }
    }

    private void HookMissionButton(Control missionControl, string buttonFieldName, int missionIndex)
    {
        var button = GetReflectedMemberValue(missionControl, buttonFieldName) as Control;
        if (button == null || _hookedAimButtons.Contains(button))
        {
            return;
        }

        button.MouseDown += (_, _) => CaptureAimDisplayIndex(missionControl, missionIndex, buttonFieldName);
        button.Click += (_, _) => CaptureAimDisplayIndex(missionControl, missionIndex, buttonFieldName);
        _hookedAimButtons.Add(button);
    }

    private void CaptureAimDisplayIndex(Control missionControl, int fallbackMissionIndex, string buttonFieldName)
    {
        var parentForm = GetReflectedMemberValue(missionControl, "parentCFFForm");
        var formIndex = GetCallForFireFormIndex(parentForm);
        var missionIndex = GetSelectedMissionTabIndex(parentForm)
            ?? GetMissionIndexWithinForm(missionControl, parentForm, fallbackMissionIndex);
        if (formIndex < 0 || missionIndex < 0)
        {
            return;
        }

        var displayIndex = (formIndex * 8) + Math.Min(missionIndex, 7);
        _pendingAimDisplayIndex = displayIndex;

        if (string.Equals(buttonFieldName, "btnAim", StringComparison.Ordinal))
        {
            _manuallyAimedDisplayIndexes.Add(displayIndex);
            SynchronizeAimedOverlays();
            UpdateMapOverlay();
            RefreshControl();
        }
        else if (string.Equals(buttonFieldName, "btnCeaseFire", StringComparison.Ordinal)
            || string.Equals(buttonFieldName, "btnEndOfMission", StringComparison.Ordinal))
        {
            _manuallyAimedDisplayIndexes.Remove(displayIndex);
            SynchronizeAimedOverlays();
            UpdateMapOverlay();
            RefreshControl();
        }
    }

    private int GetCallForFireFormIndex(object? parentForm)
    {
        if (parentForm == null)
        {
            return -1;
        }

        var backgroundMissions = TryGetBackgroundMissionList(_mission?.CallForFire);
        if (backgroundMissions != null)
        {
            for (var i = 0; i < backgroundMissions.Count; i++)
            {
                var hostForm = GetReflectedMemberValue(backgroundMissions[i]!, "hostCallForFireForm");
                if (ReferenceEquals(hostForm, parentForm))
                {
                    return i;
                }
            }
        }

        var cffForms = Application.OpenForms
            .Cast<Form>()
            .Where(f => f.GetType().FullName == "RW_ACE.FormCallForFire")
            .ToList();
        for (var i = 0; i < cffForms.Count; i++)
        {
            if (ReferenceEquals(cffForms[i], parentForm))
            {
                return i;
            }
        }

        return -1;
    }

    private static int? GetSelectedMissionTabIndex(object? parentForm)
    {
        if (parentForm == null)
        {
            return null;
        }

        var tabControl = GetReflectedMemberValue(parentForm, "tabControl")
            ?? GetReflectedMemberValue(parentForm, "_tabControl");
        if (tabControl == null)
        {
            return null;
        }

        foreach (var memberName in new[] { "SelectedTabIndex", "SelectedIndex" })
        {
            var value = GetReflectedMemberValue(tabControl, memberName);
            if (TryConvertToInt(value, out var index) && index >= 0)
            {
                return index;
            }
        }

        var selectedTab = GetReflectedMemberValue(tabControl, "SelectedTab");
        var tabs = GetReflectedMemberValue(tabControl, "Tabs") as IEnumerable;
        if (selectedTab != null && tabs != null)
        {
            var index = 0;
            foreach (var tab in tabs)
            {
                if (ReferenceEquals(tab, selectedTab))
                {
                    return index;
                }

                index++;
            }
        }

        return null;
    }

    private static bool TryConvertToInt(object? value, out int result)
    {
        switch (value)
        {
            case int intValue:
                result = intValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                result = (int)longValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static int GetMissionIndexWithinForm(Control missionControl, object? parentForm, int fallbackMissionIndex)
    {
        if (parentForm is Control formControl)
        {
            var controls = EnumerateControls(formControl)
                .Where(c => c.GetType().FullName == "RW_ACE.FormCallForFire_Control")
                .ToList();
            for (var i = 0; i < controls.Count; i++)
            {
                if (ReferenceEquals(controls[i], missionControl))
                {
                    return Math.Max(0, controls.Count - 1 - i);
                }
            }
        }

        return fallbackMissionIndex;
    }

    private static IEnumerable<Control> EnumerateControls(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateControls(child))
            {
                yield return descendant;
            }
        }
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
            .Select(v => $"{v.SourceKey}:{v.NotBefore.Ticks}:{v.IsExecuted}:{v.Conflicts.Count}:{v.Polygon.Count}:{v.FootprintPolygons.Count}:{GetOverlayColor(v, _mission.MissionTime).ToArgb()}:{(v.ScheduledExecutionTime?.Ticks ?? 0)}:{v.IsAimed}:{v.HasTargetListed}:{v.IsTimedExecutionMission}"));
        if (signature == _lastOverlaySignature)
        {
            return;
        }

        ClearMapOverlay();
        foreach (var volume in _activeVolumes)
        {
            AddVolumeOverlay(volume, GetOverlayColor(volume, _mission.MissionTime));
        }

        _lastOverlaySignature = signature;
    }

    private static Color GetOverlayColor(AirspaceVolume volume, DateTime missionTime)
    {
        return volume.GetDisplayColor(missionTime);
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
