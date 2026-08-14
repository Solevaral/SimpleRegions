using System;
using System.Collections.Generic;

namespace SimpleRegions
{
    /// <summary>
    /// An inclusive tile rectangle: it covers every tile from (X1,Y1) to (X2,Y2) inclusive.
    /// Inclusive on purpose — a claim made from two marked corners must contain both corner
    /// tiles, and every area number the player is shown is a real count of owned tiles.
    /// </summary>
    public struct Rect
    {
        public int X1, Y1, X2, Y2;

        public Rect(int x1, int y1, int x2, int y2)
        {
            X1 = Math.Min(x1, x2);
            Y1 = Math.Min(y1, y2);
            X2 = Math.Max(x1, x2);
            Y2 = Math.Max(y1, y2);
        }

        public int Width => X2 - X1 + 1;
        public int Height => Y2 - Y1 + 1;
        public long Area => (long)Width * Height;

        public bool Contains(int x, int y) => x >= X1 && x <= X2 && y >= Y1 && y <= Y2;

        public bool Intersects(Rect other) =>
            X1 <= other.X2 && X2 >= other.X1 && Y1 <= other.Y2 && Y2 >= other.Y1;

        public override string ToString() => $"({X1},{Y1})-({X2},{Y2})";
    }

    /// <summary>
    /// Area arithmetic for the claim budget.
    ///
    /// The budget charges the UNION of a player's claims, never the sum. Overlapping your own
    /// claims is a supported way to build L-shapes and other non-rectangular footprints, so
    /// land covered by two of your own regions must be paid for once — charging per-region
    /// would penalise exactly the workflow the overlap rule exists to enable.
    /// </summary>
    public static class AreaMath
    {
        /// <summary>
        /// Exact area of the union of the given rectangles, via coordinate compression:
        /// the distinct edges cut the plane into cells that are each either fully covered
        /// or fully empty, so summing the covered ones is exact regardless of how the
        /// rectangles overlap.
        /// </summary>
        public static long UnionArea(IList<Rect> rects)
        {
            if (rects == null || rects.Count == 0) return 0;
            if (rects.Count == 1) return rects[0].Area;

            // Work in half-open [start, end) space so adjacent-but-not-overlapping
            // rectangles (e.g. x2=10 and x1=11) do not merge into one another.
            var xsSet = new SortedSet<int>();
            var ysSet = new SortedSet<int>();
            foreach (var r in rects)
            {
                xsSet.Add(r.X1);
                xsSet.Add(r.X2 + 1);
                ysSet.Add(r.Y1);
                ysSet.Add(r.Y2 + 1);
            }

            var xs = new List<int>(xsSet);
            var ys = new List<int>(ysSet);

            long total = 0;
            for (var i = 0; i + 1 < xs.Count; i++)
            {
                for (var j = 0; j + 1 < ys.Count; j++)
                {
                    var cellX = xs[i];
                    var cellY = ys[j];

                    var covered = false;
                    foreach (var r in rects)
                    {
                        if (r.Contains(cellX, cellY)) { covered = true; break; }
                    }
                    if (!covered) continue;

                    long w = xs[i + 1] - xs[i];
                    long h = ys[j + 1] - ys[j];
                    total += w * h;
                }
            }

            return total;
        }

        /// <summary>
        /// Converts an inclusive <see cref="Rect"/> into the Width/Height a TShock region
        /// must be created with.
        ///
        /// VERIFIED AGAINST TShock 6.1.0: Region.InArea treats the upper bound as INCLUSIVE —
        /// a region stored with Width=10 actually protects 11 tiles (X .. X+Width). So the
        /// stored width is one LESS than the tile count. Getting this backwards would make
        /// every claim silently one tile larger than the player selected and than the area
        /// they were charged for.
        /// </summary>
        public static void ToTShockSize(Rect rect, out int storedWidth, out int storedHeight)
        {
            storedWidth = rect.X2 - rect.X1;
            storedHeight = rect.Y2 - rect.Y1;
        }

        /// <summary>
        /// Inverse of <see cref="ToTShockSize"/>: the inclusive tile rectangle that a TShock
        /// region with the given stored origin/size actually protects.
        /// </summary>
        public static Rect FromTShockRect(int x, int y, int storedWidth, int storedHeight)
        {
            return new Rect(x, y, x + storedWidth, y + storedHeight);
        }

        /// <summary>
        /// How much NEW land <paramref name="candidate"/> would consume on top of what the
        /// player already owns — i.e. union(existing + candidate) - union(existing).
        /// Land already inside one of their own claims is free, which is what makes
        /// layering regions to form complex shapes affordable.
        /// </summary>
        public static long AdditionalCost(IList<Rect> existing, Rect candidate)
        {
            var before = UnionArea(existing);

            var combined = new List<Rect>(existing.Count + 1);
            combined.AddRange(existing);
            combined.Add(candidate);

            return UnionArea(combined) - before;
        }
    }
}
