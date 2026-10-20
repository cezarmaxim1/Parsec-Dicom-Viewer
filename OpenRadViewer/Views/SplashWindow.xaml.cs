using System.Windows;
using System.Windows.Media.Animation;

namespace OpenRadViewer.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        MouseDown += (s, e) => { Visibility = Visibility.Collapsed; try { Close(); } catch { } };
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Display for 900ms then smoothly fade out over 350ms and close
        var fadeOutAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            BeginTime = TimeSpan.FromMilliseconds(900),
            Duration = new Duration(TimeSpan.FromMilliseconds(350)),
            FillBehavior = FillBehavior.HoldEnd
        };

        fadeOutAnim.Completed += (s, args) =>
        {
            Visibility = Visibility.Collapsed;
            try { Close(); } catch { }
        };

        BeginAnimation(OpacityProperty, fadeOutAnim);
    }
}
