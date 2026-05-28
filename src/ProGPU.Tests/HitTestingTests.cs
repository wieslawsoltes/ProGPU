using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;
using ProGPU.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace ProGPU.Tests
{
    public class HitTestingTests
    {
        [Fact]
        public void Test_GeneralTransform_TransformPoint()
        {
            // Create a translation transform matrix: shift right by 50, down by 100
            var matrix = Matrix4x4.CreateTranslation(50f, 100f, 0f);
            var transform = new GeneralTransform(matrix);

            var origin = new Vector2(0f, 0f);
            var transformed = transform.TransformPoint(origin);

            Assert.Equal(50f, transformed.X);
            Assert.Equal(100f, transformed.Y);
        }

        [Fact]
        public void Test_GeneralTransform_TransformBounds_Translation()
        {
            var matrix = Matrix4x4.CreateTranslation(10f, 20f, 0f);
            var transform = new GeneralTransform(matrix);

            var bounds = new Rect(0f, 0f, 100f, 50f);
            var transformedBounds = transform.TransformBounds(bounds);

            Assert.Equal(10f, transformedBounds.X);
            Assert.Equal(20f, transformedBounds.Y);
            Assert.Equal(100f, transformedBounds.Width);
            Assert.Equal(50f, transformedBounds.Height);
        }

        [Fact]
        public void Test_GeneralTransform_TransformBounds_Rotation()
        {
            // Rotate 90 degrees around Z axis (counter-clockwise)
            // (1, 0) -> (0, 1), (0, 1) -> (-1, 0)
            var matrix = Matrix4x4.CreateRotationZ(MathF.PI / 2f);
            var transform = new GeneralTransform(matrix);

            // Bounding box of rotated rect
            var bounds = new Rect(0f, 0f, 10f, 20f);
            var transformedBounds = transform.TransformBounds(bounds);

            // A 10x20 rect rotated 90 degrees becomes a 20x10 rect
            // The top-right corner (10,0) -> (0,10)
            // The bottom-right corner (10,20) -> (-20,10)
            // The bottom-left corner (0,20) -> (-20,0)
            // Thus, transformed bounds should be x: -20, y: 0, width: 20, height: 10
            Assert.True(MathF.Abs(transformedBounds.X - (-20f)) < 0.01f);
            Assert.True(MathF.Abs(transformedBounds.Y - 0f) < 0.01f);
            Assert.True(MathF.Abs(transformedBounds.Width - 20f) < 0.01f);
            Assert.True(MathF.Abs(transformedBounds.Height - 10f) < 0.01f);
        }

        [Fact]
        public void Test_RTree_STR_BulkLoading_And_Querying()
        {
            var rtree = new RTree<string>(maxDegree: 2);
            var entries = new List<RTreeEntry<string>>
            {
                new RTreeEntry<string>(new Rect(0f, 0f, 10f, 10f), "ItemA"),
                new RTreeEntry<string>(new Rect(20f, 0f, 10f, 10f), "ItemB"),
                new RTreeEntry<string>(new Rect(0f, 20f, 10f, 10f), "ItemC"),
                new RTreeEntry<string>(new Rect(20f, 20f, 10f, 10f), "ItemD")
            };

            rtree.Rebuild(entries);

            // Verify root node covers everything: 0 to 30 X, 0 to 30 Y
            Assert.NotNull(rtree.Root);
            Assert.Equal(0f, rtree.Root.Bounds.X);
            Assert.Equal(0f, rtree.Root.Bounds.Y);
            Assert.Equal(30f, rtree.Root.Bounds.Width);
            Assert.Equal(30f, rtree.Root.Bounds.Height);

            // Query at (5, 5) -> should hit only ItemA
            var resultsA = rtree.Query(new Vector2(5f, 5f));
            Assert.Single(resultsA);
            Assert.Contains("ItemA", resultsA);

            // Query at (25, 25) -> should hit only ItemD
            var resultsD = rtree.Query(new Vector2(25f, 25f));
            Assert.Single(resultsD);
            Assert.Contains("ItemD", resultsD);

            // Query in a region covering top half -> should hit ItemA and ItemB
            var resultsTop = rtree.Query(new Rect(0f, 0f, 30f, 15f));
            Assert.Equal(2, resultsTop.Count);
            Assert.Contains("ItemA", resultsTop);
            Assert.Contains("ItemB", resultsTop);
        }

        [Fact]
        public void Test_Visual_TransformToVisual_CumulativeMatrix()
        {
            var parent = new Canvas { Size = new Vector2(500f, 500f), Offset = new Vector2(10f, 10f) };
            var child = new Button { Size = new Vector2(100f, 50f), Offset = new Vector2(20f, 30f) };
            parent.Children.Add(child);

            // Transform child coordinates to root (null)
            var transformToRoot = child.TransformToVisual(null);
            var originTransformed = transformToRoot.TransformPoint(Vector2.Zero);

            // Cumulative offset: parent (10, 10) + child (20, 30) = (30, 40)
            Assert.Equal(30f, originTransformed.X);
            Assert.Equal(40f, originTransformed.Y);

            // Transform child coordinates to parent
            var transformToParent = child.TransformToVisual(parent);
            var originToParent = transformToParent.TransformPoint(Vector2.Zero);
            Assert.Equal(20f, originToParent.X);
            Assert.Equal(30f, originToParent.Y);
        }

        [Fact]
        public void Test_Visual_CenteredRotation_RenderTransformOrigin()
        {
            // Create a Visual of size 100x100 at offset (10, 20)
            var visual = new Visual
            {
                Size = new Vector2(100f, 100f),
                Offset = new Vector2(10f, 20f),
                RenderTransformOrigin = new Vector2(0.5f, 0.5f), // Default is 0.5, 0.5
                Rotation = MathF.PI // 180 degrees
            };

            var localTransform = visual.GetLocalTransform();

            // Transform center point (50, 50) of visual
            var centerLocal = new Vector4(50f, 50f, 0f, 1f);
            var centerTransformed = Vector4.Transform(centerLocal, localTransform);

            // Center of 100x100 visual at (10, 20) is (60, 70)
            // It should be invariant under centered rotation (except for translation to offset)
            Assert.True(MathF.Abs(centerTransformed.X - 60f) < 0.01f);
            Assert.True(MathF.Abs(centerTransformed.Y - 70f) < 0.01f);

            // Transform top-left point (0, 0)
            var topLeftLocal = new Vector4(0f, 0f, 0f, 1f);
            var topLeftTransformed = Vector4.Transform(topLeftLocal, localTransform);

            // (0,0) rotated 180 degrees around (50,50) is (100,100), plus offset (10,20) = (110,120)
            Assert.True(MathF.Abs(topLeftTransformed.X - 110f) < 0.01f);
            Assert.True(MathF.Abs(topLeftTransformed.Y - 120f) < 0.01f);

            // Now test custom CenterPoint override to maintain backward compatibility
            var visualCustom = new Visual
            {
                Size = new Vector2(100f, 100f),
                Offset = new Vector2(10f, 20f),
                CenterPoint = new Vector3(10f, 10f, 0f), // Custom center point
                Rotation = MathF.PI
            };

            var localTransformCustom = visualCustom.GetLocalTransform();

            // Custom center point (10, 10) should be invariant (except for offset) -> (10 + 10, 10 + 20) = (20, 30)
            var customCenterLocal = new Vector4(10f, 10f, 0f, 1f);
            var customCenterTransformed = Vector4.Transform(customCenterLocal, localTransformCustom);
            Assert.True(MathF.Abs(customCenterTransformed.X - 20f) < 0.01f);
            Assert.True(MathF.Abs(customCenterTransformed.Y - 30f) < 0.01f);
        }
    }
}
