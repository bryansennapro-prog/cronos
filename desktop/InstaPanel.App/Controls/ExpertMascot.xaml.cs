using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace InstaPanel.App.Controls;

public enum MascotMood
{
    Happy,
    Neutral,
    Worried,
    Thinking,
}

/// <summary>
/// Tico, o mascote do Especialista. Flutua de leve, pisca, acena e muda a boca conforme o humor.
/// Sem animações quando o Windows está com "Efeitos de animação" desligado.
/// </summary>
public sealed partial class ExpertMascot : UserControl
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(MascotMood), typeof(ExpertMascot),
        new PropertyMetadata(MascotMood.Happy, (d, _) => ((ExpertMascot)d).ApplyMood()));

    private static readonly bool AnimationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private readonly DispatcherTimer _blinkTimer = new();
    private readonly Random _random = new();
    private Storyboard? _bob;
    private Storyboard? _shake;

    public ExpertMascot()
    {
        InitializeComponent();
        _blinkTimer.Tick += (_, _) => Blink();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MascotMood Mood
    {
        get => (MascotMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>Acena com o braço direito (ex.: ao trazer uma dica nova).</summary>
    public void Wave()
    {
        if (!AnimationsEnabled) return;
        var wave = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        foreach (var (ms, angle) in new[] { (0, -28.0), (150, -62.0), (300, -36.0), (450, -62.0), (600, -36.0), (800, -28.0) })
            wave.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(ms), Value = angle });
        Play(wave, ArmWave, nameof(RotateTransform.Angle));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyMood();
        if (!AnimationsEnabled) return;

        var bob = new DoubleAnimation
        {
            From = 0,
            To = -4,
            Duration = TimeSpan.FromSeconds(1.6),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        _bob = Play(bob, Bob, nameof(TranslateTransform.Y));
        ScheduleBlink();
        Wave();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _blinkTimer.Stop();
        _bob?.Stop();
        _shake?.Stop();
    }

    private void ApplyMood()
    {
        var mood = Mood;
        MouthHappy.Visibility = mood == MascotMood.Happy ? Visibility.Visible : Visibility.Collapsed;
        MouthNeutral.Visibility = mood == MascotMood.Neutral ? Visibility.Visible : Visibility.Collapsed;
        MouthWorried.Visibility = mood == MascotMood.Worried ? Visibility.Visible : Visibility.Collapsed;
        MouthThinking.Visibility = mood == MascotMood.Thinking ? Visibility.Visible : Visibility.Collapsed;

        // Pensando: olhos para cima e os sinos tremem, como um despertador "trabalhando".
        EyesTransform.TranslateY = mood == MascotMood.Thinking ? -3 : 0;
        EyesTransform.TranslateX = mood == MascotMood.Thinking ? 2 : 0;

        _shake?.Stop();
        _shake = null;
        BellShake.Angle = 0;
        if (mood == MascotMood.Thinking && AnimationsEnabled && IsLoaded)
        {
            var shake = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, EnableDependentAnimation = true };
            foreach (var (ms, angle) in new[] { (0, 0.0), (80, -5.0), (160, 5.0), (240, -5.0), (320, 0.0), (1400, 0.0) })
                shake.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(ms), Value = angle });
            _shake = Play(shake, BellShake, nameof(RotateTransform.Angle));
        }
    }

    private void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(_random.Next(2800, 5200));
        _blinkTimer.Start();
    }

    private void Blink()
    {
        _blinkTimer.Stop();
        var blink = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        blink.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1 });
        blink.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(70), Value = 0.1 });
        blink.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(150), Value = 1 });
        Play(blink, EyesTransform, nameof(CompositeTransform.ScaleY));
        ScheduleBlink();
    }

    private static Storyboard Play(Timeline animation, DependencyObject target, string property)
    {
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
        storyboard.Begin();
        return storyboard;
    }
}
