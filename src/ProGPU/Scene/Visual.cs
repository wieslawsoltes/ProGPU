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
    private Vector3 _scale = Vector3.One;
    private float _rotation = 0f;
    private Vector3 _centerPoint = Vector3.Zero;
    private readonly Dictionary<string, CompositionAnimation> _activeAnimations = new();

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

    public Vector3 Scale
    {
        get => _scale;
        set
        {
            if (_scale != value)
            {
                _scale = value;
                Invalidate();
            }
        }
    }

    public float Rotation
    {
        get => _rotation;
        set
        {
            if (_rotation != value)
            {
                _rotation = value;
                Invalidate();
            }
        }
    }

    public Vector3 CenterPoint
    {
        get => _centerPoint;
        set
        {
            if (_centerPoint != value)
            {
                _centerPoint = value;
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
        var translationToOrigin = Matrix4x4.CreateTranslation(-CenterPoint.X, -CenterPoint.Y, -CenterPoint.Z);
        var scaleMatrix = Matrix4x4.CreateScale(Scale);
        var rotationMatrix = Matrix4x4.CreateRotationZ(Rotation);
        var translationToOffsetAndRestoreCenter = Matrix4x4.CreateTranslation(Offset.X + CenterPoint.X, Offset.Y + CenterPoint.Y, CenterPoint.Z);

        var modelMatrix = translationToOrigin * scaleMatrix * rotationMatrix * translationToOffsetAndRestoreCenter;
        return Transform * modelMatrix;
    }

    public void StartAnimation(string propertyName, CompositionAnimation animation)
    {
        _activeAnimations[propertyName] = animation;
        Invalidate();
    }

    public void StopAnimation(string propertyName)
    {
        if (_activeAnimations.Remove(propertyName))
        {
            Invalidate();
        }
    }

    public void UpdateAnimations(float elapsedSeconds)
    {
        TickAnimations(elapsedSeconds);

        if (this is ContainerVisual container)
        {
            var children = container.Children;
            for (int i = 0; i < children.Count; i++)
            {
                children[i].UpdateAnimations(elapsedSeconds);
            }
        }
    }

    public void TickAnimations(float elapsedSeconds)
    {
        if (_activeAnimations.Count == 0) return;

        bool changed = false;

        foreach (var kvp in _activeAnimations)
        {
            var propertyName = kvp.Key;
            var animation = kvp.Value;

            animation.Tick(elapsedSeconds);

            var value = animation.CurrentValue;
            if (value == null) continue;

            switch (propertyName.ToLowerInvariant())
            {
                case "opacity":
                    if (value is float fOpacity)
                    {
                        if (_opacity != fOpacity)
                        {
                            _opacity = fOpacity;
                            changed = true;
                        }
                    }
                    break;

                case "rotation":
                    if (value is float fRotation)
                    {
                        if (_rotation != fRotation)
                        {
                            _rotation = fRotation;
                            changed = true;
                        }
                    }
                    break;

                case "offset":
                    if (value is Vector2 vOffset)
                    {
                        if (_offset != vOffset)
                        {
                            _offset = vOffset;
                            changed = true;
                        }
                    }
                    break;

                case "size":
                    if (value is Vector2 vSize)
                    {
                        if (_size != vSize)
                        {
                            _size = vSize;
                            changed = true;
                        }
                    }
                    break;

                case "scale":
                    if (value is Vector3 vScale)
                    {
                        if (_scale != vScale)
                        {
                            _scale = vScale;
                            changed = true;
                        }
                    }
                    else if (value is Vector2 vScale2)
                    {
                        var vScale3 = new Vector3(vScale2, 1.0f);
                        if (_scale != vScale3)
                        {
                            _scale = vScale3;
                            changed = true;
                        }
                    }
                    break;
            }
        }

        if (changed)
        {
            Invalidate();
        }
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

public class TextVisual : ProGPU.WinUI.FrameworkElement
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

    private TtfFont? ResolveFont()
    {
        Visual? p = this;
        while (p != null)
        {
            var prop = p.GetType().GetProperty("Font");
            if (prop != null && prop.GetValue(p) is TtfFont f) return f;
            p = p.Parent;
        }

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in asm)
            {
                var type = assembly.GetType("ProGPU.Samples.Program");
                if (type != null)
                {
                    var method = type.GetMethod("GetFont");
                    if (method != null && method.Invoke(null, null) is TtfFont staticFont)
                    {
                        return staticFont;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public TextLayout? GetOrUpdateLayout(GlyphAtlas atlas)
    {
        var resolvedFont = ResolveFont();
        if (resolvedFont == null) return null;

        if (_layout == null)
        {
            _layout = new TextLayout(Text, resolvedFont, FontSize, Size.X, Alignment, atlas);
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
        var resolvedFont = ResolveFont();
        if (string.IsNullOrEmpty(Text) || resolvedFont == null)
            return Vector2.Zero;

        float maxWidth = WidthConstraint ?? availableSize.X;
        var tempLayout = new TextLayout(Text, resolvedFont, FontSize, maxWidth, Alignment, null);
        return tempLayout.MeasuredSize;
    }

    protected override void ArrangeOverride(Rect arrangeRect)
    {
        Size = new Vector2(arrangeRect.Width, arrangeRect.Height);
        _layout = null; // force regeneration with new size and actual atlas on next render
    }

    public override void OnRender(DrawingContext context)
    {
        var resolvedFont = ResolveFont();
        if (string.IsNullOrEmpty(Text) || resolvedFont == null) return;
        
        var resolvedBrush = Brush ?? ProGPU.WinUI.ThemeManager.GetBrush("TextPrimary");

        // Add single drawing run command; compositor will dynamically compile coordinates
        context.Commands.Add(new RenderCommand
        {
            Type = RenderCommandType.DrawText,
            Text = Text,
            Font = resolvedFont,
            FontSize = FontSize,
            Brush = resolvedBrush,
            Position = Vector2.Zero,
            Rect = new Rect(Vector2.Zero, Size)
        });
    }
}
