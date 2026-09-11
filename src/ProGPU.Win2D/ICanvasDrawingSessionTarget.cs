using System.Numerics;
using ProGPU.Scene;

namespace Microsoft.Graphics.Canvas;

internal interface ICanvasDrawingSessionTarget :
    ICanvasResourceCreatorWithDpi
{
    Windows.Foundation.Rect DrawingBounds { get; }

    void ValidateClear();

    void Commit(
        GpuPicture sessionPicture,
        bool hasClear,
        Vector4 clearColor);

    void EndSession();
}
