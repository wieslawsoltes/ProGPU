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

    public class Viewport3D : Control
    {
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
        public Vector3 LightDirection { get; set; } = new Vector3(0.5f, 1f, -0.5f);
        public float LightIntensity { get; set; } = 1.0f;
        public Vector3 AmbientColor { get; set; } = new Vector3(1f, 1f, 1f);
        public float AmbientIntensity { get; set; } = 0.25f;

        public RenderMode3D RenderMode { get; set; } = RenderMode3D.Solid;
        public ShadingMode3D ShadingMode { get; set; } = ShadingMode3D.Realistic;

        private GpuTexture? _colorTexture;
        private GpuTexture? _msaaColorTexture;
        private GpuTexture? _depthTexture;
        private uint _textureSampleCount;

        private bool _isOrbiting = false;
        private bool _isPanning = false;
        private Vector2 _lastPointerPosition;

        private float _cameraTheta = 0f;
        private float _cameraPhi = 0.5f;
        private float _cameraRadius = 10f;
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

                    var target = projCamera.LookAt;
                    var offset = new Vector3(
                        _cameraRadius * sinPhi * sinTheta,
                        _cameraRadius * cosPhi,
                        _cameraRadius * sinPhi * cosTheta
                    );

                    projCamera.Position = target + offset;
                    projCamera.LookDirection = -offset;
                    
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
            _camera.Changed += OnCameraChanged;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            IsTabStop = true;
            Unloaded += (s, e) => DisposeTextures();
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

            // 2. Build recursive payload for Mesh3DExtensionPipeline
            var payload = new Viewport3DCompilationPayload
            {
                ViewportSize = Size,
                LightDirection = LightDirection,
                LightIntensity = LightIntensity,
                AmbientColor = AmbientColor,
                AmbientIntensity = AmbientIntensity,
                ColorTexture = _colorTexture,
                MsaaColorTexture = _msaaColorTexture,
                DepthTexture = _depthTexture,
                SampleCount = sampleCount,
                RenderMode = RenderMode,
                ShadingMode = ShadingMode
            };

            foreach (var visual in Children)
            {
                CompileVisual(visual, Matrix4x4.Identity, payload);
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

        private void CompileVisual(Visual3D visual, Matrix4x4 parentTransform, Viewport3DCompilationPayload payload)
        {
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
                                        activeBrush = ThemeManager.GetBrush(themeRes.ResourceKey, ActualTheme, ActualThemeFamily);
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

                                    Brush? activeBackBrush = backDiffuse.Brush;
                                    if (backDiffuse.Brush is ThemeResourceBrush themeResBack)
                                    {
                                        activeBackBrush = ThemeManager.GetBrush(themeResBack.ResourceKey, ActualTheme, ActualThemeFamily);
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

                                    payload.Meshes.Add(new MeshCompilationEntry
                                    {
                                        Geometry = mesh,
                                        GeometryVersion = mesh.Version,
                                        Positions = positions,
                                        Normals = normals,
                                        Indices = indices,
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

                    _isOrbiting = false;
                    _isPanning = true;
                    _lastPointerPosition = e.Position;
                    InputSystem.CapturePointer(this);
                }
            }
            base.OnPointerPressed(e);
        }

        public override void OnPointerReleased(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
                e.Handled = true;
                if (_isOrbiting || _isPanning)
                {
                    InputSystem.ReleasePointerCapture();
                    _isOrbiting = false;
                    _isPanning = false;
                }
            }
            base.OnPointerReleased(e);
        }

        public override void OnPointerMoved(PointerRoutedEventArgs e)
        {
            if (IsEnabled)
            {
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
                    projCamera.LookAt -= right * (delta.X * panSpeed);
                    projCamera.LookAt += up * (delta.Y * panSpeed);

                    ApplyCameraState();
                }
            }
            base.OnPointerMoved(e);
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
                        projCamera.LookAt -= right * panSpeed;
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
                        projCamera.LookAt += right * panSpeed;
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
                        projCamera.LookAt += up * panSpeed;
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
                        projCamera.LookAt -= up * panSpeed;
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
            public Vector4 Color;
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

            // Dynamic glass backdrop background and border
            var bgBrush = new SolidColorBrush(new Vector4(0.08f, 0.08f, 0.1f, 0.45f));
            var borderBrush = ThemeManager.GetBrush("ControlBorder", ActualTheme, ActualThemeFamily) 
                              ?? new SolidColorBrush(new Vector4(0.25f, 0.25f, 0.3f, 0.4f));
            var bgPen = new Pen(borderBrush, 1f);

            context.FillCircle(bgBrush, center, bgRadius);
            context.DrawCircle(null, bgPen, center, bgRadius);

            // Project axes directions in camera space
            var axisX = new ProjectedAxis { Color = new Vector4(0.92f, 0.25f, 0.25f, 1f), Label = "X", VCam = Vector3.TransformNormal(Vector3.UnitX, view) };
            axisX.ProjPos = new Vector2(center.X + axisX.VCam.X * axisLength, center.Y - axisX.VCam.Y * axisLength);

            var axisY = new ProjectedAxis { Color = new Vector4(0.20f, 0.80f, 0.30f, 1f), Label = "Y", VCam = Vector3.TransformNormal(Vector3.UnitY, view) };
            axisY.ProjPos = new Vector2(center.X + axisY.VCam.X * axisLength, center.Y - axisY.VCam.Y * axisLength);

            var axisZ = new ProjectedAxis { Color = new Vector4(0.18f, 0.50f, 0.95f, 1f), Label = "Z", VCam = Vector3.TransformNormal(Vector3.UnitZ, view) };
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
            var originBrush = new SolidColorBrush(new Vector4(0.85f, 0.85f, 0.85f, 1f));
            context.FillCircle(originBrush, center, 3.5f);

            var labelBrush = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
            var tipBorderPen = new Pen(new SolidColorBrush(new Vector4(1f, 1f, 1f, 0.9f)), 1f);

            // Draw axes in depth order
            void DrawAxis(ProjectedAxis axis)
            {
                var linePen = new Pen(new SolidColorBrush(axis.Color), 2f);
                context.DrawLine(linePen, center, axis.ProjPos);

                var tipBrush = new SolidColorBrush(axis.Color);
                context.FillCircle(tipBrush, axis.ProjPos, tipRadius);
                context.DrawCircle(null, tipBorderPen, axis.ProjPos, tipRadius);

                context.DrawText(axis.Label, font, 10f, labelBrush, axis.ProjPos + new Vector2(-3.5f, -5.5f), isBold: true);
            }

            DrawAxis(first);
            DrawAxis(second);
            DrawAxis(third);
        }
    }
}
