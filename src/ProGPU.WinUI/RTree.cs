using System;
using System.Collections.Generic;
using System.Numerics;
using ProGPU.Scene;

namespace ProGPU.WinUI
{
    public struct RTreeEntry<T>
    {
        public Rect Bounds;
        public T Item;

        public RTreeEntry(Rect bounds, T item)
        {
            Bounds = bounds;
            Item = item;
        }
    }

    public class RTreeNode<T>
    {
        public Rect Bounds { get; set; }
        public List<RTreeNode<T>>? Children { get; set; }
        public List<RTreeEntry<T>>? Entries { get; set; }
        public bool IsLeaf => Entries != null;

        public void Query(Vector2 point, List<T> results)
        {
            if (!Bounds.Contains(point)) return;

            if (IsLeaf)
            {
                foreach (var entry in Entries!)
                {
                    if (entry.Bounds.Contains(point))
                    {
                        results.Add(entry.Item);
                    }
                }
            }
            else
            {
                foreach (var child in Children!)
                {
                    child.Query(point, results);
                }
            }
        }

        public void Query(Rect queryRect, List<T> results)
        {
            if (!RTree<T>.Intersects(Bounds, queryRect)) return;

            if (IsLeaf)
            {
                foreach (var entry in Entries!)
                {
                    if (RTree<T>.Intersects(entry.Bounds, queryRect))
                    {
                        results.Add(entry.Item);
                    }
                }
            }
            else
            {
                foreach (var child in Children!)
                {
                    child.Query(queryRect, results);
                }
            }
        }
    }

    public class RTree<T>
    {
        private readonly int _maxDegree;
        private RTreeNode<T>? _root;

        public RTreeNode<T>? Root => _root;

        public RTree(int maxDegree = 8)
        {
            _maxDegree = maxDegree;
        }

        public void Rebuild(List<RTreeEntry<T>> entries)
        {
            if (entries.Count == 0)
            {
                _root = null;
                return;
            }
            _root = BuildTree(entries, _maxDegree);
        }

        public List<T> Query(Vector2 point)
        {
            var results = new List<T>();
            _root?.Query(point, results);
            return results;
        }

        public List<T> Query(Rect queryRect)
        {
            var results = new List<T>();
            _root?.Query(queryRect, results);
            return results;
        }

        public static RTreeNode<T>? BuildTree(List<RTreeEntry<T>> entries, int maxDegree = 8)
        {
            if (entries.Count == 0) return null;

            var currentLevel = PackNodes(entries, maxDegree);

            while (currentLevel.Count > 1)
            {
                currentLevel = PackParentNodes(currentLevel, maxDegree);
            }

            return currentLevel[0];
        }

        private static List<RTreeNode<T>> PackNodes(List<RTreeEntry<T>> entries, int maxDegree)
        {
            var leaves = new List<RTreeNode<T>>();
            int n = entries.Count;
            if (n == 0) return leaves;

            // Sort by center X
            entries.Sort((a, b) => {
                float ax = a.Bounds.X + a.Bounds.Width / 2f;
                float bx = b.Bounds.X + b.Bounds.Width / 2f;
                return ax.CompareTo(bx);
            });

            int leafCount = (int)Math.Ceiling((double)n / maxDegree);
            int sliceCount = (int)Math.Ceiling(Math.Sqrt(leafCount));
            if (sliceCount <= 0) sliceCount = 1;
            int entriesPerSlice = (int)Math.Ceiling((double)n / sliceCount);

            for (int i = 0; i < sliceCount; i++)
            {
                int sliceStart = i * entriesPerSlice;
                int sliceEnd = Math.Min(sliceStart + entriesPerSlice, n);
                if (sliceStart >= n) break;

                var slice = new List<RTreeEntry<T>>();
                for (int j = sliceStart; j < sliceEnd; j++)
                {
                    slice.Add(entries[j]);
                }

                // Sort slice by center Y
                slice.Sort((a, b) => {
                    float ay = a.Bounds.Y + a.Bounds.Height / 2f;
                    float by = b.Bounds.Y + b.Bounds.Height / 2f;
                    return ay.CompareTo(by);
                });

                int sliceSize = slice.Count;
                for (int k = 0; k < sliceSize; k += maxDegree)
                {
                    int groupSize = Math.Min(maxDegree, sliceSize - k);
                    var group = new List<RTreeEntry<T>>();
                    for (int m = 0; m < groupSize; m++)
                    {
                        group.Add(slice[k + m]);
                    }

                    Rect bounds = group[0].Bounds;
                    for (int m = 1; m < group.Count; m++)
                    {
                        bounds = Union(bounds, group[m].Bounds);
                    }

                    leaves.Add(new RTreeNode<T>
                    {
                        Bounds = bounds,
                        Entries = group
                    });
                }
            }

            return leaves;
        }

        private static List<RTreeNode<T>> PackParentNodes(List<RTreeNode<T>> childNodes, int maxDegree)
        {
            var parents = new List<RTreeNode<T>>();
            int n = childNodes.Count;
            if (n == 0) return parents;

            // Sort by center X
            childNodes.Sort((a, b) => {
                float ax = a.Bounds.X + a.Bounds.Width / 2f;
                float bx = b.Bounds.X + b.Bounds.Width / 2f;
                return ax.CompareTo(bx);
            });

            int parentCount = (int)Math.Ceiling((double)n / maxDegree);
            int sliceCount = (int)Math.Ceiling(Math.Sqrt(parentCount));
            if (sliceCount <= 0) sliceCount = 1;
            int nodesPerSlice = (int)Math.Ceiling((double)n / sliceCount);

            for (int i = 0; i < sliceCount; i++)
            {
                int sliceStart = i * nodesPerSlice;
                int sliceEnd = Math.Min(sliceStart + nodesPerSlice, n);
                if (sliceStart >= n) break;

                var slice = new List<RTreeNode<T>>();
                for (int j = sliceStart; j < sliceEnd; j++)
                {
                    slice.Add(childNodes[j]);
                }

                // Sort slice by center Y
                slice.Sort((a, b) => {
                    float ay = a.Bounds.Y + a.Bounds.Height / 2f;
                    float by = b.Bounds.Y + b.Bounds.Height / 2f;
                    return ay.CompareTo(by);
                });

                int sliceSize = slice.Count;
                for (int k = 0; k < sliceSize; k += maxDegree)
                {
                    int groupSize = Math.Min(maxDegree, sliceSize - k);
                    var group = new List<RTreeNode<T>>();
                    for (int m = 0; m < groupSize; m++)
                    {
                        group.Add(slice[k + m]);
                    }

                    Rect bounds = group[0].Bounds;
                    for (int m = 1; m < group.Count; m++)
                    {
                        bounds = Union(bounds, group[m].Bounds);
                    }

                    parents.Add(new RTreeNode<T>
                    {
                        Bounds = bounds,
                        Children = group
                    });
                }
            }

            return parents;
        }

        public static Rect Union(Rect r1, Rect r2)
        {
            float minX = MathF.Min(r1.X, r2.X);
            float minY = MathF.Min(r1.Y, r2.Y);
            float maxX = MathF.Max(r1.X + r1.Width, r2.X + r2.Width);
            float maxY = MathF.Max(r1.Y + r1.Height, r2.Y + r2.Height);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        public static bool Intersects(Rect r1, Rect r2)
        {
            return !(r1.X + r1.Width < r2.X ||
                     r2.X + r2.Width < r1.X ||
                     r1.Y + r1.Height < r2.Y ||
                     r2.Y + r2.Height < r1.Y);
        }
    }
}
