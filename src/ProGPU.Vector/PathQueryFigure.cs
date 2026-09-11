using System.Numerics;

namespace ProGPU.Vector;

/// <summary>Canonical query topology, not a native ABI record. The caller owns the compiled arrays.</summary>
#if PROGPU_VECTOR_INTERNAL
internal
#else
public
#endif
readonly record struct PathQueryFigure(Vector2 Start, int FirstSegment, int SegmentCount, bool Closed, bool Filled);
