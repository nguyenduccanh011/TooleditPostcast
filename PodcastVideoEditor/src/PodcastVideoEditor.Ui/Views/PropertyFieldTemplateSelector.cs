using PodcastVideoEditor.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace PodcastVideoEditor.Ui.Views
{
    /// <summary>
    /// Selects the appropriate DataTemplate for PropertyField by FieldType.
    /// </summary>
    public class PropertyFieldTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? StringTemplate { get; set; }
        public DataTemplate? TextAreaTemplate { get; set; }
        public DataTemplate? IntTemplate { get; set; }
        public DataTemplate? FloatTemplate { get; set; }
        public DataTemplate? ColorTemplate { get; set; }
        public DataTemplate? EnumTemplate { get; set; }
        public DataTemplate? BoolTemplate { get; set; }
        public DataTemplate? SliderTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is not PropertyField field)
                return base.SelectTemplate(item, container);

            return field.FieldType switch
            {
                PropertyFieldType.String => StringTemplate,
                PropertyFieldType.TextArea => TextAreaTemplate,
                PropertyFieldType.Int => IntTemplate,
                PropertyFieldType.Float => FloatTemplate,
                PropertyFieldType.Color => ColorTemplate,
                PropertyFieldType.Enum => EnumTemplate,
                PropertyFieldType.Bool => BoolTemplate,
                PropertyFieldType.Slider => SliderTemplate,
                _ => StringTemplate
            };
        }
    }
}
