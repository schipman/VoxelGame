using Microsoft.Xna.Framework;

namespace VoxelGame.UI;

/// <summary>
/// Lays its children out in a row or a column, one after the other. It sizes itself to fit them, and can draw a
/// window background behind them, so it is also the usual way to make a panel.
/// </summary>
public sealed class StackPanel : Widget
{
    /// <summary>Children go top to bottom if true, left to right if false.</summary>
    public bool Vertical = true;
    public int Spacing = 4;
    public int Padding = 0;

    /// <summary>Draw a window background (outline, fill, bevel) behind the children.</summary>
    public bool Background;

    /// <summary>For a vertical stack: centre each child sideways instead of lining them up on the left. (Horizontal: centre vertically.)</summary>
    public bool CenterChildren;

    public override bool HitTestVisible => Background;   // an invisible stack shouldn't swallow clicks meant for what's behind it

    protected override void Arrange()
    {
        int main = Padding, cross = 0;
        foreach (var child in Children)
        {
            if (!child.Visible) continue;
            main += (Vertical ? child.Size.Y : child.Size.X) + Spacing;
            cross = Math.Max(cross, Vertical ? child.Size.X : child.Size.Y);
        }
        main = Math.Max(main - Spacing, Padding) + Padding;   // no spacing after the last child

        Size = Vertical ? new Point(cross + 2 * Padding, main) : new Point(main, cross + 2 * Padding);

        int cursor = Padding;
        foreach (var child in Children)
        {
            if (!child.Visible) continue;
            child.Anchor = Anchor.None;

            int sideways = Padding + (CenterChildren ? (cross - (Vertical ? child.Size.X : child.Size.Y)) / 2 : 0);
            child.Position = Vertical ? new Point(sideways, cursor) : new Point(cursor, sideways);
            cursor += (Vertical ? child.Size.Y : child.Size.X) + Spacing;
        }
    }

    protected override void DrawSelf(UiRenderer renderer)
    {
        if (Background) renderer.Window(Bounds);
    }
}

/// <summary>Lays its children out in rows and columns of equal-sized cells, left to right then top to bottom.</summary>
public sealed class GridPanel : Widget
{
    public int Columns = 9;
    public Point CellSize = new(18, 18);
    public int Spacing = 2;

    protected override void Arrange()
    {
        int index = 0;
        foreach (var child in Children)
        {
            if (!child.Visible) continue;
            child.Anchor = Anchor.None;
            child.Position = new Point(index % Columns * (CellSize.X + Spacing), index / Columns * (CellSize.Y + Spacing));
            index++;
        }

        int rows = (index + Columns - 1) / Columns;
        Size = new Point(Columns * CellSize.X + (Columns - 1) * Spacing, Math.Max(rows * (CellSize.Y + Spacing) - Spacing, 0));
    }

    public override bool HitTestVisible => false;
}
