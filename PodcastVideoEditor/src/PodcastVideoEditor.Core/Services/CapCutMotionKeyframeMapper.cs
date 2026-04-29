#nullable enable
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PodcastVideoEditor.Core.Services;

/// <summary>
/// Maps a Ken Burns motion preset (<see cref="MotionPresets"/>) to a pair of CapCut
/// keyframes (start/end of segment) that approximate the same zoom/pan motion.
/// CapCut interpolates linearly between keyframes, so we emit just two points.
/// </summary>
public static class CapCutMotionKeyframeMapper
{
    public sealed record KeyframeBatch(
        IReadOnlyList<string> PropertyTypes,
        IReadOnlyList<double> Times,
        IReadOnlyList<string> Values);

    private const double EndTimeEpsilon = 0.001;

    public static KeyframeBatch? Build(
        string? motionPreset,
        double intensity,
        double durationSeconds,
        double baseScaleX,
        double baseScaleY,
        double basePosX,
        double basePosY)
    {
        if (string.IsNullOrWhiteSpace(motionPreset) || motionPreset == MotionPresets.None)
            return null;
        if (durationSeconds <= 0)
            return null;

        intensity = Math.Clamp(intensity, 0.0, 1.0);
        if (intensity <= 0.0)
            return null;

        var maxZoom = 1.0 + intensity * 0.4;
        var panScaleFactor = 1.0 + intensity * 0.3;
        var panTravel = (panScaleFactor - 1.0) * 0.5;

        var t0 = 0.0;
        var t1 = Math.Max(EndTimeEpsilon, durationSeconds - EndTimeEpsilon);

        return motionPreset switch
        {
            MotionPresets.ZoomIn => BuildZoomFrames(
                baseScaleX, baseScaleY, baseScaleX * maxZoom, baseScaleY * maxZoom,
                basePosX, basePosY, basePosX, basePosY, t0, t1),

            MotionPresets.ZoomOut => BuildZoomFrames(
                baseScaleX * maxZoom, baseScaleY * maxZoom, baseScaleX, baseScaleY,
                basePosX, basePosY, basePosX, basePosY, t0, t1),

            MotionPresets.PanLeft => BuildPanFrames(
                baseScaleX * panScaleFactor, baseScaleY * panScaleFactor,
                basePosX + panTravel, basePosY,
                basePosX - panTravel, basePosY, t0, t1),

            MotionPresets.PanRight => BuildPanFrames(
                baseScaleX * panScaleFactor, baseScaleY * panScaleFactor,
                basePosX - panTravel, basePosY,
                basePosX + panTravel, basePosY, t0, t1),

            MotionPresets.PanUp => BuildPanFrames(
                baseScaleX * panScaleFactor, baseScaleY * panScaleFactor,
                basePosX, basePosY - panTravel,
                basePosX, basePosY + panTravel, t0, t1),

            MotionPresets.PanDown => BuildPanFrames(
                baseScaleX * panScaleFactor, baseScaleY * panScaleFactor,
                basePosX, basePosY + panTravel,
                basePosX, basePosY - panTravel, t0, t1),

            MotionPresets.ZoomInPanLeft => BuildZoomPanFrames(
                baseScaleX, baseScaleY, baseScaleX * maxZoom, baseScaleY * maxZoom,
                basePosX + panTravel, basePosY,
                basePosX - panTravel, basePosY, t0, t1),

            MotionPresets.ZoomInPanRight => BuildZoomPanFrames(
                baseScaleX, baseScaleY, baseScaleX * maxZoom, baseScaleY * maxZoom,
                basePosX - panTravel, basePosY,
                basePosX + panTravel, basePosY, t0, t1),

            _ => null
        };
    }

    private static KeyframeBatch BuildZoomFrames(
        double sx0, double sy0, double sx1, double sy1,
        double px0, double py0, double px1, double py1,
        double t0, double t1)
    {
        var props = new[] { "scale_x", "scale_y", "scale_x", "scale_y", "position_x", "position_y", "position_x", "position_y" };
        var times = new[] { t0, t0, t1, t1, t0, t0, t1, t1 };
        var values = new[]
        {
            Fmt(sx0), Fmt(sy0), Fmt(sx1), Fmt(sy1),
            Fmt(px0), Fmt(py0), Fmt(px1), Fmt(py1)
        };
        return new KeyframeBatch(props, times, values);
    }

    private static KeyframeBatch BuildPanFrames(
        double sx, double sy,
        double px0, double py0, double px1, double py1,
        double t0, double t1)
    {
        var props = new[] { "scale_x", "scale_y", "scale_x", "scale_y", "position_x", "position_y", "position_x", "position_y" };
        var times = new[] { t0, t0, t1, t1, t0, t0, t1, t1 };
        var values = new[]
        {
            Fmt(sx), Fmt(sy), Fmt(sx), Fmt(sy),
            Fmt(px0), Fmt(py0), Fmt(px1), Fmt(py1)
        };
        return new KeyframeBatch(props, times, values);
    }

    private static KeyframeBatch BuildZoomPanFrames(
        double sx0, double sy0, double sx1, double sy1,
        double px0, double py0, double px1, double py1,
        double t0, double t1)
        => BuildZoomFrames(sx0, sy0, sx1, sy1, px0, py0, px1, py1, t0, t1);

    private static string Fmt(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
}
