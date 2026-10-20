using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenRadViewer.Views;

public partial class GridMatrixPickerControl : UserControl
{
    private const int TotalRows = 5;
    private const int TotalCols = 6;
    private readonly Border[,] _cells = new Border[TotalRows, TotalCols];

    public event Action<int, int>? LayoutSelected;

    public GridMatrixPickerControl()
    {
        InitializeComponent();
        BuildMatrix();
    }

    private void BuildMatrix()
    {
        MatrixGrid.Children.Clear();

        for (int r = 0; r < TotalRows; r++)
        {
            for (int c = 0; c < TotalCols; c++)
            {
                int row = r;
                int col = c;

                var cell = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(36, 36, 36)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(1),
                    Cursor = Cursors.Hand
                };

                cell.MouseEnter += (s, e) => HighlightUpTo(row, col);
                cell.MouseLeftButtonDown += (s, e) =>
                {
                    LayoutSelected?.Invoke(row + 1, col + 1);
                };

                _cells[r, c] = cell;
                MatrixGrid.Children.Add(cell);
            }
        }
    }

    private void HighlightUpTo(int maxRow, int maxCol)
    {
        TxtSelection.Text = $"{maxCol + 1} × {maxRow + 1} Panels";

        var activeBrush = new SolidColorBrush(Color.FromRgb(245, 175, 40)); // Amber
        var inactiveBrush = new SolidColorBrush(Color.FromRgb(36, 36, 36));
        var activeBorder = new SolidColorBrush(Color.FromRgb(255, 210, 80));
        var inactiveBorder = new SolidColorBrush(Color.FromRgb(55, 55, 55));

        for (int r = 0; r < TotalRows; r++)
        {
            for (int c = 0; c < TotalCols; c++)
            {
                if (r <= maxRow && c <= maxCol)
                {
                    _cells[r, c].Background = activeBrush;
                    _cells[r, c].BorderBrush = activeBorder;
                }
                else
                {
                    _cells[r, c].Background = inactiveBrush;
                    _cells[r, c].BorderBrush = inactiveBorder;
                }
            }
        }
    }

    private void OnGridMouseLeave(object sender, MouseEventArgs e)
    {
        HighlightUpTo(0, 0);
    }
}
