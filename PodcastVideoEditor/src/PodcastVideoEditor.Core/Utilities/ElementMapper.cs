#nullable enable
using PodcastVideoEditor.Core.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PodcastVideoEditor.Core.Utilities;

/// <summary>
/// Maps between Element (database model) and CanvasElement (UI model).
/// Handles serialization of type-specific properties to/from JSON.
/// </summary>
public static class ElementMapper
{
    /// <summary>
    /// Convert a CanvasElement (UI) to an Element (DB) for persistence.
    /// </summary>
    public static Element ToElement(CanvasElement canvas, string projectId)
    {
        var props = SerializeTypeProperties(canvas);

        return new Element
        {
            Id = canvas.Id,
            ProjectId = projectId,
            Type = canvas.Type.ToString(),
            X = canvas.X,
            Y = canvas.Y,
            Width = canvas.Width,
            Height = canvas.Height,
            ZIndex = canvas.ZIndex,
            Opacity = canvas is LogoElement logo ? logo.Opacity
                    : canvas is ImageElement img ? img.Opacity
                    : 1.0,
            Rotation = canvas.Rotation,
            IsVisible = canvas.IsVisible,
            SegmentId = canvas.SegmentId,
            PropertiesJson = props,
            CreatedAt = canvas.CreatedAt
        };
    }

    /// <summary>
    /// Convert an Element (DB) to a CanvasElement (UI) for display.
    /// </summary>
    public static CanvasElement? ToCanvasElement(Element element)
    {
        var canvas = CreateCanvasElementByType(element.Type);
        if (canvas == null)
            return null;

        canvas.Id = element.Id;
        canvas.Name = element.Type;
        canvas.X = element.X;
        canvas.Y = element.Y;
        canvas.Width = element.Width;
        canvas.Height = element.Height;
        canvas.ZIndex = element.ZIndex;
        canvas.Rotation = element.Rotation;
        canvas.IsVisible = element.IsVisible;
        canvas.SegmentId = element.SegmentId;
        canvas.CreatedAt = element.CreatedAt;

        DeserializeTypeProperties(canvas, element.PropertiesJson);

        return canvas;
    }

    /// <summary>
    /// Bulk convert CanvasElements to Elements for saving.
    /// </summary>
    public static List<Element> ToElements(IEnumerable<CanvasElement> canvasElements, string projectId)
    {
        var result = new List<Element>();
        foreach (var ce in canvasElements)
            result.Add(ToElement(ce, projectId));
        return result;
    }

    /// <summary>
    /// Bulk convert Elements to CanvasElements for loading.
    /// </summary>
    public static List<CanvasElement> ToCanvasElements(IEnumerable<Element> elements)
    {
        var result = new List<CanvasElement>();
        foreach (var el in elements)
        {
            var ce = ToCanvasElement(el);
            if (ce != null)
                result.Add(ce);
        }
        return result;
    }

    private static CanvasElement? CreateCanvasElementByType(string type)
    {
        return type switch
        {
            "Title" => new TitleElement(),
            "Logo" => new LogoElement(),
            "Visualizer" => new VisualizerElement(),
            "Image" => new ImageElement(),
            "Text" => new TextElement(),
            _ => null
        };
    }

    private static string SerializeTypeProperties(CanvasElement canvas)
    {
        var dict = new Dictionary<string, object>();
        dict["Name"] = canvas.Name ?? string.Empty;

        switch (canvas)
        {
            case TitleElement t:
                dict["Text"] = t.Text;
                dict["FontFamily"] = t.FontFamily;
                dict["FontSize"] = t.FontSize;
                dict["ColorHex"] = t.ColorHex;
                dict["IsBold"] = t.IsBold;
                dict["IsItalic"] = t.IsItalic;
                dict["Alignment"] = t.Alignment.ToString();
                break;
            case LogoElement l:
                dict["ImagePath"] = l.ImagePath;
                dict["Opacity"] = l.Opacity;
                dict["ScaleMode"] = l.ScaleMode.ToString();
                break;
            case VisualizerElement v:
                dict["ColorPalette"] = v.ColorPalette.ToString();
                dict["BandCount"] = v.BandCount;
                dict["Style"] = v.Style.ToString();
                break;
            case ImageElement i:
                dict["FilePath"] = i.FilePath;
                dict["Opacity"] = i.Opacity;
                dict["ScaleMode"] = i.ScaleMode.ToString();
                break;
            case TextElement te:
                dict["Content"] = te.Content;
                dict["FontFamily"] = te.FontFamily;
                dict["FontSize"] = te.FontSize;
                dict["ColorHex"] = te.ColorHex;
                dict["IsBold"] = te.IsBold;
                dict["IsItalic"] = te.IsItalic;
                dict["Alignment"] = te.Alignment.ToString();
                break;
        }

        return JsonSerializer.Serialize(dict);
    }

    private static void DeserializeTypeProperties(CanvasElement canvas, string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return;

        Dictionary<string, JsonElement>? dict;
        try
        {
            dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch
        {
            return;
        }

        if (dict == null)
            return;

        if (dict.TryGetValue("Name", out var nameEl))
            canvas.Name = nameEl.GetString() ?? canvas.Type.ToString();

        switch (canvas)
        {
            case TitleElement t:
                if (dict.TryGetValue("Text", out var text)) t.Text = text.GetString() ?? "Title";
                if (dict.TryGetValue("FontFamily", out var ff)) t.FontFamily = ff.GetString() ?? "Arial";
                if (dict.TryGetValue("FontSize", out var fs)) t.FontSize = fs.GetDouble();
                if (dict.TryGetValue("ColorHex", out var ch)) t.ColorHex = ch.GetString() ?? "#FFFFFF";
                if (dict.TryGetValue("IsBold", out var ib)) t.IsBold = ib.GetBoolean();
                if (dict.TryGetValue("IsItalic", out var ii)) t.IsItalic = ii.GetBoolean();
                if (dict.TryGetValue("Alignment", out var al))
                    t.Alignment = Enum.TryParse<TextAlignment>(al.GetString(), out var alignVal) ? alignVal : TextAlignment.Center;
                break;
            case LogoElement l:
                if (dict.TryGetValue("ImagePath", out var ip)) l.ImagePath = ip.GetString() ?? string.Empty;
                if (dict.TryGetValue("Opacity", out var lo)) l.Opacity = lo.GetDouble();
                if (dict.TryGetValue("ScaleMode", out var lsm))
                    l.ScaleMode = Enum.TryParse<ScaleMode>(lsm.GetString(), out var lsmVal) ? lsmVal : ScaleMode.Fit;
                break;
            case VisualizerElement v:
                if (dict.TryGetValue("ColorPalette", out var cp))
                    v.ColorPalette = Enum.TryParse<ColorPalette>(cp.GetString(), out var cpVal) ? cpVal : ColorPalette.Rainbow;
                if (dict.TryGetValue("BandCount", out var bc)) v.BandCount = bc.GetInt32();
                if (dict.TryGetValue("Style", out var vs))
                    v.Style = Enum.TryParse<VisualizerStyle>(vs.GetString(), out var vsVal) ? vsVal : VisualizerStyle.Bars;
                break;
            case ImageElement i:
                if (dict.TryGetValue("FilePath", out var fp)) i.FilePath = fp.GetString() ?? string.Empty;
                if (dict.TryGetValue("Opacity", out var io)) i.Opacity = io.GetDouble();
                if (dict.TryGetValue("ScaleMode", out var ism))
                    i.ScaleMode = Enum.TryParse<ScaleMode>(ism.GetString(), out var ismVal) ? ismVal : ScaleMode.Fill;
                break;
            case TextElement te:
                if (dict.TryGetValue("Content", out var tc)) te.Content = tc.GetString() ?? "Text";
                if (dict.TryGetValue("FontFamily", out var tff)) te.FontFamily = tff.GetString() ?? "Arial";
                if (dict.TryGetValue("FontSize", out var tfs)) te.FontSize = tfs.GetDouble();
                if (dict.TryGetValue("ColorHex", out var tch)) te.ColorHex = tch.GetString() ?? "#FFFFFF";
                if (dict.TryGetValue("IsBold", out var tib)) te.IsBold = tib.GetBoolean();
                if (dict.TryGetValue("IsItalic", out var tii)) te.IsItalic = tii.GetBoolean();
                if (dict.TryGetValue("Alignment", out var tal))
                    te.Alignment = Enum.TryParse<TextAlignment>(tal.GetString(), out var talVal) ? talVal : TextAlignment.Center;
                break;
        }
    }
}
