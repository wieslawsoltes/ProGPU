using System;
using System.Numerics;
using ProGPU.Scene;

namespace Microsoft.UI.Xaml.Media
{
    public class GeneralTransform : ProGPU.Scene.GeneralTransform
    {
        public GeneralTransform(Matrix4x4 matrix) : base(matrix)
        {
        }
    }
}
