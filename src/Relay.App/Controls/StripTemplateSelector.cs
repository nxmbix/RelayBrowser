using System.Windows;
using System.Windows.Controls;
using Relay.Browser;

namespace Relay.App.Controls;

/// <summary>Chooses the tab or group-header template for items in the tab strip.</summary>
public sealed class StripTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TabTemplate { get; set; }
    public DataTemplate? GroupTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is TabGroup ? GroupTemplate : TabTemplate;
}
