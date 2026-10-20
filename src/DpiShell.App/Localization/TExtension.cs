using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace DpiShell.App;

/// <summary>Источник строк для привязок: индексатор по ключу, при смене языка — «все индексы изменились».</summary>
public sealed class LangSource : INotifyPropertyChanged
{
    public static LangSource Instance { get; } = new();
    LangSource() { }

    public string this[string key] => L.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}

/// <summary>
/// Строка интерфейса в разметке: <c>Text="{l:T home.title}"</c> (xmlns:l="clr-namespace:DpiShell.App").
/// Работает в Text, Content, Header, ToolTip, Run.Text, в стилях (Setter.Value) и шаблонах: это привязка к
/// <see cref="LangSource"/>, поэтому при смене языка текст меняется сразу. У свойства, к которому нельзя привязаться, — просто строка.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var target = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
        // Обычное CLR-свойство (не DependencyProperty и не Setter) привязку не примет — текст на момент загрузки.
        if (target is { TargetProperty: not null and not DependencyProperty } && target.TargetObject is not Setter
            && target.TargetObject is not null && target.TargetObject.GetType().FullName != "System.Windows.SharedDp")
            return L.T(Key);
        var binding = new Binding($"[{Key}]") { Source = LangSource.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
