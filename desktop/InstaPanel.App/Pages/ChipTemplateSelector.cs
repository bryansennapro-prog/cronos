using InstaPanel.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InstaPanel.App.Pages;

/// <summary>Cartão compacto no mês e detalhado na semana.</summary>
public sealed partial class ChipTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Month { get; set; }

    public DataTemplate? Week { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is PostItemViewModel { Detailed: true } ? Week : Month;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
