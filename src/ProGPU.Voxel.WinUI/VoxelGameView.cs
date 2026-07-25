using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProGPU.Backend;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Vector;
using ProGPU.WinUI.Input;
using Silk.NET.Input;
using Silk.NET.WebGPU;

namespace ProGPU.Voxel.WinUI;

/// <summary>
/// A self-contained first-person voxel game surface. World generation and initial meshing
/// run off the UI thread; frame simulation is owned by the normal visual update phase.
/// </summary>
public sealed class VoxelGameView : Control
{
    private sealed class TransferGeometry
    {
        public int Version = -1;
        public GpuVoxelVertex[] Vertices = Array.Empty<GpuVoxelVertex>();
        public uint[] Indices = Array.Empty<uint>();
    }

    private readonly HashSet<Key> _pressedKeys = new();
    private readonly Dictionary<VoxelChunk, TransferGeometry> _transferGeometry = new();
    private readonly List<VoxelChunkRenderEntry> _renderEntryPool = new();
    private readonly VoxelPlayerController _player = new();
    private readonly VoxelTerrainCompilationPayload _payload = new();
    private readonly Brush _hudBrush = new ThemeResourceBrush("TextPrimary");
    private readonly Brush _accentBrush = new ThemeResourceBrush("SystemAccentColor");
    private readonly Pen _crosshairPen;
    private readonly Pen _focusPen;
    private Task<VoxelWorld>? _worldTask;
    private GpuTexture? _colorTexture;
    private GpuTexture? _msaaColorTexture;
    private GpuTexture? _depthTexture;
    private uint _textureSampleCount;
    private bool _jumpRequested;
    private bool _animateMaterials;
    private float _time;
    private VoxelRaycastHit? _target;
    private Exception? _loadError;

    public VoxelGameView()
    {
        _crosshairPen = new Pen(_hudBrush, 2f);
        _focusPen = new Pen(_accentBrush, 2f);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsTabStop = true;
        Unloaded += (_, _) =>
        {
            DisposeTextures();
            _pressedKeys.Clear();
            RelativePointerCapture.Release(this);
        };
    }

    public VoxelWorld? World { get; private set; }

    public VoxelPlayerController Player => _player;

    public VoxelBlock SelectedBlock { get; private set; } = VoxelBlock.Grass;

    public int RenderDistanceInChunks { get; set; } = 6;

    public float MouseSensitivity { get; set; } = 0.0025f;

    public bool IsMouseLookActive => RelativePointerCapture.IsCaptured(this);

    public bool IsLoading => _worldTask is not null;

    public Exception? LoadError => _loadError;

    public event EventHandler? WorldReady;

    public event EventHandler? SelectedBlockChanged;

    public event EventHandler? MouseLookActiveChanged;

    public void StartNewWorld(int seed = 1337, int chunkRadius = 3)
    {
        if (_worldTask is not null)
        {
            return;
        }

        World = null;
        _loadError = null;
        _transferGeometry.Clear();
        _worldTask = Task.Run(() =>
            VoxelTerrainGenerator.Generate(
                new VoxelTerrainSettings(seed, chunkRadius, BuildMeshes: true)));
        Invalidate();
    }

    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var width = float.IsInfinity(availableSize.X) ? 960f : availableSize.X;
        var height = float.IsInfinity(availableSize.Y) ? 640f : availableSize.Y;
        return new Vector2(width, height);
    }

    protected override void OnUpdateAnimations(float elapsedSeconds)
    {
        base.OnUpdateAnimations(elapsedSeconds);
        CompleteWorldLoadIfReady();
        if (World is null)
        {
            if (_worldTask is not null)
            {
                Invalidate();
            }
            return;
        }

        var input = new VoxelPlayerInput(
            Axis(Key.W, Key.Up, Key.S, Key.Down),
            Axis(Key.D, Key.Right, Key.A, Key.Left),
            Axis(Key.Space, Key.E, Key.ControlLeft, Key.Q),
            _jumpRequested,
            IsPressed(Key.ShiftLeft) || IsPressed(Key.ShiftRight));
        _jumpRequested = false;
        _player.Update(World, input, elapsedSeconds);
        if (_player.Position.Y < -24f)
        {
            SpawnPlayer(World);
        }

        if (_animateMaterials)
        {
            _time += Math.Clamp(elapsedSeconds, 0f, 0.1f);
        }
        UpdateTarget();
        Invalidate();
    }

    public override void OnRender(DrawingContext context)
    {
        if (Size.X <= 0 || Size.Y <= 0)
        {
            return;
        }

        var wgpuContext = GetActiveWgpuContext();
        if (wgpuContext is null || World is null)
        {
            DrawLoadingSurface(context);
            base.OnRender(context);
            return;
        }

        var dpiScale = (float)DisplayScaleResolver.ResolveWindowDisplayScale(wgpuContext.Window);
        var width = (uint)Math.Max(1, Size.X * dpiScale);
        var height = (uint)Math.Max(1, Size.Y * dpiScale);
        var sampleCount = dpiScale >= 1.5f ? 1u : 4u;
        EnsureTextures(wgpuContext, width, height, sampleCount);

        var aspect = Size.X / Math.Max(1f, Size.Y);
        var farPlane = Math.Max(48f, RenderDistanceInChunks * VoxelChunk.Size * 1.45f);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            70f * MathF.PI / 180f,
            aspect,
            0.08f,
            farPlane);
        var view = Matrix4x4.CreateLookAt(
            _player.EyePosition,
            _player.EyePosition + _player.LookDirection,
            Vector3.UnitY);
        var viewProjection = view * projection;

        PreparePayload(viewProjection, farPlane);
        if (_payload.Chunks.Count > 0)
        {
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawExtension,
                ExtensionId = CompositorBuiltInExtensions.VoxelTerrain,
                UseGpuTransforms = true,
                CameraView = view,
                Transform = projection,
                DataParam = _payload
            });
            context.Commands.Add(new RenderCommand
            {
                Type = RenderCommandType.DrawTexture,
                Rect = new Rect(Vector2.Zero, Size),
                Texture = _colorTexture
            });
        }

        DrawHud(context);
        base.OnRender(context);
    }

    public override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (IsEnabled && IsFocused)
        {
            if (e.Key == Key.Escape)
            {
                RelativePointerCapture.Release(this);
                _pressedKeys.Clear();
                e.Handled = true;
                base.OnKeyDown(e);
                return;
            }

            var wasPressed = !_pressedKeys.Add(e.Key);
            if (!wasPressed && e.Key == Key.Space)
            {
                _jumpRequested = true;
            }
            else if (!wasPressed && e.Key == Key.F)
            {
                _player.ToggleFlying();
            }
            else if (!wasPressed && TrySelectBlock(e.Key, out var selected))
            {
                SelectedBlock = selected;
                SelectedBlockChanged?.Invoke(this, EventArgs.Empty);
            }

            if (IsGameKey(e.Key))
            {
                e.Handled = true;
            }
        }
        base.OnKeyDown(e);
    }

    public override void OnKeyUp(KeyRoutedEventArgs e)
    {
        _pressedKeys.Remove(e.Key);
        if (IsGameKey(e.Key))
        {
            e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    public override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        if (IsEnabled)
        {
            InputSystem.SetFocus(this);
            if (!IsMouseLookActive)
            {
                RelativePointerCapture.TryAcquire(
                    this,
                    OnRelativePointerMoved,
                    OnMouseLookActiveChanged);
            }
            else if (e.IsLeftButtonPressed)
            {
                RemoveTargetedBlock();
            }
            else if (e.IsRightButtonPressed)
            {
                PlaceSelectedBlock();
            }
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    public override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        if (IsMouseLookActive)
        {
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsMouseLookActive && e.WheelDelta != 0f)
        {
            CycleSelectedBlock(e.WheelDelta < 0f ? 1 : -1);
            e.Handled = true;
        }
        base.OnPointerWheelChanged(e);
    }

    private void OnRelativePointerMoved(Vector2 delta)
    {
        if (!IsEnabled || !IsMouseLookActive) return;
        _player.AddLook(-delta.X * MouseSensitivity, -delta.Y * MouseSensitivity);
        UpdateTarget();
        Invalidate();
    }

    private void OnMouseLookActiveChanged(bool active)
    {
        if (!active)
        {
            _pressedKeys.Clear();
            _jumpRequested = false;
        }
        MouseLookActiveChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void CompleteWorldLoadIfReady()
    {
        if (_worldTask is null || !_worldTask.IsCompleted)
        {
            return;
        }

        if (_worldTask.IsCompletedSuccessfully)
        {
            World = _worldTask.Result;
            _animateMaterials = World.ContainsBlock(VoxelBlock.Water);
            SpawnPlayer(World);
            WorldReady?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _loadError = _worldTask.Exception?.GetBaseException() ??
                new InvalidOperationException("Voxel world generation was canceled.");
        }
        _worldTask = null;
        Invalidate();
    }

    private void SpawnPlayer(VoxelWorld world)
    {
        var surface = world.FindSurfaceY(0, 0);
        _player.Teleport(new Vector3(0.5f, surface + 1.05f, 0.5f), yaw: 0.65f);
        UpdateTarget();
    }

    private void PreparePayload(Matrix4x4 viewProjection, float farPlane)
    {
        _payload.Chunks.Clear();
        _payload.ColorTexture = _colorTexture;
        _payload.MsaaColorTexture = _msaaColorTexture;
        _payload.DepthTexture = _depthTexture;
        _payload.SampleCount = _textureSampleCount;
        _payload.CameraPosition = _player.EyePosition;
        _payload.Time = _time;
        _payload.FogStart = farPlane * 0.55f;
        _payload.FogEnd = farPlane * 0.92f;
        _payload.HasSelectedBlock = _target.HasValue;
        if (_target is { } target)
        {
            _payload.SelectedBlock = new Vector3(target.Block.X, target.Block.Y, target.Block.Z);
        }

        var maxDistanceSquared = MathF.Pow(RenderDistanceInChunks * VoxelChunk.Size, 2f);
        var renderEntryIndex = 0;
        foreach (var chunk in World!.Chunks)
        {
            if (chunk.IsEmpty)
            {
                continue;
            }

            var origin = chunk.Position.WorldOrigin;
            var center = origin + new Vector3(VoxelChunk.Size * 0.5f);
            if (Vector3.DistanceSquared(center, _player.EyePosition) > maxDistanceSquared)
            {
                continue;
            }

            var maximum = origin + new Vector3(VoxelChunk.Size);
            if (!VoxelFrustum.Intersects(viewProjection, origin, maximum))
            {
                continue;
            }

            var mesh = World.GetOrBuildMesh(chunk);
            if (mesh.Indices.Length == 0)
            {
                continue;
            }

            var transfer = GetTransferGeometry(chunk, mesh);
            VoxelChunkRenderEntry entry;
            if (renderEntryIndex < _renderEntryPool.Count)
            {
                entry = _renderEntryPool[renderEntryIndex];
            }
            else
            {
                entry = new VoxelChunkRenderEntry();
                _renderEntryPool.Add(entry);
            }
            entry.Geometry = transfer;
            entry.GeometryVersion = transfer.Version;
            entry.Vertices = transfer.Vertices;
            entry.Indices = transfer.Indices;
            entry.Origin = origin;
            _payload.Chunks.Add(entry);
            renderEntryIndex++;
        }
    }

    private TransferGeometry GetTransferGeometry(VoxelChunk chunk, VoxelMesh mesh)
    {
        if (!_transferGeometry.TryGetValue(chunk, out var transfer))
        {
            transfer = new TransferGeometry();
            _transferGeometry.Add(chunk, transfer);
        }

        if (transfer.Version == mesh.Version)
        {
            return transfer;
        }

        var vertices = new GpuVoxelVertex[mesh.Vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var source = mesh.Vertices[index];
            vertices[index] = new GpuVoxelVertex
            {
                Position = source.Position,
                TextureCoordinate = source.TextureCoordinate,
                Material = source.Material
            };
        }
        transfer.Vertices = vertices;
        transfer.Indices = mesh.Indices;
        transfer.Version = mesh.Version;
        return transfer;
    }

    private void UpdateTarget()
    {
        _target = World is not null &&
            VoxelRaycaster.TryCast(
                World,
                _player.EyePosition,
                _player.LookDirection,
                7f,
                out var hit)
            ? hit
            : null;
    }

    private void RemoveTargetedBlock()
    {
        if (World is null || _target is not { } target || target.Block.Y <= 0)
        {
            return;
        }
        World.SetBlock(target.Block.X, target.Block.Y, target.Block.Z, VoxelBlock.Air);
        UpdateTarget();
        Invalidate();
    }

    private void PlaceSelectedBlock()
    {
        if (World is null || _target is not { } target)
        {
            return;
        }

        var position = target.Previous;
        if (_player.IntersectsBlock(position.X, position.Y, position.Z))
        {
            return;
        }

        World.SetBlock(position.X, position.Y, position.Z, SelectedBlock);
        _animateMaterials |= SelectedBlock == VoxelBlock.Water;
        UpdateTarget();
        Invalidate();
    }

    private void DrawLoadingSurface(DrawingContext context)
    {
        context.DrawRectangle(
            new ThemeResourceBrush("CardBackground"),
            null,
            new Rect(Vector2.Zero, Size));
        var center = Size * 0.5f;
        context.DrawLine(_focusPen, center - new Vector2(16, 0), center + new Vector2(16, 0));
    }

    private void DrawHud(DrawingContext context)
    {
        var center = Size * 0.5f;
        const float gap = 4f;
        const float length = 10f;
        context.DrawLine(_crosshairPen, center - new Vector2(gap + length, 0), center - new Vector2(gap, 0));
        context.DrawLine(_crosshairPen, center + new Vector2(gap, 0), center + new Vector2(gap + length, 0));
        context.DrawLine(_crosshairPen, center - new Vector2(0, gap + length), center - new Vector2(0, gap));
        context.DrawLine(_crosshairPen, center + new Vector2(0, gap), center + new Vector2(0, gap + length));

        if (IsFocused)
        {
            context.DrawRectangle(
                null,
                _focusPen,
                new Rect(1, 1, Math.Max(0, Size.X - 2), Math.Max(0, Size.Y - 2)));
        }
    }

    private void EnsureTextures(WgpuContext context, uint width, uint height, uint sampleCount)
    {
        if (_colorTexture is not null &&
            _colorTexture.Width == width &&
            _colorTexture.Height == height &&
            _textureSampleCount == sampleCount)
        {
            return;
        }

        DisposeTextures();
        _colorTexture = new GpuTexture(
            context,
            width,
            height,
            TextureFormat.Rgba8Unorm,
            TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            "Voxel color",
            alphaMode: GpuTextureAlphaMode.Premultiplied);
        _msaaColorTexture = sampleCount > 1
            ? new GpuTexture(
                context,
                width,
                height,
                TextureFormat.Rgba8Unorm,
                TextureUsage.RenderAttachment,
                "Voxel MSAA color",
                sampleCount: sampleCount,
                alphaMode: GpuTextureAlphaMode.Premultiplied)
            : null;
        _depthTexture = new GpuTexture(
            context,
            width,
            height,
            TextureFormat.Depth24Plus,
            TextureUsage.RenderAttachment,
            "Voxel depth",
            sampleCount: sampleCount);
        _textureSampleCount = sampleCount;
    }

    private void DisposeTextures()
    {
        _colorTexture?.Dispose();
        _colorTexture = null;
        _msaaColorTexture?.Dispose();
        _msaaColorTexture = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
        _textureSampleCount = 0;
    }

    private WgpuContext? GetActiveWgpuContext()
    {
        var windows = WindowManager.ActiveWindows;
        if (windows.Count == 0) return WgpuContext.Current;
        if (windows.Count == 1) return windows[0].WgpuContext;

        Visual? current = this;
        while (current is not null)
        {
            for (var index = 0; index < windows.Count; index++)
            {
                if (windows[index].Content == current)
                {
                    return windows[index].WgpuContext;
                }
            }
            current = current.Parent;
        }
        return windows[0].WgpuContext;
    }

    private bool IsPressed(Key primary, Key alternate = Key.Unknown) =>
        _pressedKeys.Contains(primary) ||
        (alternate != Key.Unknown && _pressedKeys.Contains(alternate));

    private float Axis(Key positive, Key positiveAlternate, Key negative, Key negativeAlternate) =>
        (IsPressed(positive, positiveAlternate) ? 1f : 0f) -
        (IsPressed(negative, negativeAlternate) ? 1f : 0f);

    private static bool TrySelectBlock(Key key, out VoxelBlock block)
    {
        var slot = key switch
        {
            Key.Number1 or Key.Keypad1 => 1,
            Key.Number2 or Key.Keypad2 => 2,
            Key.Number3 or Key.Keypad3 => 3,
            Key.Number4 or Key.Keypad4 => 4,
            Key.Number5 or Key.Keypad5 => 5,
            Key.Number6 or Key.Keypad6 => 6,
            Key.Number7 or Key.Keypad7 => 7,
            _ => 0
        };
        block = VoxelBlockCatalog.FromHotbarSlot(slot);
        return slot != 0;
    }

    private void CycleSelectedBlock(int direction)
    {
        var slot = 1;
        for (; slot <= VoxelBlockCatalog.PlaceableCount; slot++)
        {
            if (VoxelBlockCatalog.FromHotbarSlot(slot) == SelectedBlock) break;
        }
        slot = 1 + (slot - 1 + direction + VoxelBlockCatalog.PlaceableCount) %
            VoxelBlockCatalog.PlaceableCount;
        SelectedBlock = VoxelBlockCatalog.FromHotbarSlot(slot);
        SelectedBlockChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private static bool IsGameKey(Key key) => key is
        Key.W or Key.A or Key.S or Key.D or
        Key.Up or Key.Down or Key.Left or Key.Right or
        Key.Space or Key.Q or Key.E or Key.F or Key.Escape or
        Key.ControlLeft or Key.ControlRight or
        Key.ShiftLeft or Key.ShiftRight or
        Key.Number1 or Key.Number2 or Key.Number3 or Key.Number4 or
        Key.Number5 or Key.Number6 or Key.Number7 or
        Key.Keypad1 or Key.Keypad2 or Key.Keypad3 or Key.Keypad4 or
        Key.Keypad5 or Key.Keypad6 or Key.Keypad7;
}
