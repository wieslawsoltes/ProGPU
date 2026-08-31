using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using ProGPU.Vector;
using ProGPU.Text;
using ProGPU.Scene;
using ProGPU.Scene.Extensions;
using ProGPU.Backend;
using ProGPU.Media.Playback;
using ProGPU.Media.Rendering;
using Silk.NET.WebGPU;

namespace Microsoft.UI.Xaml.Media.Media3D
{
    public abstract class Geometry3D
    {
        public int Version { get; set; }

        public void Invalidate()
        {
            Version++;
        }
    }

    public class MeshGeometry3D : Geometry3D
    {
        private Vector3[] _positions = Array.Empty<Vector3>();
        private Vector3[] _normals = Array.Empty<Vector3>();
        private Vector2[] _textureCoordinates = Array.Empty<Vector2>();
        private int[] _triangleIndices = Array.Empty<int>();

        public Vector3[] Positions
        {
            get => _positions;
            set { _positions = value; Invalidate(); }
        }

        public Vector3[] Normals
        {
            get => _normals;
            set { _normals = value; Invalidate(); }
        }

        public Vector2[] TextureCoordinates
        {
            get => _textureCoordinates;
            set { _textureCoordinates = value; Invalidate(); }
        }

        public int[] TriangleIndices
        {
            get => _triangleIndices;
            set { _triangleIndices = value; Invalidate(); }
        }

        public Vector3[] GetNormalsOrCompute()
        {
            if (Normals != null && Normals.Length == Positions.Length)
                return Normals;

            if (Positions.Length == 0 || TriangleIndices.Length == 0)
                return Array.Empty<Vector3>();

            var computed = new Vector3[Positions.Length];
            
            for (int i = 0; i < TriangleIndices.Length; i += 3)
            {
                if (i + 2 >= TriangleIndices.Length) break;
                
                int i0 = TriangleIndices[i];
                int i1 = TriangleIndices[i + 1];
                int i2 = TriangleIndices[i + 2];

                if (i0 < 0 || i0 >= Positions.Length || i1 < 0 || i1 >= Positions.Length || i2 < 0 || i2 >= Positions.Length) continue;

                var p0 = Positions[i0];
                var p1 = Positions[i1];
                var p2 = Positions[i2];

                var u = p1 - p0;
                var v = p2 - p0;
                var normal = Vector3.Cross(u, v);
                float len = normal.Length();
                if (len > 0.0001f)
                {
                    normal /= len;
                }

                computed[i0] += normal;
                computed[i1] += normal;
                computed[i2] += normal;
            }

            for (int i = 0; i < computed.Length; i++)
            {
                float len = computed[i].Length();
                if (len > 0.0001f)
                    computed[i] /= len;
                else
                    computed[i] = Vector3.UnitY;
            }

            return computed;
        }
    }

    public abstract class Material
    {
    }

    public class DiffuseMaterial : Material
    {
        public Brush Brush { get; set; } = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
        public Vector4 Color { get; set; } = Vector4.One;
        public Vector3 SpecularColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);
        public float Shininess { get; set; } = 32.0f;
        public Vector3 AmbientColor { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);

        public DiffuseMaterial()
        {
        }

        public DiffuseMaterial(Brush brush)
        {
            Brush = brush;
        }
    }

    /// <summary>
    /// ProGPU extension material that samples the current WinUI MediaPlayer
    /// frame directly in the Mesh3D WebGPU pass.
    /// </summary>
    public sealed class ProGpuMediaTextureMaterial :
        DiffuseMaterial,
        IDisposable
    {
        private Windows.Media.Playback.MediaPlayer? _mediaPlayer;
        private MediaVideoEffectOptions _effects =
            MediaVideoEffectOptions.Identity;
        private TextureSamplingMode _samplingMode =
            TextureSamplingMode.Linear;
        private bool _disposed;

        public Windows.Media.Playback.MediaPlayer? MediaPlayer
        {
            get => _mediaPlayer;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (ReferenceEquals(_mediaPlayer, value))
                {
                    return;
                }
                if (_mediaPlayer is not null)
                {
                    _mediaPlayer.ProGpuFrameAvailable -=
                        OnFrameAvailable;
                    _mediaPlayer.PlaybackSession
                        .PresentationChanged -=
                        OnPresentationChanged;
                }
                _mediaPlayer = value;
                if (_mediaPlayer is not null)
                {
                    _mediaPlayer.ProGpuFrameAvailable +=
                        OnFrameAvailable;
                    _mediaPlayer.PlaybackSession
                        .PresentationChanged +=
                        OnPresentationChanged;
                }
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public MediaVideoEffectOptions Effects
        {
            get => _effects;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_effects.Equals(value))
                {
                    _effects = value;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public TextureSamplingMode SamplingMode
        {
            get => _samplingMode;
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_samplingMode != value)
                {
                    _samplingMode = value;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        internal event EventHandler? Changed;

        internal IProGpuTextureLeaseSource? TextureSource =>
            _mediaPlayer?.ProGpuVideoSurface;

        internal MeshTexturePresentation TexturePresentation
        {
            get
            {
                Windows.Media.Playback.MediaPlaybackSession?
                    session = _mediaPlayer?.PlaybackSession;
                if (session is null)
                {
                    return MeshTexturePresentation.Identity;
                }

                Windows.Foundation.Rect sourceRect =
                    session.NormalizedSourceRect;
                return MediaMesh3DPresentation
                    .GetTexturePresentation(
                        new Vector4(
                            (float)sourceRect.X,
                            (float)sourceRect.Y,
                            (float)sourceRect.Width,
                            (float)sourceRect.Height),
                        session.PlaybackRotation switch
                        {
                            Windows.Media.MediaProperties
                                .MediaRotation
                                .Clockwise90Degrees =>
                                MediaVideoRotation
                                    .Clockwise90Degrees,
                            Windows.Media.MediaProperties
                                .MediaRotation
                                .Clockwise180Degrees =>
                                MediaVideoRotation
                                    .Clockwise180Degrees,
                            Windows.Media.MediaProperties
                                .MediaRotation
                                .Clockwise270Degrees =>
                                MediaVideoRotation
                                    .Clockwise270Degrees,
                            _ => MediaVideoRotation.None
                        },
                        session.IsMirroring);
            }
        }

        private void OnFrameAvailable(
            object? sender,
            EventArgs args) =>
            Changed?.Invoke(this, EventArgs.Empty);

        private void OnPresentationChanged(
            object? sender,
            EventArgs args) =>
            Changed?.Invoke(this, EventArgs.Empty);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_mediaPlayer is not null)
            {
                _mediaPlayer.ProGpuFrameAvailable -=
                    OnFrameAvailable;
                _mediaPlayer.PlaybackSession
                    .PresentationChanged -=
                    OnPresentationChanged;
                _mediaPlayer = null;
            }
            Changed = null;
        }
    }

    public abstract class Model3D
    {
        public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
    }

    public class GeometryModel3D : Model3D
    {
        public Geometry3D? Geometry { get; set; }
        public Material? Material { get; set; }
        public Material? BackMaterial { get; set; }
    }

    public abstract class Visual3D
    {
    }

    public class ModelVisual3D : Visual3D
    {
        public Model3D? Content { get; set; }
        public List<Visual3D> Children { get; } = new();
    }

    public abstract class Camera
    {
        private Matrix4x4 _transform = Matrix4x4.Identity;
        public Matrix4x4 Transform
        {
            get => _transform;
            set
            {
                if (_transform != value)
                {
                    _transform = value;
                    RaiseChanged();
                }
            }
        }

        public event EventHandler? Changed;

        protected void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public abstract Matrix4x4 GetProjectionMatrix(float aspectRatio);
        public abstract Matrix4x4 GetViewMatrix();
    }

    public abstract class ProjectionCamera : Camera
    {
        private Vector3 _position = new Vector3(0, 0, -10);
        public Vector3 Position
        {
            get => _position;
            set
            {
                if (_position != value)
                {
                    _position = value;
                    RaiseChanged();
                }
            }
        }

        private Vector3 _lookDirection = new Vector3(0, 0, 1);
        public Vector3 LookDirection
        {
            get => _lookDirection;
            set
            {
                if (_lookDirection != value)
                {
                    _lookDirection = value;
                    RaiseChanged();
                }
            }
        }

        private Vector3 _upDirection = Vector3.UnitY;
        public Vector3 UpDirection
        {
            get => _upDirection;
            set
            {
                if (_upDirection != value)
                {
                    _upDirection = value;
                    RaiseChanged();
                }
            }
        }

        private float _nearPlaneDistance = 0.125f;
        public float NearPlaneDistance
        {
            get => _nearPlaneDistance;
            set
            {
                if (_nearPlaneDistance != value)
                {
                    _nearPlaneDistance = value;
                    RaiseChanged();
                }
            }
        }

        private float _farPlaneDistance = 1000f;
        public float FarPlaneDistance
        {
            get => _farPlaneDistance;
            set
            {
                if (_farPlaneDistance != value)
                {
                    _farPlaneDistance = value;
                    RaiseChanged();
                }
            }
        }

        // Computed property to support LookAt seamlessly (such as for orbiting controller math)
        public Vector3 LookAt
        {
            get => Position + LookDirection;
            set => LookDirection = value - Position;
        }

        /// <summary>
        /// Atomically replaces the camera position and look direction and
        /// publishes one invalidation. Orbit and pan controllers use this to
        /// avoid exposing a transient half-updated view.
        /// </summary>
        public void SetView(Vector3 position, Vector3 lookDirection)
        {
            if (_position == position && _lookDirection == lookDirection)
            {
                return;
            }

            _position = position;
            _lookDirection = lookDirection;
            RaiseChanged();
        }

        public override Matrix4x4 GetViewMatrix()
        {
            var view = Matrix4x4.CreateLookAt(Position, Position + LookDirection, UpDirection);
            if (Transform != Matrix4x4.Identity)
            {
                if (Matrix4x4.Invert(Transform, out var invTransform))
                {
                    view = invTransform * view;
                }
            }
            return view;
        }
    }

    public class PerspectiveCamera : ProjectionCamera
    {
        private float _fieldOfView = 45f;
        public float FieldOfView
        {
            get => _fieldOfView;
            set
            {
                if (_fieldOfView != value)
                {
                    _fieldOfView = value;
                    RaiseChanged();
                }
            }
        }

        public override Matrix4x4 GetProjectionMatrix(float aspectRatio)
        {
            float fovRad = FieldOfView * MathF.PI / 180f;
            return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, NearPlaneDistance, FarPlaneDistance);
        }
    }

    public class OrthographicCamera : ProjectionCamera
    {
        private float _width = 2f;
        public float Width
        {
            get => _width;
            set
            {
                if (_width != value)
                {
                    _width = value;
                    RaiseChanged();
                }
            }
        }

        public override Matrix4x4 GetProjectionMatrix(float aspectRatio)
        {
            float height = Width / aspectRatio;
            return Matrix4x4.CreateOrthographic(Width, height, NearPlaneDistance, FarPlaneDistance);
        }
    }
}

namespace Microsoft.UI.Xaml.Controls
{
    using Microsoft.UI.Xaml.Media.Media3D;

    /// <summary>One stationary primary-button click inside a 3D viewport.</summary>
    public sealed class Viewport3DClickEventArgs : EventArgs
    {
        public Vector2 Position { get; }

        public bool IsControlPressed { get; }

        internal Viewport3DClickEventArgs(
            Vector2 position,
            bool isControlPressed)
        {
            Position = position;
            IsControlPressed = isControlPressed;
        }
    }

    public class Viewport3D : Control
    {
        private const float ClickDragThreshold = 4.0f;

        private bool _enableRetainedSceneCache;
        private ulong _sceneGeneration = 1;
        private ulong _recordGeneration = 1;
        private ulong _compiledSceneGeneration;
        private long _sceneCompilationCount;
        private readonly Viewport3DCompilationPayload
            _retainedPayload = new();
        private readonly Mesh3DFrameMetricsTarget
            _metricsTarget = new();
        private readonly Brush _compassBackgroundBrush =
            new ThemeResourceBrush("CardBackground")
            {
                Opacity = 0.45f
            };
        private readonly Brush _compassOriginBrush =
            new ThemeResourceBrush("TextPrimary")
            {
                Opacity = 0.85f
            };
        private readonly Brush _compassLabelBrush =
            new ThemeResourceBrush("TextPrimary");
        private readonly Brush _compassXBrush =
            new ThemeResourceBrush("Viewport3DXAxis");
        private readonly Brush _compassYBrush =
            new ThemeResourceBrush("Viewport3DYAxis");
        private readonly Brush _compassZBrush =
            new ThemeResourceBrush("Viewport3DZAxis");
        private readonly Pen _compassBorderPen;
        private readonly Pen _compassTipBorderPen;
        private readonly Pen _compassXPen;
        private readonly Pen _compassYPen;
        private readonly Pen _compassZPen;

        /// <summary>
        /// Enables generation-retained model compilation. Call
        /// <see cref="InvalidateScene"/> after mutating children, models,
        /// geometry, or materials while this option is enabled.
        /// </summary>
        public bool EnableRetainedSceneCache
        {
            get => _enableRetainedSceneCache;
            set
            {
                if (_enableRetainedSceneCache == value)
                {
                    return;
                }
                _enableRetainedSceneCache = value;
                InvalidateScene();
            }
        }

        public ulong SceneGeneration => _sceneGeneration;

        public long SceneCompilationCount => _sceneCompilationCount;

        public Mesh3DFrameMetrics LastMesh3DFrameMetrics =>
            _metricsTarget.LastFrameMetrics;

        /// <summary>
        /// Raised for a non-Shift stationary left click. Orbit and pan drags
        /// retain exclusive ownership of moved pointer gestures.
        /// </summary>
        public event EventHandler<Viewport3DClickEventArgs>? ViewportClicked;

        /// <summary>
        /// Advances the immutable model generation and schedules a redraw.
        /// </summary>
        public void InvalidateScene()
        {
            _sceneGeneration = NextGeneration(_sceneGeneration);
            _recordGeneration = NextGeneration(_recordGeneration);
            Invalidate();
        }

        private void InvalidateRecords()
        {
            _recordGeneration = NextGeneration(_recordGeneration);
            Invalidate();
        }

        private static ulong NextGeneration(ulong generation) =>
            generation == ulong.MaxValue ? 1 : generation + 1;

        private Camera _camera = new PerspectiveCamera();
        public Camera Camera
        {
            get => _camera;
            set
            {
                if (_camera != value)
                {
                    if (_camera != null)
                    {
                        _camera.Changed -= OnCameraChanged;
                    }
                    _camera = value;
                    if (_camera != null)
                    {
                        _camera.Changed += OnCameraChanged;
                    }
                    _cameraInitialized = false;
                    Invalidate();
                }
            }
        }

        private void OnCameraChanged(object? sender, EventArgs e)
        {
            if (!_isUpdatingCameraState)
            {
                _cameraInitialized = false;
                Invalidate();
            }
        }
        
        public new List<Visual3D> Children { get; } = new();

        // High-performance directional + ambient lighting parameters
        private Vector3 _lightDirection = new(0.5f, 1f, -0.5f);
        private float _lightIntensity = 1.0f;
        private Vector3 _ambientColor = Vector3.One;
        private float _ambientIntensity = 0.25f;
        private RenderMode3D _renderMode = RenderMode3D.Solid;
        private ShadingMode3D _shadingMode = ShadingMode3D.Realistic;

        public Vector3 LightDirection
        {
            get => _lightDirection;
            set
            {
                if (_lightDirection == value) return;
                _lightDirection = value;
                InvalidateRecords();
            }
        }

        public float LightIntensity
        {
            get => _lightIntensity;
            set
            {
                if (_lightIntensity == value) return;
                _lightIntensity = value;
                InvalidateRecords();
            }
        }

        public Vector3 AmbientColor
        {
            get => _ambientColor;
            set
            {
                if (_ambientColor == value) return;
                _ambientColor = value;
                InvalidateRecords();
            }
        }

        public float AmbientIntensity
        {
            get => _ambientIntensity;
            set
            {
                if (_ambientIntensity == value) return;
                _ambientIntensity = value;
                InvalidateRecords();
            }
        }

        public RenderMode3D RenderMode
        {
            get => _renderMode;
            set
            {
                if (_renderMode == value) return;
                _renderMode = value;
                InvalidateRecords();
            }
        }

        public ShadingMode3D ShadingMode
        {
            get => _shadingMode;
            set
            {
                if (_shadingMode == value) return;
                _shadingMode = value;
                InvalidateRecords();
            }
        }

        private GpuTexture? _colorTexture;
        private GpuTexture? _msaaColorTexture;
        private GpuTexture? _depthTexture;
        private WgpuContext? _textureContext;
        private uint _textureSampleCount;
        private readonly HashSet<ProGpuMediaTextureMaterial>
            _observedMediaMaterials = new();
        private readonly HashSet<ProGpuMediaTextureMaterial>
            _usedMediaMaterials = new();
        private readonly List<ProGpuMediaTextureMaterial>
            _staleMediaMaterials = new();

        private bool _isOrbiting = false;
        private bool _isPanning = false;
        private Vector2 _lastPointerPosition;
        private Vector2 _clickOrigin;
        private bool _isClickCandidate;

        private float _cameraTheta = 0f;
        private float _cameraPhi = 0.5f;
        private float _cameraRadius = 10f;
        private Vector3 _cameraTarget;
        private bool _cameraInitialized = false;
        private bool _isUpdatingCameraState = false;

        private void InitializeCameraState()
        {
            if (Camera is ProjectionCamera projCamera)
            {
                var dir = -projCamera.LookDirection;
                _cameraRadius = dir.Length();
                if (_cameraRadius < 0.1f) _cameraRadius = 0.1f;

                _cameraTheta = MathF.Atan2(dir.X, dir.Z);
                float lenXZ = MathF.Sqrt(dir.X * dir.X + dir.Z * dir.Z);
                _cameraPhi = MathF.Atan2(lenXZ, dir.Y);
                _cameraTarget = projCamera.LookAt;
                
                // Clamp phi to prevent crossing poles
                _cameraPhi = Math.Clamp(_cameraPhi, 0.01f, MathF.PI - 0.01f);
                
                _cameraInitialized = true;
            }
        }

        private void ApplyCameraState()
        {
            if (Camera is ProjectionCamera projCamera)
            {
                _isUpdatingCameraState = true;
                try
                {
                    float sinPhi = MathF.Sin(_cameraPhi);
                    float cosPhi = MathF.Cos(_cameraPhi);
                    float sinTheta = MathF.Sin(_cameraTheta);
                    float cosTheta = MathF.Cos(_cameraTheta);

                    var offset = new Vector3(
                        _cameraRadius * sinPhi * sinTheta,
                        _cameraRadius * cosPhi,
                        _cameraRadius * sinPhi * cosTheta
                    );

                    projCamera.SetView(_cameraTarget + offset, -offset);
                    
                    Invalidate();
                }
                finally
                {
                    _isUpdatingCameraState = false;
                }
            }
        }

        public Viewport3D()
        {
            _compassBorderPen = new Pen(
                new ThemeResourceBrush("ControlBorder"),
                1f);
            _compassTipBorderPen = new Pen(
                new ThemeResourceBrush("TextPrimary")
                {
                    Opacity = 0.9f
                },
                1f);
            _compassXPen = new Pen(_compassXBrush, 2f);
            _compassYPen = new Pen(_compassYBrush, 2f);
            _compassZPen = new Pen(_compassZBrush, 2f);
            _camera.Changed += OnCameraChanged;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            IsTabStop = true;
            Unloaded += (s, e) =>
            {
                DisposeTextures();
                ClearMediaMaterialSubscriptions();
            };
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
            _textureContext = null;
        }

        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            // Fills all available screen dimensions
            float w = float.IsInfinity(availableSize.X) ? 400f : availableSize.X;
            float h = float.IsInfinity(availableSize.Y) ? 300f : availableSize.Y;
            return new Vector2(w, h);
        }

        protected override void ArrangeOverride(Rect arrangeRect)
        {
            base.ArrangeOverride(arrangeRect);
        }

        private WgpuContext? GetActiveWgpuContext()
        {
            var activeWindows = WindowManager.ActiveWindows;
            if (activeWindows.Count == 0) return WgpuContext.Current;
            if (activeWindows.Count == 1) return activeWindows[0].WgpuContext;

            Visual? current = this;
            while (current != null)
            {
                for (int i = 0; i < activeWindows.Count; i++)
                {
                    if (activeWindows[i].Content == current)
                    {
                        return activeWindows[i].WgpuContext;
                    }
                }
                current = current.Parent;
            }

            return activeWindows[0].WgpuContext;
        }

        public override void OnRender(DrawingContext context)
        {
            if (Size.X <= 0 || Size.Y <= 0 || Camera == null) return;

            if (!_cameraInitialized) InitializeCameraState();

            var wgpuContext = GetActiveWgpuContext();
            if (wgpuContext == null) return;

            if (_textureContext != null &&
                !ReferenceEquals(_textureContext, wgpuContext))
            {
                DisposeTextures();
            }
            _textureContext = wgpuContext;

            float dpiScale = (float)DisplayScaleResolver.ResolveWindowDisplayScale(wgpuContext.Window);

            uint width = (uint)Math.Max(1, Size.X * dpiScale);
            uint height = (uint)Math.Max(1, Size.Y * dpiScale);
            uint sampleCount = dpiScale >= 1.5f ? 1u : 4u;

            // Recreate offscreen textures if size changed
            if (_colorTexture == null ||
                _colorTexture.Width != width ||
                _colorTexture.Height != height ||
                _textureSampleCount != sampleCount)
            {
                _colorTexture?.Dispose();
                _colorTexture = new GpuTexture(wgpuContext, width, height, TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment | TextureUsage.TextureBinding, "Viewport3D Color Texture", alphaMode: GpuTextureAlphaMode.Premultiplied);

                _msaaColorTexture?.Dispose();
                _msaaColorTexture = sampleCount > 1
                    ? new GpuTexture(wgpuContext, width, height, TextureFormat.Rgba8Unorm, TextureUsage.RenderAttachment, "Viewport3D MSAA Color Texture", sampleCount: sampleCount, alphaMode: GpuTextureAlphaMode.Premultiplied)
                    : null;

                _depthTexture?.Dispose();
                _depthTexture = new GpuTexture(wgpuContext, width, height, TextureFormat.Depth24PlusStencil8, TextureUsage.RenderAttachment, "Viewport3D Depth Texture", sampleCount: sampleCount);
                _textureSampleCount = sampleCount;
            }

            float aspectRatio = Size.X / Size.Y;

            // 1. Setup projection and camera view matrices
            var projection = Camera.GetProjectionMatrix(aspectRatio);
            var view = Camera.GetViewMatrix();

            // 2. Build or reuse the generation-owned recursive payload.
            Viewport3DCompilationPayload payload =
                EnableRetainedSceneCache
                    ? _retainedPayload
                    : new Viewport3DCompilationPayload();
            bool compileScene =
                !EnableRetainedSceneCache ||
                _compiledSceneGeneration != _sceneGeneration;
            payload.ViewportSize = Size;
            payload.LightDirection = LightDirection;
            payload.LightIntensity = LightIntensity;
            payload.AmbientColor = AmbientColor;
            payload.AmbientIntensity = AmbientIntensity;
            payload.ColorTexture = _colorTexture;
            payload.MsaaColorTexture = _msaaColorTexture;
            payload.DepthTexture = _depthTexture;
            payload.SampleCount = sampleCount;
            ulong targetPixelCount = (ulong)width * height;
            ulong targetSampleLayers =
                1UL + sampleCount +
                (sampleCount > 1 ? sampleCount : 0UL);
            payload.LogicalTargetTextureBytes = checked(
                targetPixelCount * 4UL * targetSampleLayers);
            payload.RenderMode = RenderMode;
            payload.ShadingMode = ShadingMode;
            payload.SceneGeneration = EnableRetainedSceneCache
                ? _sceneGeneration
                : 0;
            payload.RecordGeneration = EnableRetainedSceneCache
                ? _recordGeneration
                : 0;
            payload.SceneReused = !compileScene;
            payload.SceneCompilationCount = compileScene ? 1 : 0;
            payload.ModelVisualVisitCount = 0;
            payload.MetricsTarget = _metricsTarget;

            if (compileScene)
            {
                payload.Meshes.Clear();
                foreach (var visual in Children)
                {
                    CompileVisual(
                        visual,
                        Matrix4x4.Identity,
                        payload);
                }
                SynchronizeMediaMaterialSubscriptions();
                _sceneCompilationCount++;
                if (EnableRetainedSceneCache)
                {
                    _compiledSceneGeneration = _sceneGeneration;
                }
            }

            if (payload.Meshes.Count > 0)
            {
                // Push active viewport projection onto context's command stack
                context.Commands.Add(new RenderCommand
                {
                    Type = RenderCommandType.DrawExtension,
                    ExtensionId = CompositorBuiltInExtensions.Mesh3D,
                    UseGpuTransforms = true,
                    CameraView = view,
                    Transform = projection,
                    DataParam = payload
                });

                // Render the offscreen 3D texture on the main 2D pass
                context.Commands.Add(new RenderCommand
                {
                    Type = RenderCommandType.DrawTexture,
                    Rect = new Rect(Vector2.Zero, Size),
                    Texture = _colorTexture
                });
            }

            DrawCoordinateCompass(context, view);

            base.OnRender(context);
        }

        private void ObserveMediaMaterial(
            ProGpuMediaTextureMaterial material)
        {
            _usedMediaMaterials.Add(material);
            if (_observedMediaMaterials.Add(material))
            {
                material.Changed += OnMediaMaterialChanged;
            }
        }

        private void SynchronizeMediaMaterialSubscriptions()
        {
            if (_observedMediaMaterials.Count !=
                _usedMediaMaterials.Count)
            {
                foreach (ProGpuMediaTextureMaterial material in
                         _observedMediaMaterials)
                {
                    if (!_usedMediaMaterials.Contains(material))
                    {
                        _staleMediaMaterials.Add(material);
                    }
                }
                for (int index = 0;
                     index < _staleMediaMaterials.Count;
                     index++)
                {
                    ProGpuMediaTextureMaterial material =
                        _staleMediaMaterials[index];
                    material.Changed -= OnMediaMaterialChanged;
                    _observedMediaMaterials.Remove(material);
                }
                _staleMediaMaterials.Clear();
            }
            _usedMediaMaterials.Clear();
        }

        private void ClearMediaMaterialSubscriptions()
        {
            foreach (ProGpuMediaTextureMaterial material in
                     _observedMediaMaterials)
            {
                material.Changed -= OnMediaMaterialChanged;
            }
            _observedMediaMaterials.Clear();
            _usedMediaMaterials.Clear();
            _staleMediaMaterials.Clear();
        }

        private void OnMediaMaterialChanged(
            object? sender,
            EventArgs args) =>
            InvalidateScene();

        private void CompileVisual(Visual3D visual, Matrix4x4 parentTransform, Viewport3DCompilationPayload payload)
        {
            payload.ModelVisualVisitCount++;
            if (visual is ModelVisual3D modelVisual)
            {
                var localTransform = parentTransform;
                if (modelVisual.Content != null)
                {
                    localTransform = modelVisual.Content.Transform * parentTransform;
                    
                    if (modelVisual.Content is GeometryModel3D geomModel && geomModel.Geometry != null)
                    {
                        var mesh = geomModel.Geometry as MeshGeometry3D;
                        if (mesh != null)
                        {
                            var positions = mesh.Positions;
                            var normals = mesh.GetNormalsOrCompute();
                            var indices = mesh.TriangleIndices;

                            if (positions.Length > 0 && indices.Length > 0)
                            {
                                // Dynamic WinUI 3 Palette Brush resolving to match Rule 1.C
                                Vector4 diffuseColor = Vector4.One;
                                Vector3 specularColor = new Vector3(0.2f, 0.2f, 0.2f);
                                float shininess = 32.0f;
                                Vector3 ambientColor = new Vector3(0.2f, 0.2f, 0.2f);
                                float opacity = 1.0f;
                                IProGpuTextureLeaseSource?
                                    textureSource = null;
                                MeshTextureEffect textureEffect =
                                    MeshTextureEffect.Identity;
                                TextureSamplingMode textureSamplingMode =
                                    TextureSamplingMode.Linear;
                                ImageEffectYuvConversion?
                                    textureYuvConversion = null;
                                MeshTexturePresentation
                                    texturePresentation =
                                        MeshTexturePresentation
                                            .Identity;

                                if (geomModel.Material is DiffuseMaterial diffuse && diffuse.Brush != null)
                                {
                                    opacity = diffuse.Brush.Opacity;
                                    specularColor = diffuse.SpecularColor;
                                    shininess = diffuse.Shininess;
                                    ambientColor = diffuse.AmbientColor;

                                    // If the brush is a dynamic theme resource brush, resolve it against the active theme family
                                    Brush? activeBrush = diffuse.Brush;
                                    if (diffuse.Brush is ThemeResourceBrush themeRes)
                                    {
                                        activeBrush = XamlResourceResolver.ResolveThemeBrush(
                                            themeRes, this, ActualTheme, ActualThemeFamily);
                                    }

                                    if (activeBrush is SolidColorBrush solid)
                                    {
                                        diffuseColor = solid.Color;
                                    }
                                    else if (activeBrush is LinearGradientBrush gradient && gradient.Stops.Length > 0)
                                    {
                                        diffuseColor = gradient.Stops[0].Color; // Fallback to first stop for mesh base color
                                    }

                                    // Blend with DiffuseMaterial.Color if it is set
                                    diffuseColor *= diffuse.Color;
                                    opacity *= diffuseColor.W;

                                    if (diffuse is
                                        ProGpuMediaTextureMaterial
                                            mediaMaterial)
                                    {
                                        ObserveMediaMaterial(
                                            mediaMaterial);
                                        textureSource =
                                            mediaMaterial.TextureSource;
                                        MediaVideoEffectOptions effects =
                                            mediaMaterial.Effects;
                                        textureEffect =
                                            new MeshTextureEffect(
                                                effects.Brightness,
                                                effects.Contrast,
                                                effects.Saturation,
                                                effects.Grayscale,
                                                effects.Sepia,
                                                effects.Invert,
                                                effects.BlurSigma,
                                                effects.ColorMatrix,
                                                effects
                                                    .LuminanceToAlpha);
                                        textureSamplingMode =
                                            mediaMaterial.SamplingMode;
                                        textureYuvConversion =
                                            GetMediaYuvConversion(
                                                textureSource);
                                        texturePresentation =
                                            mediaMaterial
                                                .TexturePresentation;
                                    }
                                }

                                if (geomModel.Material != null || geomModel.BackMaterial == null)
                                {
                                    payload.Meshes.Add(new MeshCompilationEntry
                                    {
                                        Geometry = mesh,
                                        GeometryVersion = mesh.Version,
                                        Positions = positions,
                                        Normals = normals,
                                        Indices = indices,
                                        TextureCoordinates =
                                            mesh.TextureCoordinates,
                                        TextureSource = textureSource,
                                        TextureEffect = textureEffect,
                                        TextureSamplingMode =
                                            textureSamplingMode,
                                        YuvConversion =
                                            textureYuvConversion,
                                        TexturePresentation =
                                            texturePresentation,
                                        ModelTransform = localTransform,
                                        Color = diffuseColor,
                                        SpecularColor = specularColor,
                                        Shininess = shininess,
                                        AmbientColor = ambientColor,
                                        Opacity = opacity,
                                        IsBackFace = false
                                    });
                                }

                                if (geomModel.BackMaterial is DiffuseMaterial backDiffuse && backDiffuse.Brush != null)
                                {
                                    Vector4 backDiffuseColor = Vector4.One;
                                    Vector3 backSpecularColor = backDiffuse.SpecularColor;
                                    float backShininess = backDiffuse.Shininess;
                                    Vector3 backAmbientColor = backDiffuse.AmbientColor;
                                    float backOpacity = backDiffuse.Brush.Opacity;
                                    IProGpuTextureLeaseSource?
                                        backTextureSource = null;
                                    MeshTextureEffect backTextureEffect =
                                        MeshTextureEffect.Identity;
                                    TextureSamplingMode
                                        backTextureSamplingMode =
                                            TextureSamplingMode.Linear;
                                    ImageEffectYuvConversion?
                                        backTextureYuvConversion =
                                            null;
                                    MeshTexturePresentation
                                        backTexturePresentation =
                                            MeshTexturePresentation
                                                .Identity;

                                    Brush? activeBackBrush = backDiffuse.Brush;
                                    if (backDiffuse.Brush is ThemeResourceBrush themeResBack)
                                    {
                                        activeBackBrush = XamlResourceResolver.ResolveThemeBrush(
                                            themeResBack, this, ActualTheme, ActualThemeFamily);
                                    }

                                    if (activeBackBrush is SolidColorBrush solidBack)
                                    {
                                        backDiffuseColor = solidBack.Color;
                                    }
                                    else if (activeBackBrush is LinearGradientBrush gradientBack && gradientBack.Stops.Length > 0)
                                    {
                                        backDiffuseColor = gradientBack.Stops[0].Color;
                                    }

                                    backDiffuseColor *= backDiffuse.Color;
                                    backOpacity *= backDiffuseColor.W;

                                    if (backDiffuse is
                                        ProGpuMediaTextureMaterial
                                            backMediaMaterial)
                                    {
                                        ObserveMediaMaterial(
                                            backMediaMaterial);
                                        backTextureSource =
                                            backMediaMaterial
                                                .TextureSource;
                                        MediaVideoEffectOptions effects =
                                            backMediaMaterial.Effects;
                                        backTextureEffect =
                                            new MeshTextureEffect(
                                                effects.Brightness,
                                                effects.Contrast,
                                                effects.Saturation,
                                                effects.Grayscale,
                                                effects.Sepia,
                                                effects.Invert,
                                                effects.BlurSigma,
                                                effects.ColorMatrix,
                                                effects
                                                    .LuminanceToAlpha);
                                        backTextureSamplingMode =
                                            backMediaMaterial
                                                .SamplingMode;
                                        backTextureYuvConversion =
                                            GetMediaYuvConversion(
                                                backTextureSource);
                                        backTexturePresentation =
                                            backMediaMaterial
                                                .TexturePresentation;
                                    }

                                    payload.Meshes.Add(new MeshCompilationEntry
                                    {
                                        Geometry = mesh,
                                        GeometryVersion = mesh.Version,
                                        Positions = positions,
                                        Normals = normals,
                                        Indices = indices,
                                        TextureCoordinates =
                                            mesh.TextureCoordinates,
                                        TextureSource =
                                            backTextureSource,
                                        TextureEffect =
                                            backTextureEffect,
                                        TextureSamplingMode =
                                            backTextureSamplingMode,
                                        YuvConversion =
                                            backTextureYuvConversion,
                                        TexturePresentation =
                                            backTexturePresentation,
                                        ModelTransform = localTransform,
                                        Color = backDiffuseColor,
                                        SpecularColor = backSpecularColor,
                                        Shininess = backShininess,
                                        AmbientColor = backAmbientColor,
                                        Opacity = backOpacity,
                                        IsBackFace = true
                                    });
                                }
                            }
                        }
                    }
                }

                foreach (var child in modelVisual.Children)
                {
                    CompileVisual(child, localTransform, payload);
                }
            }
        }

        private static ImageEffectYuvConversion?
            GetMediaYuvConversion(
                IProGpuTextureLeaseSource? textureSource)
        {
            if (textureSource is not MediaGpuSurface surface)
            {
                return null;
            }

            MediaGpuFrameDescriptor descriptor =
                surface.CurrentDescriptor;
            return descriptor.PixelFormat is
                    MediaVideoPixelFormat.Nv12 or
                    MediaVideoPixelFormat.P010
                ? MediaGpuSurfaceDrawingExtensions
                    .GetYuvConversion(descriptor)
                : null;
        }

        public override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                e.Handled = true;
                InputSystem.SetFocus(this);

                bool isShift = InputSystem.Current.IsShiftPressed;

                if (e.IsLeftButtonPressed)
                {
                    if (!_cameraInitialized) InitializeCameraState();

                    _clickOrigin = e.Position;
                    _isClickCandidate = !isShift;

                    if (isShift || Camera is OrthographicCamera)
                    {
                        _isOrbiting = false;
                        _isPanning = true;
                    }
                    else
                    {
                        _isOrbiting = true;
                        _isPanning = false;
                    }

                    _lastPointerPosition = e.Position;
                    InputSystem.CapturePointer(this);
                }
                else if (e.IsRightButtonPressed || e.IsMiddleButtonPressed)
                {
                    if (!_cameraInitialized) InitializeCameraState();

                    _isClickCandidate = false;
                    _isOrbiting = false;
                    _isPanning = true;
                    _lastPointerPosition = e.Position;
                    InputSystem.CapturePointer(this);
                }
                else
                {
                    _isClickCandidate = false;
                }
            }
            base.OnPointerPressed(e);
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                e.Handled = true;
                bool publishClick = _isClickCandidate &&
                    Vector2.DistanceSquared(e.Position, _clickOrigin) <=
                    ClickDragThreshold * ClickDragThreshold;
                _isClickCandidate = false;
                if (_isOrbiting || _isPanning)
                {
                    InputSystem.ReleasePointerCapture();
                    _isOrbiting = false;
                    _isPanning = false;
                }
                if (publishClick)
                {
                    ViewportClicked?.Invoke(
                        this,
                        new Viewport3DClickEventArgs(
                            e.Position,
                            InputSystem.Current.IsControlPressed));
                }
            }
            base.OnPointerReleased(e);
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                if (_isClickCandidate &&
                    Vector2.DistanceSquared(e.Position, _clickOrigin) >
                    ClickDragThreshold * ClickDragThreshold)
                {
                    _isClickCandidate = false;
                }
                if (_isOrbiting)
                {
                    e.Handled = true;
                    if (!_cameraInitialized) InitializeCameraState();

                    var delta = e.Position - _lastPointerPosition;
                    _lastPointerPosition = e.Position;

                    _cameraTheta -= delta.X * 0.01f;
                    _cameraPhi -= delta.Y * 0.01f;

                    // Clamp Phi to prevent visual flipping/gimbal lock at the poles
                    _cameraPhi = Math.Clamp(_cameraPhi, 0.01f, MathF.PI - 0.01f);

                    ApplyCameraState();
                }
                else if (_isPanning && Camera is ProjectionCamera projCamera)
                {
                    e.Handled = true;
                    if (!_cameraInitialized) InitializeCameraState();

                    var delta = e.Position - _lastPointerPosition;
                    _lastPointerPosition = e.Position;

                    var forward = Vector3.Normalize(projCamera.LookDirection);
                    var right = Vector3.Normalize(Vector3.Cross(forward, projCamera.UpDirection));
                    var up = Vector3.Normalize(Vector3.Cross(right, forward));

                    float panSpeed = _cameraRadius * 0.0015f;
                    _cameraTarget -= right * (delta.X * panSpeed);
                    _cameraTarget += up * (delta.Y * panSpeed);

                    ApplyCameraState();
                }
            }
            base.OnPointerMoved(e);
        }

        public override void OnPointerCanceled(PointerRoutedEventArgs e)
        {
            _isClickCandidate = false;
            _isOrbiting = false;
            _isPanning = false;
            base.OnPointerCanceled(e);
        }

        public override void OnPointerCaptureLost(PointerRoutedEventArgs e)
        {
            _isClickCandidate = false;
            _isOrbiting = false;
            _isPanning = false;
            base.OnPointerCaptureLost(e);
        }

        public override void OnPointerWheelChanged(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                e.Handled = true;
                if (!_cameraInitialized) InitializeCameraState();

                float zoomFactor = e.WheelDelta > 0 ? 0.9f : 1.1f;

                if (Camera is OrthographicCamera ortho)
                {
                    ortho.Width *= zoomFactor;
                    ortho.Width = Math.Clamp(ortho.Width, 0.1f, 1000.0f);
                }
                else
                {
                    _cameraRadius *= zoomFactor;
                    _cameraRadius = Math.Clamp(_cameraRadius, 1.5f, 100.0f);
                }

                ApplyCameraState();
            }
            base.OnPointerWheelChanged(e);
        }

        public override void OnKeyDown(KeyRoutedEventArgs e)
        {
            if (IsEnabled && IsFocused && Camera is ProjectionCamera projCamera)
            {
                if (!_cameraInitialized) InitializeCameraState();
                
                bool changed = false;
                float angleSpeed = 0.05f;
                float panSpeed = _cameraRadius * 0.03f;

                var forward = Vector3.Normalize(projCamera.LookDirection);
                var right = Vector3.Normalize(Vector3.Cross(forward, projCamera.UpDirection));
                var up = Vector3.Normalize(Vector3.Cross(right, forward));

                bool isShift = InputSystem.Current.IsShiftPressed;

                if (e.Key == Silk.NET.Input.Key.Left || e.Key == Silk.NET.Input.Key.A)
                {
                    if (isShift || projCamera is OrthographicCamera)
                    {
                        _cameraTarget -= right * panSpeed;
                    }
                    else
                    {
                        _cameraTheta -= angleSpeed;
                    }
                    changed = true;
                }
                else if (e.Key == Silk.NET.Input.Key.Right || e.Key == Silk.NET.Input.Key.D)
                {
                    if (isShift || projCamera is OrthographicCamera)
                    {
                        _cameraTarget += right * panSpeed;
                    }
                    else
                    {
                        _cameraTheta += angleSpeed;
                    }
                    changed = true;
                }
                else if (e.Key == Silk.NET.Input.Key.Up || e.Key == Silk.NET.Input.Key.W)
                {
                    if (isShift || projCamera is OrthographicCamera)
                    {
                        _cameraTarget += up * panSpeed;
                    }
                    else
                    {
                        _cameraPhi -= angleSpeed;
                        _cameraPhi = Math.Clamp(_cameraPhi, 0.01f, MathF.PI - 0.01f);
                    }
                    changed = true;
                }
                else if (e.Key == Silk.NET.Input.Key.Down || e.Key == Silk.NET.Input.Key.S)
                {
                    if (isShift || projCamera is OrthographicCamera)
                    {
                        _cameraTarget -= up * panSpeed;
                    }
                    else
                    {
                        _cameraPhi += angleSpeed;
                        _cameraPhi = Math.Clamp(_cameraPhi, 0.01f, MathF.PI - 0.01f);
                    }
                    changed = true;
                }
                else if (e.Key == Silk.NET.Input.Key.PageUp || e.Key == Silk.NET.Input.Key.Q)
                {
                    if (projCamera is OrthographicCamera ortho)
                    {
                        ortho.Width *= 0.9f;
                        ortho.Width = Math.Clamp(ortho.Width, 0.1f, 1000.0f);
                    }
                    else
                    {
                        _cameraRadius *= 0.9f;
                        _cameraRadius = Math.Clamp(_cameraRadius, 1.5f, 100.0f);
                    }
                    changed = true;
                }
                else if (e.Key == Silk.NET.Input.Key.PageDown || e.Key == Silk.NET.Input.Key.E)
                {
                    if (projCamera is OrthographicCamera ortho)
                    {
                        ortho.Width *= 1.1f;
                        ortho.Width = Math.Clamp(ortho.Width, 0.1f, 1000.0f);
                    }
                    else
                    {
                        _cameraRadius *= 1.1f;
                        _cameraRadius = Math.Clamp(_cameraRadius, 1.5f, 100.0f);
                    }
                    changed = true;
                }

                if (changed)
                {
                    e.Handled = true;
                    ApplyCameraState();
                }
            }
            base.OnKeyDown(e);
        }

        private struct ProjectedAxis
        {
            public Brush Brush;
            public Pen Pen;
            public string Label;
            public Vector3 VCam;
            public Vector2 ProjPos;
        }

        private void DrawCoordinateCompass(DrawingContext context, Matrix4x4 view)
        {
            var font = Font ?? PopupService.DefaultFont;
            if (font == null) return;

            float padding = 65f;
            float bgRadius = 38f;
            float axisLength = 25f;
            float tipRadius = 7f;

            Vector2 center = new Vector2(Size.X - padding, padding);

            context.FillCircle(
                _compassBackgroundBrush,
                center,
                bgRadius);
            context.DrawCircle(
                null,
                _compassBorderPen,
                center,
                bgRadius);

            // Project axes directions in camera space
            var axisX = new ProjectedAxis { Brush = _compassXBrush, Pen = _compassXPen, Label = "X", VCam = Vector3.TransformNormal(Vector3.UnitX, view) };
            axisX.ProjPos = new Vector2(center.X + axisX.VCam.X * axisLength, center.Y - axisX.VCam.Y * axisLength);

            var axisY = new ProjectedAxis { Brush = _compassYBrush, Pen = _compassYPen, Label = "Y", VCam = Vector3.TransformNormal(Vector3.UnitY, view) };
            axisY.ProjPos = new Vector2(center.X + axisY.VCam.X * axisLength, center.Y - axisY.VCam.Y * axisLength);

            var axisZ = new ProjectedAxis { Brush = _compassZBrush, Pen = _compassZPen, Label = "Z", VCam = Vector3.TransformNormal(Vector3.UnitZ, view) };
            axisZ.ProjPos = new Vector2(center.X + axisZ.VCam.X * axisLength, center.Y - axisZ.VCam.Y * axisLength);

            // Zero-allocation bubble sort of three elements by depth
            ProjectedAxis first = axisX;
            ProjectedAxis second = axisY;
            ProjectedAxis third = axisZ;

            if (first.VCam.Z > second.VCam.Z)
            {
                var temp = first;
                first = second;
                second = temp;
            }
            if (second.VCam.Z > third.VCam.Z)
            {
                var temp = second;
                second = third;
                third = temp;
            }
            if (first.VCam.Z > second.VCam.Z)
            {
                var temp = first;
                first = second;
                second = temp;
            }

            // Draw center origin dot
            context.FillCircle(
                _compassOriginBrush,
                center,
                3.5f);

            DrawCompassAxis(
                context,
                first,
                center,
                tipRadius,
                font);
            DrawCompassAxis(
                context,
                second,
                center,
                tipRadius,
                font);
            DrawCompassAxis(
                context,
                third,
                center,
                tipRadius,
                font);
        }

        private void DrawCompassAxis(
            DrawingContext context,
            ProjectedAxis axis,
            Vector2 center,
            float tipRadius,
            TtfFont font)
        {
            context.DrawLine(axis.Pen, center, axis.ProjPos);
            context.FillCircle(axis.Brush, axis.ProjPos, tipRadius);
            context.DrawCircle(
                null,
                _compassTipBorderPen,
                axis.ProjPos,
                tipRadius);
            context.DrawText(
                axis.Label,
                font,
                10f,
                _compassLabelBrush,
                axis.ProjPos + new Vector2(-3.5f, -5.5f),
                isBold: true);
        }
    }
}
