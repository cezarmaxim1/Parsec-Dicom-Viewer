// @purpose: Model for quick-reference radiological guides, charts, tables, and classification diagrams in ExtraTools.
// @depends: System
// @exports: ReferenceGuideItem

namespace OpenRadViewer.Core.Models;

public class ReferenceGuideItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "Classification"; // "Classification" or "Table"
    public string ImagePath { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; } = false;
    public DateTime DateAdded { get; set; } = DateTime.Now;
}
