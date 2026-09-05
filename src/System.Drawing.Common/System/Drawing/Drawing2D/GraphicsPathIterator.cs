namespace System.Drawing.Drawing2D;

public sealed class GraphicsPathIterator : MarshalByRefObject, IDisposable
{
    private readonly PointF[] _points;
    private readonly byte[] _types;
    private readonly FillMode _fillMode;
    private int _markerPosition;
    private int _subpathPosition;
    private int _typePosition;
    private bool _disposed;

    public GraphicsPathIterator(GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        _fillMode = path.FillMode;
        _points = path.PathPoints;
        _types = path.PathTypes;
    }

    ~GraphicsPathIterator() => Dispose(disposing: false);

    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return _points.Length;
        }
    }

    public int SubpathCount
    {
        get
        {
            ThrowIfDisposed();
            int count = 0;
            for (int index = 0; index < _types.Length; index++)
            {
                if (GetBaseType(_types[index]) == (byte)PathPointType.Start) count++;
            }

            return count;
        }
    }

    public int CopyData(ref PointF[] points, ref byte[] types, int startIndex, int endIndex)
    {
        ThrowIfDisposed();
        int count = ValidateRange(startIndex, endIndex);
        if (points == null || points.Length < count) points = new PointF[count];
        if (types == null || types.Length < count) types = new byte[count];
        return CopyData(points, types, startIndex, endIndex);
    }

    public int CopyData(Span<PointF> points, Span<byte> types, int startIndex, int endIndex)
    {
        ThrowIfDisposed();
        int count = ValidateRange(startIndex, endIndex);
        if (points.Length < count || types.Length < count)
        {
            throw new ArgumentException("Destination is too short.");
        }

        _points.AsSpan(startIndex, count).CopyTo(points);
        _types.AsSpan(startIndex, count).CopyTo(types);
        return count;
    }

    public int Enumerate(ref PointF[] points, ref byte[] types)
    {
        ThrowIfDisposed();
        if (points == null || points.Length < Count) points = new PointF[Count];
        if (types == null || types.Length < Count) types = new byte[Count];
        return Enumerate(points, types);
    }

    public int Enumerate(Span<PointF> points, Span<byte> types)
    {
        ThrowIfDisposed();
        if (points.Length < Count || types.Length < Count)
        {
            throw new ArgumentException("Destination is too short.");
        }

        _points.CopyTo(points);
        _types.CopyTo(types);
        return Count;
    }

    public bool HasCurve()
    {
        ThrowIfDisposed();
        for (int index = 0; index < _types.Length; index++)
        {
            if (GetBaseType(_types[index]) == (byte)PathPointType.Bezier3) return true;
        }

        return false;
    }

    public int NextMarker(out int startIndex, out int endIndex)
    {
        ThrowIfDisposed();
        if (_markerPosition >= Count)
        {
            startIndex = 0;
            endIndex = 0;
            return 0;
        }

        startIndex = _markerPosition;
        endIndex = Count - 1;
        for (int index = _markerPosition; index < Count; index++)
        {
            if ((_types[index] & (byte)PathPointType.PathMarker) != 0)
            {
                endIndex = index;
                break;
            }
        }

        _markerPosition = endIndex + 1;
        return endIndex - startIndex + 1;
    }

    public int NextMarker(GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int count = NextMarker(out int startIndex, out int endIndex);
        CopyRangeToPath(path, startIndex, endIndex, count);
        return count;
    }

    public int NextPathType(out byte pathType, out int startIndex, out int endIndex)
    {
        ThrowIfDisposed();
        if (_typePosition >= Count)
        {
            pathType = 0;
            startIndex = 0;
            endIndex = 0;
            return 0;
        }

        startIndex = _typePosition;
        pathType = GetBaseType(_types[startIndex]);
        endIndex = startIndex;
        while (endIndex + 1 < Count && GetBaseType(_types[endIndex + 1]) == pathType)
        {
            endIndex++;
        }

        _typePosition = endIndex + 1;
        return endIndex - startIndex + 1;
    }

    public int NextSubpath(out int startIndex, out int endIndex, out bool isClosed)
    {
        ThrowIfDisposed();
        startIndex = FindNextSubpathStart(_subpathPosition);
        if (startIndex < 0)
        {
            startIndex = 0;
            endIndex = 0;
            isClosed = false;
            return 0;
        }

        endIndex = Count - 1;
        for (int index = startIndex + 1; index < Count; index++)
        {
            if (GetBaseType(_types[index]) == (byte)PathPointType.Start)
            {
                endIndex = index - 1;
                break;
            }
        }

        isClosed = (_types[endIndex] & (byte)PathPointType.CloseSubpath) != 0;
        _subpathPosition = endIndex + 1;
        return endIndex - startIndex + 1;
    }

    public int NextSubpath(GraphicsPath path, out bool isClosed)
    {
        ArgumentNullException.ThrowIfNull(path);
        int count = NextSubpath(out int startIndex, out int endIndex, out isClosed);
        CopyRangeToPath(path, startIndex, endIndex, count);
        return count;
    }

    public void Rewind()
    {
        ThrowIfDisposed();
        _markerPosition = 0;
        _subpathPosition = 0;
        _typePosition = 0;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing) => _disposed = true;

    private int ValidateRange(int startIndex, int endIndex)
    {
        if (startIndex < 0 || endIndex < startIndex || endIndex >= Count)
        {
            throw new ArgumentException("Parameter is not valid.");
        }

        return endIndex - startIndex + 1;
    }

    private int FindNextSubpathStart(int position)
    {
        for (int index = position; index < Count; index++)
        {
            if (GetBaseType(_types[index]) == (byte)PathPointType.Start) return index;
        }

        return -1;
    }

    private void CopyRangeToPath(GraphicsPath path, int startIndex, int endIndex, int count)
    {
        path.Reset();
        path.FillMode = _fillMode;
        if (count == 0) return;

        int copyStart = startIndex;
        if (GetBaseType(_types[startIndex]) != (byte)PathPointType.Start)
        {
            while (copyStart > 0 && GetBaseType(_types[copyStart - 1]) == (byte)PathPointType.Bezier3)
            {
                copyStart--;
            }

            if (copyStart > 0) copyStart--;
        }

        int copyCount = endIndex - copyStart + 1;
        var points = new PointF[copyCount];
        var types = new byte[copyCount];
        _points.AsSpan(copyStart, copyCount).CopyTo(points);
        _types.AsSpan(copyStart, copyCount).CopyTo(types);
        types[0] = (byte)((types[0] & ~(byte)PathPointType.PathTypeMask) | (byte)PathPointType.Start);
        using var range = new GraphicsPath(points, types, _fillMode);
        path.AddPath(range, connect: false);
    }

    private static byte GetBaseType(byte type) => (byte)(type & (byte)PathPointType.PathTypeMask);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
