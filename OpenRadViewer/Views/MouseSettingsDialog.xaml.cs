// @purpose: UI dialog for customizing mouse buttons, wheel actions, and modifier keys, persisting settings to mousesettings.json.
// @depends: OpenRadViewer.Core.Config, OpenRadViewer.ViewModels
// @exports: MouseSettingsDialog

using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using OpenRadViewer.Core.Config;
using OpenRadViewer.ViewModels;

namespace OpenRadViewer.Views;

public partial class MouseSettingsDialog : Window
{
    public static MouseToolAction[] AllActions { get; } = Enum.GetValues<MouseToolAction>();

    public ObservableCollection<MouseBindingRule> EditableBindings { get; set; } = new();

    private readonly MainViewModel _mainVm;

    public MouseSettingsDialog(MainViewModel mainVm)
    {
        InitializeComponent();
        _mainVm = mainVm;

        LoadCurrentBindings();
        BindingsGrid.ItemsSource = EditableBindings;
    }

    private void LoadCurrentBindings()
    {
        EditableBindings.Clear();

        // Ensure all triggers have an entry
        var allTriggers = Enum.GetValues<MouseInputTrigger>();
        foreach (var trigger in allTriggers)
        {
            var existing = _mainVm.MouseConfig.Bindings.FirstOrDefault(b => b.Trigger == trigger);
            EditableBindings.Add(new MouseBindingRule
            {
                Trigger = trigger,
                Action = existing?.Action ?? MouseToolAction.None
            });
        }
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var defaults = MouseSettings.CreateDefault();
        foreach (var row in EditableBindings)
        {
            row.Action = defaults.GetActionFor(row.Trigger);
        }
        BindingsGrid.Items.Refresh();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _mainVm.MouseConfig.Bindings = EditableBindings.ToList();
        
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mousesettings.json");
        _mainVm.MouseConfig.SaveToFile(path);

        _mainVm.LeftTool = _mainVm.MouseConfig.GetActionFor(MouseInputTrigger.LeftDrag);
        _mainVm.RightTool = _mainVm.MouseConfig.GetActionFor(MouseInputTrigger.RightDrag);
        _mainVm.MiddleTool = _mainVm.MouseConfig.GetActionFor(MouseInputTrigger.MiddleDrag);

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
