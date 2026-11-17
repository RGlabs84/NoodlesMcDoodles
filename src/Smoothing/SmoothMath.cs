using System;

namespace NoodlesMcDoodles.Smoothing
{
    /// <summary>
    /// Pure interpolation math for C1 (docs/design/06-client-smoothing-and-compression.md) — deliberately free
    /// of <c>UnityEngine</c>, <c>ZDO</c>, and Harmony types so it can be exercised standalone, outside the game
    /// process, by <c>tools/smooth-sim</c> (loads <c>bin/NoodlesMcDoodles.dll</c> by reflection and calls these
    /// exact methods, the same way <c>tools/t9-sim</c> calls <c>Transport.RateRule.Decide</c>). Every method here
    /// takes and returns plain floats only, so the offline tool needs no UnityEngine reference at all.
    ///
    /// Every method is allocation-free (value types only) — this runs on the hot path, once per visible
    /// non-owned object per frame (task brief: "keep per-frame allocations at zero on the hot path").
    /// </summary>
    internal static class SmoothMath
    {
        public static float Distance(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = bx - ax, dy = by - ay, dz = bz - az;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>
        /// Shortest-path quaternion slerp (inputs assumed already normalized, as every <c>ZDO.GetRotation()</c>
        /// value is). Falls back to a normalized linear blend when the two rotations are nearly identical, the
        /// standard fix for the divide-by-(near-zero) that a naive slerp hits there.
        /// </summary>
        public static void SlerpQuat(
            float ax, float ay, float az, float aw,
            float bx, float by, float bz, float bw,
            float t,
            out float rx, out float ry, out float rz, out float rw)
        {
            float dot = ax * bx + ay * by + az * bz + aw * bw;
            if (dot < 0f) { bx = -bx; by = -by; bz = -bz; bw = -bw; dot = -dot; } // take the shorter arc

            float s0, s1;
            const float NearParallel = 1f - 1e-5f;
            if (dot > NearParallel)
            {
                s0 = 1f - t;
                s1 = t;
            }
            else
            {
                double theta0 = Math.Acos(dot);
                double sinTheta0 = Math.Sin(theta0);
                double theta = theta0 * t;
                s0 = (float)(Math.Sin(theta0 - theta) / sinTheta0);
                s1 = (float)(Math.Sin(theta) / sinTheta0);
            }

            rx = s0 * ax + s1 * bx;
            ry = s0 * ay + s1 * by;
            rz = s0 * az + s1 * bz;
            rw = s0 * aw + s1 * bw;

            float len = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
            if (len > 1e-9f)
            {
                rx /= len; ry /= len; rz /= len; rw /= len;
            }
            else
            {
                rx = 0f; ry = 0f; rz = 0f; rw = 1f; // degenerate input — identity is the only safe fallback
            }
        }
    }
}
