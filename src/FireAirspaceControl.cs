using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MaceFireAirspace;

internal sealed class FireAirspaceControl : UserControl
{
    private readonly DataGridView _missionsGrid = new();
    private readonly DataGridView _activeGrid = new();
    private readonly DataGridView _conflictsGrid = new();
    private readonly Button _refreshButton = new();
    private readonly NumericUpDown _horizontalNm = new();
    private readonly NumericUpDown _verticalFt = new();

    public event EventHandler? RefreshRequested;
    public event EventHandler<SeparationSettings>? SettingsChanged;

    public FireAirspaceControl()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildTab("Call For Fire", _missionsGrid));
        tabs.TabPages.Add(BuildTab("Active Airspace", _activeGrid));
        tabs.TabPages.Add(BuildTab("Conflicts", _conflictsGrid));

        _refreshButton.Text = "Refresh";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _horizontalNm.DecimalPlaces = 2;
        _horizontalNm.Minimum = 0.1M;
        _horizontalNm.Maximum = 25;
        _horizontalNm.Increment = 0.1M;
        _horizontalNm.Width = 70;
        _horizontalNm.ValueChanged += (_, _) => RaiseSettingsChanged();

        _verticalFt.Minimum = 100;
        _verticalFt.Maximum = 10000;
        _verticalFt.Increment = 100;
        _verticalFt.Width = 80;
        _verticalFt.Value = 1000;
        _verticalFt.ValueChanged += (_, _) => RaiseSettingsChanged();

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        header.Controls.Add(_refreshButton);
        header.Controls.Add(_verticalFt);
        header.Controls.Add(new Label { Text = "Vertical ft", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
        header.Controls.Add(_horizontalNm);
        header.Controls.Add(new Label { Text = "Horizontal NM", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });

        Controls.Add(tabs);
        Controls.Add(header);

        ConfigureGrid(_missionsGrid);
        ConfigureGrid(_activeGrid);
        ConfigureGrid(_conflictsGrid);
    }

    public void SetData(
        IReadOnlyCollection<CallForFireMissionSnapshot> missions,
        IReadOnlyCollection<AirspaceVolume> activeVolumes,
        IReadOnlyCollection<ConflictRecord> conflictHistory,
        SeparationSettings settings)
    {
        SetSettingValues(settings);

        _missionsGrid.DataSource = missions
            .OrderBy(m => m.RequestId)
            .Select(m => new
            {
                CFF = m.CffFormName,
                Mission = m.MissionName,
                m.TargetNumber,
                m.BatteryName,
                m.Round,
                m.NumberOfRounds,
                m.TargetLocationText,
                GunTargetLine = $"{m.GunTargetLine_deg:0.0}",
                MaxOrdMSL_m = $"{m.MaxOrdinateMSL_m:0}",
                MaxOrdMSL_ft = $"{m.MaxOrdinateMSL_m * 3.280839895:0}",
                TOF_s = $"{m.TimeOfFlight_s:0.0}",
                m.Status
            })
            .ToList();

        _activeGrid.DataSource = activeVolumes
            .OrderBy(v => v.DisplayName)
            .Select(v => new
            {
                CFF = v.CffFormName,
                Mission = v.MissionName,
                Battery = v.FiringEntityName,
                Start = v.StartTime.ToLongTimeString(),
                NotBefore = v.NotBefore.ToLongTimeString(),
                MaxOrdMSL_m = $"{v.UpperAltitudeMsl_m:0}",
                MaxOrdMSL_ft = $"{v.UpperAltitudeMsl_m * 3.280839895:0}",
                GTLBuffer_ft = $"{v.LateralBuffer_m * 3.280839895:0}",
                Conflicts = v.Conflicts.Count
            })
            .ToList();

        _conflictsGrid.DataSource = conflictHistory
            .OrderByDescending(c => c.LastSeen)
            .Select(c => new
            {
                c.Airspace,
                c.Aircraft,
                FirstSeen = c.FirstSeen.ToLongTimeString(),
                LastSeen = c.LastSeen.ToLongTimeString(),
                c.Severity
            })
            .ToList();
    }

    private void RaiseSettingsChanged()
    {
        SettingsChanged?.Invoke(this, new SeparationSettings
        {
            Horizontal_nm = (double)_horizontalNm.Value,
            Vertical_ft = (double)_verticalFt.Value
        });
    }

    private void SetSettingValues(SeparationSettings settings)
    {
        if (_horizontalNm.Value != (decimal)settings.Horizontal_nm)
        {
            _horizontalNm.Value = (decimal)settings.Horizontal_nm;
        }

        if (_verticalFt.Value != (decimal)settings.Vertical_ft)
        {
            _verticalFt.Value = (decimal)settings.Vertical_ft;
        }
    }

    private static TabPage BuildTab(string text, Control body)
    {
        body.Dock = DockStyle.Fill;
        return new TabPage(text) { Controls = { body } };
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.ReadOnly = true;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
    }
}
