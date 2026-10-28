// @purpose: Corner indicator displaying colored dots for Left (Orange), Right (Blue), and Hold Wheel (Green) mouse bindings on toolbar buttons.
// @depends: OpenRadViewer.Core.Config, OpenRadViewer.ViewModels
// @exports: ToolBindingIndicator

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OpenRadViewer.Core.Config;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class ToolBindingIndicator : UserControl
{
    public static readonly DependencyProperty TargetActionProperty =
        DependencyProperty.Register(
            nameof(TargetAction),
            typeof(string),
            typeof(ToolBindingIndicator),
            new PropertyMetadata(string.Empty, OnTargetActionChanged));

    public string TargetAction
    {
        get => (string)GetValue(TargetActionProperty);
        set => SetValue(TargetActionProperty, value);
    }

    private static void OnTargetActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ToolBindingIndicator indicator)
        {
            indicator.UpdateDots();
        }
    }

    public ToolBindingIndicator()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? _subscribedVm;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeToViewModel(DataContext as MainViewModel ?? Application.Current.MainWindow?.DataContext as MainViewModel);
        UpdateDots();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnsubscribeFromViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        SubscribeToViewModel(e.NewValue as MainViewModel ?? Application.Current.MainWindow?.DataContext as MainViewModel);
        UpdateDots();
    }

    private void SubscribeToViewModel(MainViewModel? vm)
    {
        if (_subscribedVm == vm) return;
        UnsubscribeFromViewModel();
        _subscribedVm = vm;
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.LeftTool) or
                              nameof(MainViewModel.RightTool) or
                              nameof(MainViewModel.MiddleTool))
        {
            Dispatcher.InvokeAsync(UpdateDots);
        }
    }

    public void UpdateDots()
    {
        var vm = _subscribedVm ?? DataContext as MainViewModel ?? Application.Current.MainWindow?.DataContext as MainViewModel;
        if (vm == null || string.IsNullOrWhiteSpace(TargetAction))
        {
            DotLeft.Visibility = Visibility.Collapsed;
            DotRight.Visibility = Visibility.Collapsed;
            DotMiddle.Visibility = Visibility.Collapsed;
            return;
        }

        bool IsMatch(MouseToolAction tool)
        {
            if (TargetAction.Equals("Measurement", StringComparison.OrdinalIgnoreCase))
            {
                return tool is MouseToolAction.MeasureLength or
                               MouseToolAction.MeasureEllipse or
                               MouseToolAction.MeasureRectangle or
                               MouseToolAction.MeasureAngle or
                               MouseToolAction.MeasureCobbAngle or
                               MouseToolAction.MeasureInsallSalvati or
                               MouseToolAction.Arrow or
                               MouseToolAction.Pencil or
                               MouseToolAction.TextAnnotation;
            }
            return tool.ToString().Equals(TargetAction, StringComparison.OrdinalIgnoreCase);
        }

        DotLeft.Visibility = IsMatch(vm.LeftTool) ? Visibility.Visible : Visibility.Collapsed;
        DotRight.Visibility = IsMatch(vm.RightTool) ? Visibility.Visible : Visibility.Collapsed;
        DotMiddle.Visibility = IsMatch(vm.MiddleTool) ? Visibility.Visible : Visibility.Collapsed;
    }
}
