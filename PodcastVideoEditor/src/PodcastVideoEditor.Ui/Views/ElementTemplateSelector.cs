using PodcastVideoEditor.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views;

/// <summary>
/// Selects DataTemplate for canvas elements based on element type.
/// Title/Text show their content; Visualizer uses bitmap; others use default.
/// </summary>
public class ElementTemplateSelector : DataTemplateSelector
{
    public DataTemplate? DefaultTemplate { get; set; }
    public DataTemplate? VisualizerTemplate { get; set; }
    public DataTemplate? TitleTemplate { get; set; }
    public DataTemplate? TextTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            VisualizerElement => VisualizerTemplate ?? DefaultTemplate,
            TitleElement => TitleTemplate ?? DefaultTemplate,
            TextElement => TextTemplate ?? DefaultTemplate,
            _ => DefaultTemplate
        };
    }
}
