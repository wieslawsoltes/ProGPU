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
    private readonly float[] _postEffectConstants = new float[32];
    private readonly WgslEffectParameters _postEffect =
        new(WgslEffectShaders.VoxelWeather);
    private Task<VoxelWorld>? _worldTask;
    private GpuTexture? _colorTexture;
    private GpuTexture? _msaaColorTexture;
    private GpuTexture? _depthTexture;
    private uint _textureSampleCount;
    private bool _jumpRequested;
    private bool _animateMaterials;
    private bool _enableRayTracing;
    private bool _enableRain;
    private bool _enableMotionBlur;
    private bool _enableVoxelEffects = true;
    private float _time;
    private float _lastYaw;
    private float _lastPitch;
    private Vector2 _cameraMotion;
    private VoxelRayTracingVolume? _rayTracingVolume;
    private VoxelRaycastHit? _target;
    private Exception? _loadError;

    public VoxelGameView()
    {
        _enableRayTracing = ReadBooleanEnvironment("PROGPU_VOXEL_RAY_TRACING");
        _enableRain = ReadBooleanEnvironment("PROGPU_VOXEL_RAIN");
        _enableMotionBlur = ReadBooleanEnvironment("PROGPU_VOXEL_MOTION_BLUR");
        _enableVoxelEffects = !ReadBooleanEnvironment("PROGPU_VOXEL_DISABLE_MATERIAL_EFFECTS");
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

    public bool EnableRayTracing
    {
        get => _enableRayTracing;
        set => SetRenderOption(ref _enableRayTracing, value);
    }

    public bool EnableRain
    {
        get => _enableRain;
        set => SetRenderOption(ref _enableRain, value);
    }

    public bool EnableMotionBlur
    {
        get => _enableMotionBlur;
        set => SetRenderOption(ref _enableMotionBlur, value);
    }

    public bool EnableVoxelEffects
    {
        get => _enableVoxelEffects;
        set => SetRenderOption(ref _enableVoxelEffects, value);
    }

    public event EventHandler? WorldReady;

    public event EventHandler? SelectedBlockChanged;

    public event EventHandler? MouseLookActiveChanged;

    public event EventHandler? RenderOptionsChanged;

    public void StartNewWorld(int seed = 1337, int chunkRadius = 3)
    {
        if (_worldTask is not null)
        {
            return;
        }

        World = null;
        _loadError = null;
        _transferGeometry.Clear();
        _rayTracingVolume = null;
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

        if (_animateMaterials || EnableRain || EnableVoxelEffects)
        {
            _time += Math.Clamp(elapsedSeconds, 0f, 0.1f);
        }
        var yawDelta = NormalizeAngle(_player.Yaw - _lastYaw);
        var pitchDelta = _player.Pitch - _lastPitch;
        _cameraMotion = Vector2.Lerp(
            _cameraMotion,
            new Vector2(-yawDelta, pitchDelta) * 0.055f,
            Math.Clamp(elapsedSeconds * 18f, 0f, 1f));
        _lastYaw = _player.Yaw;
        _lastPitch = _player.Pitch;
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
        var sampleCount = EnableRayTracing ? 1u : dpiScale >= 1.5f ? 1u : 4u;
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
        if (_payload.Chunks.Count > 0 || EnableRayTracing)
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
            if (EnableRain || EnableMotionBlur)
            {
                UpdatePostEffectParameters();
                context.DrawWgslEffect(_postEffect);
            }
            else
            {
                context.Commands.Add(new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Rect = new Rect(Vector2.Zero, Size),
                    Texture = _colorTexture
                });
            }
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
            else if (!wasPressed && e.Key == Key.R)
            {
                EnableRayTracing = !EnableRayTracing;
            }
            else if (!wasPressed && e.Key == Key.T)
            {
                EnableRain = !EnableRain;
            }
            else if (!wasPressed && e.Key == Key.M)
            {
                EnableMotionBlur = !EnableMotionBlur;
            }
            else if (!wasPressed && e.Key == Key.V)
            {
                EnableVoxelEffects = !EnableVoxelEffects;
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
            _rayTracingVolume = BuildRayTracingVolume(World);
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
        _payload.CameraForward = _player.LookDirection;
        _payload.AspectRatio = Math.Max(0.01f, Size.X / Math.Max(1f, Size.Y));
        _payload.VerticalFieldOfView = 70f * MathF.PI / 180f;
        _payload.Time = _time;
        _payload.FogStart = farPlane * 0.55f;
        _payload.FogEnd = farPlane * 0.92f;
        _payload.HasSelectedBlock = _target.HasValue;
        _payload.RenderMode = EnableRayTracing ? VoxelRenderMode.RayTraced : VoxelRenderMode.Rasterized;
        _payload.RayTracingVolume = _rayTracingVolume;
        _payload.MaterialEffect = EnableVoxelEffects
            ? VoxelMaterialEffects.DynamicEnvironment
            : VoxelMaterialEffects.None;
        _payload.WindStrength = EnableRain ? 0.9f : 0.35f;
        _payload.DeformationStrength = EnableVoxelEffects ? 1f : 0f;
        _payload.RainIntensity = EnableRain ? 0.82f : 0f;
        _payload.Wetness = EnableRain ? 0.9f : 0f;
        _payload.TimeOfDay = 0.22f + (0.5f + 0.5f * MathF.Sin(_time * 0.025f)) * 0.5f;
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
        _rayTracingVolume?.TrySetBlock(target.Block.X, target.Block.Y, target.Block.Z, 0u);
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
        _rayTracingVolume?.TrySetBlock(position.X, position.Y, position.Z, (uint)SelectedBlock);
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

    private void UpdatePostEffectParameters()
    {
        _postEffect.SourceTexture = _colorTexture;
        _postEffect.Bounds = new Rect(Vector2.Zero, Size);
        _postEffect.Constants = _postEffectConstants;
        _postEffectConstants[0] = _time;
        _postEffectConstants[1] = EnableRain ? 0.82f : 0f;
        _postEffectConstants[2] = EnableMotionBlur ? 1f : 0f;
        _postEffectConstants[3] = 1f;
        _postEffectConstants[4] = Math.Clamp(_cameraMotion.X, -0.018f, 0.018f);
        _postEffectConstants[5] = Math.Clamp(_cameraMotion.Y, -0.018f, 0.018f);
        _postEffectConstants[6] = 0.8f;
        _postEffectConstants[7] = 0.3f;
        _postEffectConstants[8] = 0.66f;
        _postEffectConstants[9] = 0.84f;
        _postEffectConstants[10] = 1f;
        _postEffectConstants[11] = 0.58f;

        var look = _player.LookDirection;
        var right = Vector3.Cross(look, Vector3.UnitY);
        right = right.LengthSquared() > 0.000001f ? Vector3.Normalize(right) : Vector3.UnitX;
        var up = Vector3.Normalize(Vector3.Cross(right, look));
        var eye = _player.EyePosition;
        _postEffectConstants[12] = eye.X;
        _postEffectConstants[13] = eye.Y;
        _postEffectConstants[14] = eye.Z;
        _postEffectConstants[15] = MathF.Tan(70f * MathF.PI / 360f);
        _postEffectConstants[16] = look.X;
        _postEffectConstants[17] = look.Y;
        _postEffectConstants[18] = look.Z;
        _postEffectConstants[19] = Math.Max(0.01f, Size.X / Math.Max(1f, Size.Y));
        _postEffectConstants[20] = right.X;
        _postEffectConstants[21] = right.Y;
        _postEffectConstants[22] = right.Z;
        _postEffectConstants[23] = 0f;
        _postEffectConstants[24] = up.X;
        _postEffectConstants[25] = up.Y;
        _postEffectConstants[26] = up.Z;
        _postEffectConstants[27] = 0f;
        _postEffectConstants[28] = 0.92f;
        _postEffectConstants[29] = 0.88f;
        _postEffectConstants[30] = 4.5f;
        _postEffectConstants[31] = 72f;
    }

    private static VoxelRayTracingVolume BuildRayTracingVolume(VoxelWorld world)
    {
        if (world.Chunks.Count == 0)
        {
            return new VoxelRayTracingVolume
            {
                Blocks = new uint[1],
                OriginX = 0,
                OriginY = 0,
                OriginZ = 0,
                Width = 1,
                Height = 1,
                Depth = 1,
                ContentVersion = world.ContentVersion
            };
        }

        var minChunkX = int.MaxValue;
        var minChunkY = int.MaxValue;
        var minChunkZ = int.MaxValue;
        var maxChunkX = int.MinValue;
        var maxChunkY = int.MinValue;
        var maxChunkZ = int.MinValue;
        foreach (var chunk in world.Chunks)
        {
            minChunkX = Math.Min(minChunkX, chunk.Position.X);
            minChunkY = Math.Min(minChunkY, chunk.Position.Y);
            minChunkZ = Math.Min(minChunkZ, chunk.Position.Z);
            maxChunkX = Math.Max(maxChunkX, chunk.Position.X);
            maxChunkY = Math.Max(maxChunkY, chunk.Position.Y);
            maxChunkZ = Math.Max(maxChunkZ, chunk.Position.Z);
        }

        var originX = minChunkX * VoxelChunk.Size;
        var originY = minChunkY * VoxelChunk.Size;
        var originZ = minChunkZ * VoxelChunk.Size;
        var width = checked((maxChunkX - minChunkX + 1) * VoxelChunk.Size);
        var height = checked((maxChunkY - minChunkY + 1) * VoxelChunk.Size);
        var depth = checked((maxChunkZ - minChunkZ + 1) * VoxelChunk.Size);
        var blocks = new uint[checked(width * height * depth)];
        foreach (var chunk in world.Chunks)
        {
            var chunkOriginX = chunk.Position.X * VoxelChunk.Size - originX;
            var chunkOriginY = chunk.Position.Y * VoxelChunk.Size - originY;
            var chunkOriginZ = chunk.Position.Z * VoxelChunk.Size - originZ;
            for (var y = 0; y < VoxelChunk.Size; y++)
            {
                for (var z = 0; z < VoxelChunk.Size; z++)
                {
                    for (var x = 0; x < VoxelChunk.Size; x++)
                    {
                        var block = chunk.GetLocal(x, y, z);
                        blocks[
                            chunkOriginX + x +
                            width * (chunkOriginZ + z + depth * (chunkOriginY + y))] =
                            (uint)block;
                    }
                }
            }
        }

        return new VoxelRayTracingVolume
        {
            Blocks = blocks,
            OriginX = originX,
            OriginY = originY,
            OriginZ = originZ,
            Width = width,
            Height = height,
            Depth = depth,
            ContentVersion = world.ContentVersion
        };
    }

    private void SetRenderOption(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }
        field = value;
        RenderOptionsChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= MathF.Tau;
        while (angle < -MathF.PI) angle += MathF.Tau;
        return angle;
    }

    private static bool ReadBooleanEnvironment(string name) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var enabled) && enabled;

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
        Key.Space or Key.Q or Key.E or Key.F or Key.R or Key.T or Key.M or Key.V or Key.Escape or
        Key.ControlLeft or Key.ControlRight or
        Key.ShiftLeft or Key.ShiftRight or
        Key.Number1 or Key.Number2 or Key.Number3 or Key.Number4 or
        Key.Number5 or Key.Number6 or Key.Number7 or
        Key.Keypad1 or Key.Keypad2 or Key.Keypad3 or Key.Keypad4 or
        Key.Keypad5 or Key.Keypad6 or Key.Keypad7;
}
