using InstaPanel.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InstaPanel.App.Controls;

/// <summary>Data + horário local para agendar. Valida "no futuro" e "até 75 dias" (mesmas regras do Worker).</summary>
public sealed partial class DateTimeField : UserControl
{
    public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(75);
    public static readonly TimeSpan MinAhead = TimeSpan.FromMinutes(2);

    private bool _updating;

    public DateTimeField()
    {
        InitializeComponent();
        DatePicker.MinDate = DateTimeOffset.Now.Date;
        DatePicker.MaxDate = DateTimeOffset.Now.Add(MaxAhead).Date;
        Value = SuggestedDefault();
    }

    public event EventHandler? ValueChanged;

    public bool ShowQuickPicks
    {
        get => QuickPicks.Visibility == Visibility.Visible;
        set => QuickPicks.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Horário escolhido (fuso local).</summary>
    public DateTimeOffset Value
    {
        get
        {
            var date = DatePicker.Date?.Date ?? DateTime.Today;
            var time = TimePicker.SelectedTime ?? TimeSpan.Zero;
            return new DateTimeOffset(date.Add(time), TimeZoneInfo.Local.GetUtcOffset(date.Add(time)));
        }
        set
        {
            _updating = true;
            var local = value.ToLocalTime();
            if (local.Date < DatePicker.MinDate.Date) DatePicker.MinDate = local.Date;
            DatePicker.Date = local.Date;
            TimePicker.SelectedTime = new TimeSpan(local.Hour, local.Minute, 0);
            _updating = false;
            UpdateHint();
        }
    }

    public bool IsValid => Validate() is null;

    /// <summary>Próxima hora cheia que esteja a pelo menos 30 min de agora.</summary>
    public static DateTimeOffset SuggestedDefault()
    {
        var t = DateTimeOffset.Now.AddMinutes(30);
        return new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset).AddHours(1);
    }

    private string? Validate()
    {
        var v = Value;
        if (v < DateTimeOffset.Now.Add(MinAhead)) return "Escolha um horário pelo menos 2 minutos no futuro.";
        if (v > DateTimeOffset.Now.Add(MaxAhead)) return "O agendamento pode ser de até 75 dias.";
        return null;
    }

    private void UpdateHint()
    {
        var error = Validate();
        HintText.Text = error ?? $"Será publicado {Formatting.Friendly(Value)} (horário deste PC), mesmo com o PC desligado.";
        HintText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[error is null ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush"];
    }

    private void Changed()
    {
        if (_updating) return;
        UpdateHint();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args) => Changed();

    private void TimePicker_SelectedTimeChanged(TimePicker sender, TimePickerSelectedValueChangedEventArgs args) => Changed();

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string tag) return;
        var parts = tag.Split('|');
        var day = DateTime.Today.AddDays(int.Parse(parts[0]));
        var at = day.AddHours(int.Parse(parts[1]));
        Value = new DateTimeOffset(at, TimeZoneInfo.Local.GetUtcOffset(at));
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }
}
