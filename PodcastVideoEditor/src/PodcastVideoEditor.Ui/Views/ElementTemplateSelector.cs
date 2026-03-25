using PodcastVideoEditor.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Selects DataTemplate for canvas elements based on element type.
/// TextOverlay shows content; Visualizer uses bitmap; others use default.
/// </summary>
public class ElementTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? VisualizerTemplate { get; set; }
    public DataTemplate? TextOverlayTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }
    public DataTemplate? LogoTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            VisualizerElement => VisualizerTemplate ?? DefaultTemplate,
            TextOverlayElement => TextOverlayTemplate ?? DefaultTemplate,
            ImageElement => ImageTemplate ?? DefaultTemplate,
            LogoElement => LogoTemplate ?? DefaultTemplate,
            _ => DefaultTemplate
        };
    }
}
