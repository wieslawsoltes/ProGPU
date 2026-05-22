using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.WebGPU;
using ProGPU.Backend;
using ProGPU.Vector;

namespace ProGPU.Text;

public struct GlyphInfo
{
    public uint X;
    public uint Y;
    public uint Width;
    public uint Height;
    public float BearX;
    public float BearY;
    public float Advance;
    
    // UV coordinates inside the atlas texture
    public Vector2 TexCoordMin;
    public Vector2 TexCoordMax;
}

public class GlyphAtlas : IDisposable
{
    private readonly WgpuContext _context;
    private readonly GpuTexture _atlasTexture;
    private readonly uint _atlasSize;

    // Packing state (Shelf Packer)
    private uint _currentX = 2;
    private uint _currentY = 2;
    private uint _currentRowHeight = 0;

    private readonly Dictionary<(TtfFont font, char character), GlyphInfo> _glyphs = new();
    
    private bool _isDisposed;

    public GpuTexture AtlasTexture => _atlasTexture;

    public GlyphAtlas(WgpuContext context, uint atlasSize = 1024)
    {
        _context = context;
        _atlasSize = atlasSize;
        
        // Use R8Unorm for dynamic alpha mapping (highly memory efficient)
        _atlasTexture = new GpuTexture(
            _context, 
            _atlasSize, 
            _atlasSize, 
            TextureFormat.R8Unorm, 
            TextureUsage.TextureBinding | TextureUsage.CopyDst, 
            "Dynamic Glyph Atlas"
        );

        // Fill with zero initially to clear the atlas texture
        byte[] clearData = new byte[_atlasSize * _atlasSize];
        _atlasTexture.WritePixels(new ReadOnlySpan<byte>(clearData));
    }

    public GlyphInfo GetOrCreateGlyph(TtfFont font, char character, float size)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GlyphAtlas));
        
        var key = (font, character);
        if (!_glyphs.TryGetValue(key, out var baseInfo))
        {
            ushort glyphIdx = font.GetGlyphIndex(character);

            // Handle space or control characters (empty outlines)
            var outline = font.GetGlyphOutline(glyphIdx);
            if (outline == null || character == ' ' || character == '\t' || character == '\n' || character == '\r')
            {
                baseInfo = new GlyphInfo
                {
                    X = 0, Y = 0, Width = 0, Height = 0,
                    BearX = 0, BearY = 0, Advance = 0f,
                    TexCoordMin = Vector2.Zero, TexCoordMax = Vector2.Zero
                };
            }
            else
            {
                // Rasterize outline into base size SDF alpha map
                var glyph = GlyphRasterizer.Rasterize(outline, font, GlyphRasterizer.SdfBaseSize);
                if (glyph.Width == 0 || glyph.Height == 0)
                {
                    baseInfo = new GlyphInfo
                    {
                        X = 0, Y = 0, Width = 0, Height = 0,
                        BearX = 0, BearY = 0, Advance = 0f,
                        TexCoordMin = Vector2.Zero, TexCoordMax = Vector2.Zero
                    };
                }
                else
                {
                    // Shelf Packing placement
                    uint gW = (uint)glyph.Width;
                    uint gH = (uint)glyph.Height;

                    if (_currentX + gW + 2 > _atlasSize)
                    {
                        // Row is full, wrap to next shelf row
                        _currentX = 2;
                        _currentY += _currentRowHeight + 2;
                        _currentRowHeight = 0;
                    }

                    if (_currentY + gH + 2 > _atlasSize)
                    {
                        // Atlas is entirely out of space, reset packer
                        Console.WriteLine("[GlyphAtlas] Warning: Texture Atlas is full! Clearing cache.");
                        _glyphs.Clear();
                        _currentX = 2;
                        _currentY = 2;
                        _currentRowHeight = 0;

                        byte[] clearData = new byte[_atlasSize * _atlasSize];
                        _atlasTexture.WritePixels(new ReadOnlySpan<byte>(clearData));
                    }

                    uint posX = _currentX;
                    uint posY = _currentY;

                    // Advance packer
                    _currentX += gW + 2;
                    _currentRowHeight = Math.Max(_currentRowHeight, gH);

                    // Upload glyph pixels directly to the atlas GPU texture
                    _atlasTexture.WritePixelsSubRect(
                        new ReadOnlySpan<byte>(glyph.AlphaMap), 
                        posX, 
                        posY, 
                        gW, 
                        gH
                    );

                    // Compute UV texture coordinates
                    float texelSize = 1.0f / _atlasSize;
                    var uvMin = new Vector2(posX * texelSize, posY * texelSize);
                    var uvMax = new Vector2((posX + gW) * texelSize, (posY + gH) * texelSize);

                    baseInfo = new GlyphInfo
                    {
                        X = posX,
                        Y = posY,
                        Width = gW,
                        Height = gH,
                        BearX = glyph.BearX,
                        BearY = glyph.BearY,
                        Advance = 0f,
                        TexCoordMin = uvMin,
                        TexCoordMax = uvMax
                    };
                }
            }
            _glyphs[key] = baseInfo;
        }

        // Scale layout parameters (BearX, BearY, Width, Height) from SdfBaseSize to the requested font size.
        // The Advance should be fetched at the requested target size.
        ushort idx = font.GetGlyphIndex(character);
        float advance = font.GetAdvanceWidth(idx, size);

        float scale = size / GlyphRasterizer.SdfBaseSize;

        uint scaledWidth = (uint)Math.Round(baseInfo.Width * scale);
        uint scaledHeight = (uint)Math.Round(baseInfo.Height * scale);
        if (baseInfo.Width > 0 && scaledWidth == 0) scaledWidth = 1;
        if (baseInfo.Height > 0 && scaledHeight == 0) scaledHeight = 1;

        return new GlyphInfo
        {
            X = baseInfo.X,
            Y = baseInfo.Y,
            Width = scaledWidth,
            Height = scaledHeight,
            BearX = baseInfo.BearX * scale,
            BearY = baseInfo.BearY * scale,
            Advance = advance,
            TexCoordMin = baseInfo.TexCoordMin,
            TexCoordMax = baseInfo.TexCoordMax
        };
    }

    public void Dispose()
    {
        if (_isDisposed) return;

        _atlasTexture.Dispose();
        _glyphs.Clear();

        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    ~GlyphAtlas()
    {
        Dispose();
    }
}
