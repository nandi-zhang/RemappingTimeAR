using System;
using System.Collections.Generic;
using UnityEngine;

namespace RemappingTime
{
    /// <summary>
    /// A user-drawn shape y = f(x), defined by points and evaluated with straight lines between them
    /// (flat before the first point and after the last). Formulas call it like a function, e.g.
    /// <c>c1(ton)</c> for "u over time since the trigger".
    ///
    /// <para>Points are edited on the web operator page by dragging them on a graph. <see cref="XMax"/> and the
    /// y range only bound the editor; evaluation uses the points as they are.</para>
    /// </summary>
    public sealed class Curve
    {
        public readonly string Name;
        public string Label;
        public readonly float XMax, YMin, YMax;

        private float[] _xs = { 0f, 1f };
        private float[] _ys = { 0f, 0f };

        public event Action Changed;

        public Curve(string name, string label, float xMax, float yMin, float yMax)
        {
            Name = name; Label = label; XMax = xMax; YMin = yMin; YMax = yMax;
        }

        public float[] Xs => (float[])_xs.Clone();
        public float[] Ys => (float[])_ys.Clone();

        /// <summary>Replaces the points (sorted by x, clamped to the editor range). Needs at least one point.</summary>
        public bool SetPoints(float[] xs, float[] ys)
        {
            if (xs == null || ys == null || xs.Length == 0 || xs.Length != ys.Length) return false;
            var pts = new List<Vector2>(xs.Length);
            for (int i = 0; i < xs.Length; i++)
            {
                if (float.IsNaN(xs[i]) || float.IsNaN(ys[i])) continue;
                pts.Add(new Vector2(Mathf.Clamp(xs[i], 0f, XMax), Mathf.Clamp(ys[i], YMin, YMax)));
            }
            if (pts.Count == 0) return false;
            pts.Sort((a, b) => a.x.CompareTo(b.x));
            _xs = new float[pts.Count];
            _ys = new float[pts.Count];
            for (int i = 0; i < pts.Count; i++) { _xs[i] = pts[i].x; _ys[i] = pts[i].y; }
            Changed?.Invoke();
            return true;
        }

        /// <summary>Piecewise-linear evaluation; constant beyond the first and last point.</summary>
        public float Evaluate(float x)
        {
            float[] xs = _xs, ys = _ys; // local copies: SetPoints swaps the arrays atomically
            int n = xs.Length;
            if (x <= xs[0]) return ys[0];
            if (x >= xs[n - 1]) return ys[n - 1];
            for (int i = 1; i < n; i++)
            {
                if (x <= xs[i])
                {
                    float span = xs[i] - xs[i - 1];
                    float f = span > 1e-6f ? (x - xs[i - 1]) / span : 1f;
                    return ys[i - 1] + (ys[i] - ys[i - 1]) * f;
                }
            }
            return ys[n - 1];
        }
    }
}
