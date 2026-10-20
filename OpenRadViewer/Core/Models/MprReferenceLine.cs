// @purpose: Model for multi-planar reconstruction (MPR) intersecting crosshair reference lines and rotation handles.
// @depends: System.Windows, System.Windows.Media, OpenRadViewer.Core.Services
// @exports: MprReferenceLine

using System.Windows;
using System.Windows.Media;
using OpenRadViewer.Core.Services;

namespace OpenRadViewer.Core.Models;

public class MprReferenceLine
{
    public OrthogonalPlane TargetPlane { get; set; }
    public Color Color { get; set; }
    public Point StartPoint { get; set; } // in image pixel coordinates
    public Point EndPoint { get; set; }   // in image pixel coordinates
    public Point HandlePoint { get; set; } // position of the first circular handle dot
    public Point HandlePoint2 { get; set; } // position of the opposite circular handle dot
    public Point CenterPoint { get; set; } // intersection or center of rotation
}
