using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.Text;
using ProGPU.Vector;
using netDxf.Entities;

namespace ProGPU.Dxf;

public class DxfRenderContext
{
    public DrawingContext DrawingContext { get; set; }
    
    // Viewport and projection parameters
    public float Zoom { get; set; } = 1.0f;
    public Vector2 Pan { get; set; } = Vector2.Zero;
    public Vector2 Center { get; set; } = Vector2.Zero;
    public Vector2 ScreenCenter { get; set; } = Vector2.Zero;
    
    private netDxf.DxfDocument? _document;
    
    // Active document reference for layout and space rendering
    public netDxf.DxfDocument? Document
    {
        get => _document;
        set
        {
            if (_document != value)
            {
                _document = value;
                _flatWcsEntities.Clear();
                CachedActiveLayout = null;
            }
        }
    }
    
    // Level of Detail rendering optimization flag
    public bool EnableLod { get; set; } = false;

    // GPU camera transform optimization flag
    public bool EnableGpuTransforms { get; set; } = false;

    // Static buffer compilation flag to bypass culling and micro-LOD
    public bool IsCompilingStatic { get; set; } = false;

    // Entity flattening optimization flag
    public bool EnableFlattening { get; set; } = true;
    
    // Font and Styling fallback
    public TtfFont Font { get; set; }
    public Brush FallbackBrush { get; set; } = new SolidColorBrush(new Vector4(1f, 1f, 1f, 1f));
    public Brush BackgroundBrush { get; set; } = new SolidColorBrush(new Vector4(0.12f, 0.12f, 0.14f, 1f));
    
    // Theme and visibility settings
    public HashSet<string> ActiveLayers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vector4> LayerColors { get; } = new(StringComparer.OrdinalIgnoreCase);
    
    // Matrix transform stack for nested inserts/blocks
    private readonly Stack<Matrix4x4> _transformStack = new();
    
    public Matrix4x4 CurrentTransform { get; private set; } = Matrix4x4.Identity;

    public DxfRenderContext(DrawingContext drawingContext, TtfFont defaultFont)
    {
        DrawingContext = drawingContext;
        Font = defaultFont;
    }

    /// <summary>
    /// Transforms a DXF world coordinate (Y-up) to screen coordinate (Y-down) 
    /// considering Center, Zoom, ScreenCenter, and Pan.
    /// </summary>
    public Vector2 TransformToScreen(Vector2 worldPoint)
    {
        if (EnableGpuTransforms)
        {
            return worldPoint;
        }

        // 1. Center the world coordinate (relative to the DXF model's center)
        float localX = worldPoint.X - Center.X;
        float localY = worldPoint.Y - Center.Y;
        
        // 2. Scale and project with Y inverted (CAD is Y-up, screen is Y-down)
        float screenX = localX * Zoom + ScreenCenter.X + Pan.X;
        float screenY = -localY * Zoom + ScreenCenter.Y + Pan.Y;
        
        return new Vector2(screenX, screenY);
    }

    /// <summary>
    /// Transforms a point first by the active block matrix stack, and then projects to screen.
    /// </summary>
    public Vector2 Transform(Vector2 localPoint, Matrix4x4 modelMatrix)
    {
        var v3 = new Vector3(localPoint.X, localPoint.Y, 0f);
        var v3Transformed = Vector3.Transform(v3, modelMatrix);
        return TransformToScreen(new Vector2(v3Transformed.X, v3Transformed.Y));
    }

    public Vector2 Transform(Vector3 localPoint, Matrix4x4 modelMatrix)
    {
        var v3Transformed = Vector3.Transform(localPoint, modelMatrix);
        return TransformToScreen(new Vector2(v3Transformed.X, v3Transformed.Y));
    }

    public void PushTransform(Matrix4x4 transform)
    {
        _transformStack.Push(CurrentTransform);
        CurrentTransform = transform * CurrentTransform;
    }

    public void PopTransform()
    {
        if (_transformStack.Count > 0)
        {
            CurrentTransform = _transformStack.Pop();
        }
        else
        {
            CurrentTransform = Matrix4x4.Identity;
        }
    }

    /// <summary>
    /// Transforms a 3D point from CAD world space to screen space, keeping the Z coordinate.
    /// </summary>
    public Vector3 TransformToScreen3D(Vector3 worldPoint, Matrix4x4 modelMatrix)
    {
        var v3Transformed = Vector3.Transform(worldPoint, modelMatrix);
        if (EnableGpuTransforms)
        {
            return v3Transformed;
        }

        float localX = v3Transformed.X - Center.X;
        float localY = v3Transformed.Y - Center.Y;
        
        float screenX = localX * Zoom + ScreenCenter.X + Pan.X;
        float screenY = -localY * Zoom + ScreenCenter.Y + Pan.Y;
        float screenZ = v3Transformed.Z * Zoom;
        
        return new Vector3(screenX, screenY, screenZ);
    }

    /// <summary>
    /// Checks if the given screen-space bounding box is completely off-screen.
    /// Uses a small safety padding to prevent abrupt clipping artifacts.
    /// </summary>
    public bool IsOffScreen(Vector2 minScreen, Vector2 maxScreen)
    {
        if (IsCompilingStatic) return false;

        if (EnableGpuTransforms)
        {
            // If GPU transforms are active, minScreen and maxScreen are raw WCS coords.
            // Temporarily project them just for the culling check to avoid rendering culled geometry.
            Vector2 sMin = new Vector2(
                (minScreen.X - Center.X) * Zoom + ScreenCenter.X + Pan.X,
                -(maxScreen.Y - Center.Y) * Zoom + ScreenCenter.Y + Pan.Y
            );
            Vector2 sMax = new Vector2(
                (maxScreen.X - Center.X) * Zoom + ScreenCenter.X + Pan.X,
                -(minScreen.Y - Center.Y) * Zoom + ScreenCenter.Y + Pan.Y
            );
            minScreen = Vector2.Min(sMin, sMax);
            maxScreen = Vector2.Max(sMin, sMax);
        }

        float w = ScreenCenter.X * 2f;
        float h = ScreenCenter.Y * 2f;
        if (w <= 0f || h <= 0f) return false; // Viewport not yet sized, do not cull
        
        const float padding = 50f;
        return maxScreen.X < -padding || minScreen.X > w + padding || 
               maxScreen.Y < -padding || minScreen.Y > h + padding;
    }


    // Dxf-specific brush and pen caches to prevent high-frequency GC allocations
    private readonly Dictionary<(string Layer, float R, float G, float B, float A), Brush> _brushCache = new();
    private readonly Dictionary<(string Layer, float R, float G, float B, float A, float Thickness), Pen> _penCache = new();

    public Brush GetCachedBrush(netDxf.Entities.EntityObject entity)
    {
        var color = new Vector4(1f, 1f, 1f, 1f); // Default white/fallback
        
        if (entity.Color.IsByLayer)
        {
            if (LayerColors.TryGetValue(entity.Layer.Name, out var lColor))
            {
                color = lColor;
            }
            else
            {
                var aci = entity.Layer.Color;
                color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
            }
        }
        else
        {
            var aci = entity.Color;
            color = new Vector4(aci.R / 255f, aci.G / 255f, aci.B / 255f, 1f);
        }

        var key = (entity.Layer.Name, color.X, color.Y, color.Z, color.W);
        if (!_brushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(color);
            _brushCache[key] = brush;
        }
        return brush;
    }

    public Pen GetCachedPen(netDxf.Entities.EntityObject entity, float thickness)
    {
        var brush = GetCachedBrush(entity);
        var color = (brush is SolidColorBrush solid) ? solid.Color : new Vector4(1f, 1f, 1f, 1f);
        
        var key = (entity.Layer.Name, color.X, color.Y, color.Z, color.W, thickness);
        if (!_penCache.TryGetValue(key, out var pen))
        {
            pen = new Pen(brush, thickness);
            _penCache[key] = pen;
        }
        return pen;
    }

    private string? _filePath;
    public string? FilePath 
    { 
        get => _filePath;
        set 
        {
            if (_filePath != value)
            {
                _filePath = value;
                _cached3dSolids.Clear();
                _cachedMLeaders.Clear();
                _hatchCache.Clear();
                _flatWcsEntities.Clear();
                if (!string.IsNullOrEmpty(_filePath) && System.IO.File.Exists(_filePath))
                {
                    try
                    {
                        ParseAndCache3dSolids(_filePath);
                        ParseAndCacheMLeaders(_filePath);
                    }
                    catch
                    {
                        // Ignore parsing errors and fall back gracefully
                    }
                }
            }
        }
    }

    private readonly List<Dxf3dSolid> _cached3dSolids = new();
    public IReadOnlyList<Dxf3dSolid> Cached3dSolids => _cached3dSolids;

    private readonly List<DxfMLeader> _cachedMLeaders = new();
    public IReadOnlyList<DxfMLeader> CachedMLeaders => _cachedMLeaders;

    private readonly Dictionary<netDxf.Entities.Hatch, HatchCacheEntry> _hatchCache = new();
    public Dictionary<netDxf.Entities.Hatch, HatchCacheEntry> HatchCache => _hatchCache;

    public int Solid3DCount { get; set; } = 0;

    public string? CachedActiveLayout { get; set; }

    public class FlatWcsEntity
    {
        public object Entity { get; }
        public Matrix4x4 Transform { get; }
        public List<string>? ParentInsertLayers { get; }
        public float ScaleY { get; }

        public FlatWcsEntity(object entity, Matrix4x4 transform, List<string>? parentInsertLayers = null, float scaleY = 1.0f)
        {
            Entity = entity;
            Transform = transform;
            ParentInsertLayers = parentInsertLayers;
            ScaleY = scaleY;
        }
    }

    private readonly List<FlatWcsEntity> _flatWcsEntities = new();
    public IReadOnlyList<FlatWcsEntity> FlatWcsEntities => _flatWcsEntities;

    public void FlattenDxfEntities(netDxf.DxfDocument doc)
    {
        _flatWcsEntities.Clear();
        CachedActiveLayout = doc.ActiveLayout;
        
        bool flattenedFromLayout = false;
        if (doc.Layouts != null && !string.IsNullOrEmpty(doc.ActiveLayout) && doc.Layouts.Contains(doc.ActiveLayout))
        {
            var layout = doc.Layouts[doc.ActiveLayout];
            if (layout.AssociatedBlock != null && layout.AssociatedBlock.Entities != null && layout.AssociatedBlock.Entities.Count > 0)
            {
                foreach (var entity in layout.AssociatedBlock.Entities)
                {
                    FlattenEntityRecursive(entity, Matrix4x4.Identity);
                }
                flattenedFromLayout = true;
            }
        }

        if (!flattenedFromLayout)
        {
            foreach (var line in doc.Lines) FlattenEntityRecursive(line, Matrix4x4.Identity);
            foreach (var circle in doc.Circles) FlattenEntityRecursive(circle, Matrix4x4.Identity);
            foreach (var arc in doc.Arcs) FlattenEntityRecursive(arc, Matrix4x4.Identity);
            foreach (var ellipse in doc.Ellipses) FlattenEntityRecursive(ellipse, Matrix4x4.Identity);
            foreach (var lw in doc.LwPolylines) FlattenEntityRecursive(lw, Matrix4x4.Identity);
            foreach (var poly in doc.Polylines) FlattenEntityRecursive(poly, Matrix4x4.Identity);
            foreach (var spline in doc.Splines) FlattenEntityRecursive(spline, Matrix4x4.Identity);
            foreach (var txt in doc.Texts) FlattenEntityRecursive(txt, Matrix4x4.Identity);
            foreach (var mtxt in doc.MTexts) FlattenEntityRecursive(mtxt, Matrix4x4.Identity);
            foreach (var ins in doc.Inserts) FlattenEntityRecursive(ins, Matrix4x4.Identity);
            foreach (var solid in doc.Solids) FlattenEntityRecursive(solid, Matrix4x4.Identity);
            foreach (var hatch in doc.Hatches) FlattenEntityRecursive(hatch, Matrix4x4.Identity);
            foreach (var img in doc.Images) FlattenEntityRecursive(img, Matrix4x4.Identity);
            foreach (var face in doc.Faces3d) FlattenEntityRecursive(face, Matrix4x4.Identity);
            foreach (var pt in doc.Points) FlattenEntityRecursive(pt, Matrix4x4.Identity);
            foreach (var wo in doc.Wipeouts) FlattenEntityRecursive(wo, Matrix4x4.Identity);
        }
    }

    private void FlattenEntityRecursive(netDxf.Entities.EntityObject entity, Matrix4x4 currentTransform, List<string>? parentInsertLayers = null)
    {
        if (entity is Insert insert)
        {
            var scale = insert.Scale;
            var pos = insert.Position;
            float radAngle = (float)(insert.Rotation * Math.PI / 180.0);
            var origin = insert.Block.Origin;

            var localMat = Matrix4x4.CreateTranslation(-(float)origin.X, -(float)origin.Y, -(float)origin.Z) *
                           Matrix4x4.CreateScale((float)scale.X, (float)scale.Y, (float)scale.Z) *
                           Matrix4x4.CreateRotationZ(radAngle) *
                           DxfDocumentRenderer.GetOcsMatrix(insert.Normal) *
                           Matrix4x4.CreateTranslation((float)pos.X, (float)pos.Y, (float)pos.Z);

            var combined = localMat * currentTransform;

            var nextParentLayers = parentInsertLayers != null ? new List<string>(parentInsertLayers) : new List<string>();
            if (insert.Layer != null && !string.IsNullOrEmpty(insert.Layer.Name))
            {
                nextParentLayers.Add(insert.Layer.Name);
            }

            foreach (var childEntity in insert.Block.Entities)
            {
                FlattenEntityRecursive(childEntity, combined, nextParentLayers);
            }
            
            foreach (var attr in insert.Attributes)
            {
                var flatAttr = new FlatWcsEntity(attr, currentTransform, nextParentLayers, (float)insert.Scale.Y);
                _flatWcsEntities.Add(flatAttr);
            }
        }
        else
        {
            _flatWcsEntities.Add(new FlatWcsEntity(entity, currentTransform, parentInsertLayers));
        }
    }

    private readonly Dictionary<netDxf.Blocks.Block, (Vector2 Min, Vector2 Max)> _blockBoundsCache = new();

    public (Vector2 Min, Vector2 Max) GetOrCalculateBlockBounds(netDxf.Blocks.Block block)
    {
        if (!_blockBoundsCache.TryGetValue(block, out var bounds))
        {
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            bool hasPoints = false;

            foreach (var ent in block.Entities)
            {
                if (ent is Line line)
                {
                    minX = Math.Min(minX, (float)Math.Min(line.StartPoint.X, line.EndPoint.X));
                    minY = Math.Min(minY, (float)Math.Min(line.StartPoint.Y, line.EndPoint.Y));
                    maxX = Math.Max(maxX, (float)Math.Max(line.StartPoint.X, line.EndPoint.X));
                    maxY = Math.Max(maxY, (float)Math.Max(line.StartPoint.Y, line.EndPoint.Y));
                    hasPoints = true;
                }
                else if (ent is LwPolyline lw)
                {
                    foreach (var v in lw.Vertexes)
                    {
                        minX = Math.Min(minX, (float)v.Position.X);
                        minY = Math.Min(minY, (float)v.Position.Y);
                        maxX = Math.Max(maxX, (float)v.Position.X);
                        maxY = Math.Max(maxY, (float)v.Position.Y);
                        hasPoints = true;
                    }
                }
                else if (ent is Circle circle)
                {
                    float cx = (float)circle.Center.X;
                    float cy = (float)circle.Center.Y;
                    float r = (float)circle.Radius;
                    minX = Math.Min(minX, cx - r);
                    minY = Math.Min(minY, cy - r);
                    maxX = Math.Max(maxX, cx + r);
                    maxY = Math.Max(maxY, cy + r);
                    hasPoints = true;
                }
                else if (ent is Arc arc)
                {
                    float cx = (float)arc.Center.X;
                    float cy = (float)arc.Center.Y;
                    float r = (float)arc.Radius;
                    minX = Math.Min(minX, cx - r);
                    minY = Math.Min(minY, cy - r);
                    maxX = Math.Max(maxX, cx + r);
                    maxY = Math.Max(maxY, cy + r);
                    hasPoints = true;
                }
            }

            if (!hasPoints)
            {
                minX = minY = maxX = maxY = 0f;
            }

            bounds = (new Vector2(minX, minY), new Vector2(maxX, maxY));
            _blockBoundsCache[block] = bounds;
        }
        return bounds;
    }

    private void ParseAndCache3dSolids(string path)
    {
        var satBlocks = new List<(string Layer, string Sat)>();
        var sabBlocks = new List<(string Layer, byte[] Sab)>();
        
        using var reader = new System.IO.StreamReader(path);
        string? line;
        
        bool collectingSat = false;
        var currentBlock = new System.Text.StringBuilder();
        
        string currentEntity = "";
        string currentSolidLayer = "0";
        string currentSatLayer = "0";
        var currentSabBytes = new List<byte>();

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();

            // Read group code
            if (int.TryParse(line, out int code))
            {
                string? val = reader.ReadLine()?.Trim();
                if (val == null) continue;

                if (code == 0)
                {
                    // New entity starts
                    if (currentSabBytes.Count > 0)
                    {
                        sabBlocks.Add((currentSolidLayer, currentSabBytes.ToArray()));
                        currentSabBytes.Clear();
                    }

                    currentEntity = val;
                    collectingSat = false;
                    currentSolidLayer = "0";
                }
                else if (currentEntity == "3DSOLID" || currentEntity == "REGION" || currentEntity == "BODY")
                {
                    if (code == 8)
                    {
                        currentSolidLayer = val;
                    }
                    else if (code == 1 || code == 3)
                    {
                        // Check if this is the start of an ACIS block
                        if (!collectingSat && (val.Contains("sb_version") || val.Contains("20800 0 1 0") || val.Contains("40000 0 1 0") || val.Contains("21200 0 1 0")))
                        {
                            collectingSat = true;
                            currentBlock.Clear();
                            currentSatLayer = currentSolidLayer;
                        }

                        if (collectingSat)
                        {
                            currentBlock.AppendLine(val);
                            if (val.Contains("End of ACIS") || val.Contains("End of ACIS Solid"))
                            {
                                collectingSat = false;
                                satBlocks.Add((currentSatLayer, currentBlock.ToString()));
                            }
                        }
                    }
                    else if (code == 310)
                    {
                        // Hex-encoded binary SAB
                        try
                        {
                            byte[] chunk = ConvertHexStringToBytes(val);
                            currentSabBytes.AddRange(chunk);
                        }
                        catch
                        {
                            // Ignore corrupt hex lines
                        }
                    }
                }
            }
        }

        // Flush any remaining SAB bytes
        if (currentSabBytes.Count > 0)
        {
            sabBlocks.Add((currentSolidLayer, currentSabBytes.ToArray()));
        }

        // Parse SAT blocks
        foreach (var sat in satBlocks)
        {
            try
            {
                var edges = AcisSatParser.ParseSat(sat.Sat);
                if (edges.Count > 0)
                {
                    var solid = new Dxf3dSolid { Layer = sat.Layer };
                    solid.Edges.AddRange(edges);
                    _cached3dSolids.Add(solid);
                }
            }
            catch
            {
                // Skip invalid blocks
            }
        }

        // Parse SAB blocks
        foreach (var sab in sabBlocks)
        {
            try
            {
                var edges = AcisSabParser.ParseSab(sab.Sab);
                if (edges.Count > 0)
                {
                    var solid = new Dxf3dSolid { Layer = sab.Layer };
                    solid.Edges.AddRange(edges);
                    _cached3dSolids.Add(solid);
                }
            }
            catch
            {
                // Skip invalid blocks
            }
        }
    }

    private static byte[] ConvertHexStringToBytes(string hex)
    {
        hex = hex.Replace(" ", "");
        if (hex.Length % 2 != 0)
        {
            hex = hex.Substring(0, hex.Length - 1);
        }
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    private void ParseAndCacheMLeaders(string path)
    {
        using var reader = new System.IO.StreamReader(path);
        string? line;
        
        DxfMLeader? currentMLeader = null;
        bool inContextData = false;
        bool inLeaderLine = false;
        
        var currentLeaderLinePoints = new List<Vector3>();
        
        float cx = 0f, cy = 0f, cz = 0f;
        bool hasX = false, hasY = false;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (int.TryParse(line, out int code))
            {
                string? val = reader.ReadLine()?.Trim();
                if (val == null) continue;

                if (code == 0)
                {
                    if (currentMLeader != null)
                    {
                        _cachedMLeaders.Add(currentMLeader);
                        currentMLeader = null;
                    }

                    if (val == "MULTILEADER")
                    {
                        currentMLeader = new DxfMLeader();
                        inContextData = false;
                        inLeaderLine = false;
                    }
                    continue;
                }

                if (currentMLeader != null)
                {
                    if (code == 8)
                    {
                        currentMLeader.Layer = val;
                    }
                    else if (code == 300 && val == "CONTEXT_DATA{")
                    {
                        inContextData = true;
                    }
                    else if (code == 301 && val == "}")
                    {
                        inContextData = false;
                    }
                    else if (code == 304 && val == "LEADER_LINE{")
                    {
                        inLeaderLine = true;
                        currentLeaderLinePoints = new List<Vector3>();
                        hasX = hasY = false;
                        cx = cy = cz = 0f;
                    }
                    else if (code == 305 && val == "}")
                    {
                        if (inLeaderLine)
                        {
                            if (hasX && hasY)
                            {
                                currentLeaderLinePoints.Add(new Vector3(cx, cy, cz));
                            }
                            if (currentLeaderLinePoints.Count > 0)
                            {
                                currentMLeader.LeaderLines.Add(currentLeaderLinePoints);
                            }
                            inLeaderLine = false;
                        }
                    }
                    else if (inContextData)
                    {
                        if (code == 10)
                        {
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x))
                                currentMLeader.TextInsertionPoint = new Vector3(x, currentMLeader.TextInsertionPoint.Y, currentMLeader.TextInsertionPoint.Z);
                        }
                        else if (code == 20)
                        {
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                                currentMLeader.TextInsertionPoint = new Vector3(currentMLeader.TextInsertionPoint.X, y, currentMLeader.TextInsertionPoint.Z);
                        }
                        else if (code == 30)
                        {
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                                currentMLeader.TextInsertionPoint = new Vector3(currentMLeader.TextInsertionPoint.X, currentMLeader.TextInsertionPoint.Y, z);
                        }
                        else if (code == 41 || code == 140)
                        {
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float h))
                                currentMLeader.TextHeight = h;
                        }
                        else if (code == 304)
                        {
                            currentMLeader.TextValue = val;
                        }
                    }
                    else if (inLeaderLine)
                    {
                        if (code == 10)
                        {
                            if (hasX && hasY)
                            {
                                currentLeaderLinePoints.Add(new Vector3(cx, cy, cz));
                                hasX = hasY = false;
                                cz = 0f;
                            }
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x))
                            {
                                cx = x;
                                hasX = true;
                            }
                        }
                        else if (code == 20)
                        {
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y))
                            {
                                cy = y;
                                hasY = true;
                            }
                        }
                        else if (code == 30)
                        {
                            if (float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                            {
                                cz = z;
                            }
                            if (hasX && hasY)
                            {
                                currentLeaderLinePoints.Add(new Vector3(cx, cy, cz));
                                hasX = hasY = false;
                                cz = 0f;
                            }
                        }
                    }
                }
            }
        }

        if (currentMLeader != null)
        {
            _cachedMLeaders.Add(currentMLeader);
        }
    }

    public void Reset()
    {
        _transformStack.Clear();
        CurrentTransform = Matrix4x4.Identity;
        Solid3DCount = 0;
        _blockBoundsCache.Clear();
    }
}

public class DxfMLeader
{
    public string Layer { get; set; } = "0";
    public Vector3 TextInsertionPoint { get; set; } = Vector3.Zero;
    public float TextHeight { get; set; } = 1.0f;
    public string TextValue { get; set; } = "";
    public List<List<Vector3>> LeaderLines { get; } = new();
}

public class Dxf3dSolid
{
    public string Layer { get; set; } = "0";
    public List<Acis3dEdge> Edges { get; } = new();
}

public class HatchCacheEntry
{
    public Vector2 MinModelBounds { get; set; }
    public Vector2 MaxModelBounds { get; set; }
    public float CachedZoom { get; set; } = -1f;
    public Vector2 CachedPan { get; set; } = new Vector2(float.NaN, float.NaN);
    public Matrix4x4 CachedTransform { get; set; } = Matrix4x4.Identity;
    public PathGeometry? CachedPathGeometry { get; set; }
    public List<(Vector2 Start, Vector2 End)>? ModelCpuLines { get; set; }
}
