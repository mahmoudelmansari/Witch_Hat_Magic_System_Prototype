using System.Collections.Generic;

/// <summary>
/// A node in the seal tree. A leaf seal holds one element (and optional modifiers);
/// a compound seal holds child seals instead. Fires once every circle is closed.
/// </summary>
public class Seal
{
    public Stroke outerCircle;              // The enclosing circle (open arc or closed)
    public Stroke element;                  // Leaf only: the shape inside (triangle, square...)
    public List<Stroke> modifiers = new();  // Keystones like W that alter behavior
    public List<Seal> children = new();     // Compound only: nested seals
    public bool hasFired = false;

    public bool IsLeaf => children.Count == 0;

    public bool IsValid()
    {
        if (outerCircle == null || !outerCircle.isClosed) return false;

        // Leaf: needs an element inside
        if (IsLeaf) return element != null;

        // Compound: no direct element, and every child must be valid
        if (element != null) return false;
        foreach (var child in children)
            if (!child.IsValid()) return false;
        return true;
    }

    public bool IsFullyClosed()
    {
        if (!outerCircle.isClosed) return false;
        foreach (var child in children)
            if (!child.IsFullyClosed()) return false;
        return true;
    }

    public List<ShapeLabel> GetChildElements()
    {
        var result = new List<ShapeLabel>();
        foreach (var child in children)
            if (child.element != null)
                result.Add(child.element.label);
        return result;
    }

    public bool HasModifier(ShapeLabel label)
    {
        foreach (var m in modifiers)
            if (m.label == label) return true;
        return false;
    }

    public Seal FindChildByElement(ShapeLabel label)
    {
        foreach (var child in children)
            if (child.element != null && child.element.label == label)
                return child;
        return null;
    }
}
