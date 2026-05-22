using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Text;
using ProGPU.Vector;
using ProGPU.Layout;

namespace ProGPU.Scene;

public class Visual
{
    private Vector2 _offset;
    private Vector2 _size;
    private float _opacity = 1.0f;
    private Matrix4x4 _transform = Matrix4x4.Identity;
    private bool _isDirty = true;
    private bool _cacheAsLayer;

    public ContainerVisual? Parent { get; internal set; }

    public Vector2 Offset
    {
        get => _offset;
        set
        {
            if (_offset != value)
            {
                _offset = value;
                Invalidate();
            }
        }
    }

    public Vector2 Size
    {
        get => _size;
        set
        {
            if (_size != value)
            {
                _size = value;
                Invalidate();
            }
        }
    }

    public float Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity != value)
            {
                _opacity = value;
                Invalidate();
            }
        }
    }

    public Matrix4x4 Transform
    {
        get => _transform;
        set
        {
            if (_transform != value)
            {
                _transform = value;
                Invalidate();
            }
        }
    }

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            _isDirty = value;
            if (_isDirty && Parent != null)
            {
                Parent.Invalidate();
            }
        }
    }

    public bool CacheAsLayer
    {
        get => _cacheAsLayer;
        set
        {
            if (_cacheAsLayer != value)
            {
                _cacheAsLayer = value;
                Invalidate();
            }
        }
    }

    // Composition layer texture view
    public GpuTexture? LayerTexture { get; internal set; }

    public Rect? ClipBounds { get; set; }

    public void Invalidate()
    {
        IsDirty = true;
    }

    public virtual void OnRender(DrawingContext context)
    {
        // Base visual does not record operations directly
    }

    public Matrix4x4 GetLocalTransform()
    {
        var translation = Matrix4x4.CreateTranslation(Offset.X, Offset.Y, 0f);
        return Transform * translation;
    }
}

public class ContainerVisual : Visual
{
    private readonly List<Visual> _children = new();

    public IReadOnlyList<Visual> Children => _children;

    public void AddChild(Visual child)
    {
        if (child.Parent != null)
        {
            child.Parent.RemoveChild(child);
        }

        child.Parent = this;
        _children.Add(child);
        Invalidate();
    }

    public void RemoveChild(Visual child)
    {
        if (_children.Remove(child))
        {
            child.Parent = null;
            Invalidate();
        }
    }

    public void ClearChildren()
    {
        foreach (var child in _children)
        {
            child.Parent = null;
        }
        _children.Clear();
        Invalidate();
    }
}

public class DrawingVisual : Visual
{
    public DrawingContext Context { get; } = new();

    public override void OnRender(DrawingContext context)
    {
        // Copy recorded commands
        foreach (var cmd in Context.Commands)
        {
            context.Commands.Add(cmd);
        }
    }
}

public class TextVisual : LayoutNode
{
    private string _text = string.Empty;
    private TtfFont? _font;
    private float _fontSize = 12f;
    private Brush? _brush;
    private TextAlignment _alignment = TextAlignment.Left;
    private TextLayout? _layout;

    public string Text
    {
        get => _text;
        set
        {
            if (_text != value)
            {
                _text = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    public TtfFont? Font
    {
        get => _font;
        set
        {
            if (_font != value)
            {
                _font = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    public float FontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize != value)
            {
                _fontSize = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    public Brush? Brush
    {
        get => _brush;
        set
        {
            if (_brush != value)
            {
                _brush = value;
                Invalidate();
            }
        }
    }

    public TextAlignment Alignment
    {
        get => _alignment;
        set
        {
            if (_alignment != value)
            {
                _alignment = value;
                _layout = null;
                Invalidate();
            }
        }
    }

    public TextLayout? GetOrUpdateLayout(GlyphAtlas atlas)
    {
        if (Font == null) return null;

        if (_layout == null)
        {
            _layout = new TextLayout(Text, Font, FontSize, Size.X, Alignment, atlas);
            Size = _layout.MeasuredSize;
        }
        else if (!_layout.HasTextures)
        {
            _layout.GenerateLayout(atlas);
        }
        return _layout;
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (string.IsNullOrEmpty(Text) || Font == null)
            return Vector2.Zero;

        float maxWidth = WidthConstraint ?? availableSize.X;
        var tempLayout = new TextLayout(Text, Font, FontSize, maxWidth, Alignment, null);
        return tempLayout.MeasuredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        _layout = null; // force regeneration with new size and actual atlas on next render
    }

    public override void OnRender(DrawingContext context)
    {
        if (string.IsNullOrEmpty(Text) || Font == null || Brush == null) return;
        
        // Add single drawing run command; compositor will dynamically compile coordinates
        context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = Text,
            Font = Font,
            FontSize = FontSize,
            Brush = Brush,
            Position = Vector2.Zero,
            Rect = new Rect(Vector2.Zero, Size)
        });
    }
}
