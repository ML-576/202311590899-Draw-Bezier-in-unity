using System;
using System.Collections.Generic;
using UnityEngine;

namespace BezierStudio
{
    /// <summary>可被拾取的编辑点类型</summary>
    public enum PointKind
    {
        Anchor,     // 锚点
        HandleIn,   // 锚点朝向"上一段"的控制点
        HandleOut   // 锚点朝向"下一段"的控制点
    }

    /// <summary>
    /// 路径上的一个节点（锚点 + 进出两个控制点）。
    /// handleIn / handleOut 是相对于 position 的偏移向量；
    /// 为 0 时相邻段退化为直线。
    /// </summary>
    [Serializable]
    public class BezierNode
    {
        public Vector3 position;
        public Vector3 handleIn;
        public Vector3 handleOut;
        public bool smooth;  // 是否为曲线锚点（双击切换）
        public bool broken;  // 两侧控制点是否已被 Ctrl 打断联动

        public BezierNode() { }

        public BezierNode(Vector3 pos)
        {
            position = pos;
        }

        public BezierNode Clone()
        {
            return new BezierNode
            {
                position = position,
                handleIn = handleIn,
                handleOut = handleOut,
                smooth = smooth,
                broken = broken
            };
        }
    }

    /// <summary>
    /// 分段三次 Bezier 路径：相邻两个节点构成一段三次 Bezier 曲线。
    /// </summary>
    [Serializable]
    public class BezierPath
    {
        public List<BezierNode> nodes = new List<BezierNode>();

        public BezierPath Clone()
        {
            var p = new BezierPath();
            foreach (var n in nodes) p.nodes.Add(n.Clone());
            return p;
        }

        public int SegmentCount
        {
            get { return Mathf.Max(0, nodes.Count - 1); }
        }

        /// <summary>取第 i 段的四个三次 Bezier 控制点</summary>
        public void GetSegment(int i, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3)
        {
            BezierNode a = nodes[i];
            BezierNode b = nodes[i + 1];
            p0 = a.position;
            p1 = a.position + a.handleOut;
            p2 = b.position + b.handleIn;
            p3 = b.position;
        }

        /// <summary>三次 Bezier 公式</summary>
        public static Vector3 Cubic(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            float uu = u * u;
            float tt = t * t;
            return uu * u * p0
                 + 3f * uu * t * p1
                 + 3f * u * tt * p2
                 + tt * t * p3;
        }

        public Vector3 SampleSegment(int i, float t)
        {
            Vector3 p0, p1, p2, p3;
            GetSegment(i, out p0, out p1, out p2, out p3);
            return Cubic(p0, p1, p2, p3, t);
        }

        /// <summary>估算第 i 段长度（折线近似）</summary>
        public float ApproxSegmentLength(int i, int probes = 24)
        {
            float len = 0f;
            Vector3 prev = SampleSegment(i, 0f);
            for (int k = 1; k <= probes; k++)
            {
                Vector3 cur = SampleSegment(i, k / (float)probes);
                len += Vector3.Distance(prev, cur);
                prev = cur;
            }
            return len;
        }

        /// <summary>
        /// 沿整条路径密集采样，用于 LineRenderer 预览和管道网格生成。
        /// </summary>
        public List<Vector3> SamplePath(float samplesPerMeter = 60f, int minSteps = 8, int maxSteps = 400)
        {
            var result = new List<Vector3>();
            if (nodes.Count == 0) return result;

            result.Add(nodes[0].position);
            for (int i = 0; i < SegmentCount; i++)
            {
                float len = ApproxSegmentLength(i);
                int steps = Mathf.RoundToInt(Mathf.Clamp(len * samplesPerMeter, minSteps, maxSteps));
                for (int k = 1; k <= steps; k++)
                {
                    result.Add(SampleSegment(i, k / (float)steps));
                }
            }
            return result;
        }
    }
}
