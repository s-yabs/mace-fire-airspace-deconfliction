using System;
using System.Collections.Generic;
using System.Drawing;
using BSI.MACE;

namespace MaceFireAirspace;

internal sealed class AirspaceVolume
{
    public string SourceKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int RequestId { get; set; }
    public string CffFormName { get; set; } = "";
    public string MissionName { get; set; } = "";
    public string FiringEntityName { get; set; } = "";
    public ulong FiringEntityId { get; set; }
    public IGeoPoint GunPoint { get; set; } = new GeoPoint();
    public IGeoPoint TargetPoint { get; set; } = new GeoPoint();
    public List<IGeoPoint> Polygon { get; } = new();
    public List<List<IGeoPoint>> FootprintPolygons { get; } = new();
    public DateTime StartTime { get; set; }
    public DateTime NotBefore { get; set; }
    public bool IsExecuted { get; set; }
    public bool HasTargetListed { get; set; }
    public bool IsAimed { get; set; }
    public bool IsTimedExecutionMission { get; set; }
    public bool IsRoundsComplete { get; set; }
    public DateTime? ScheduledExecutionTime { get; set; }
    public DateTime? RoundsCompleteRedUntil { get; set; }
    public DateTime? RoundsCompleteBlackUntil { get; set; }
    public double LowerAltitudeMsl_m { get; set; }
    public double UpperAltitudeMsl_m { get; set; }
    public double LateralBuffer_m { get; set; }
    public List<AirspaceConflict> Conflicts { get; } = new();

    public Color GetDisplayColor(
        DateTime missionTime,
        double preFireActivationSeconds,
        Color plannedAimedColor,
        Color preparingToFireColor,
        Color firingColor,
        Color coldColor)
    {
        if (Conflicts.Count > 0)
        {
            return firingColor;
        }

        if (IsRoundsComplete)
        {
            if (RoundsCompleteRedUntil.HasValue && missionTime <= RoundsCompleteRedUntil.Value)
            {
                return firingColor;
            }

            if (RoundsCompleteBlackUntil.HasValue && missionTime <= RoundsCompleteBlackUntil.Value)
            {
                return coldColor;
            }
        }

        if (IsExecuted)
        {
            return firingColor;
        }

        if (IsTimedExecutionMission && ScheduledExecutionTime.HasValue)
        {
            var preparingStart = ScheduledExecutionTime.Value.AddSeconds(-Math.Max(0, preFireActivationSeconds));
            if (missionTime >= preparingStart && missionTime < ScheduledExecutionTime.Value)
            {
                return preparingToFireColor;
            }

            return plannedAimedColor;
        }

        if (IsAimed)
        {
            return plannedAimedColor;
        }

        if (HasTargetListed)
        {
            return plannedAimedColor;
        }

        return Color.Orange;
    }

    public IEnumerable<Shape> ToShapes()
    {
        yield return ToShape(Polygon, SourceKey + ":gtl", Color.Orange);

        if (FootprintPolygons.Count > 0)
        {
            yield return ToShape(FootprintPolygons[0], SourceKey + ":gun", Color.Goldenrod);
        }

        if (FootprintPolygons.Count > 1)
        {
            yield return ToShape(FootprintPolygons[1], SourceKey + ":target", Color.Goldenrod);
        }
    }

    private Shape ToShape(List<IGeoPoint> points, string tag, Color normalColor)
    {
        var color = Conflicts.Count > 0 ? Color.Red : normalColor;
        var shape = new Shape
        {
            LayerName = "Fire Airspace",
            Type = IShape.ShapeTypeEnum.Polygon,
            IsClosed = true,
            Lower_m = LowerAltitudeMsl_m,
            Upper_m = UpperAltitudeMsl_m,
            AltitudeType = IShape.AltitudeTypeEnum.MSL,
            LineColor = color,
            FillColor = Color.FromArgb(70, color),
            AreaStyle = IShape.AreaStyleEnum.Solid,
            LineStyle = IShape.LineStyleEnum.Solid,
            LineWidth = 3,
            ShapeTag = tag
        };

        shape.Points.AddRange(points);
        shape.Centroid = AirspaceGeometry.Midpoint(GunPoint, TargetPoint);
        return shape;
    }
}
