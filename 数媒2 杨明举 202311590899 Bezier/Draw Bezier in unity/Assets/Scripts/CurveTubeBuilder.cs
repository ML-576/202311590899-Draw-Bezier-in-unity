using System.Collections.Generic;
using UnityEngine;

namespace BezierStudio
{
    /// <summary>
    /// 沿采样点扫掠出圆形截面管道，两端用圆形端面封闭（终端闭口）。
    /// 使用 Parallel Transport（平行移动标架）避免管道扭曲。
    /// </summary>
    public static class CurveTubeBuilder
    {
        public static Mesh Build(List<Vector3> samples, float radius, int radialSegments = 14)
        {
            var mesh = new Mesh { name = "BezierTube" };

            if (samples == null || samples.Count < 2 || radius <= 0f)
            {
                return mesh;
            }

            int rings = samples.Count;

            // ---- 切向量 ----
            var tangents = new Vector3[rings];
            for (int i = 0; i < rings; i++)
            {
                if (i == 0)
                    tangents[i] = (samples[1] - samples[0]).normalized;
                else if (i == rings - 1)
                    tangents[i] = (samples[rings - 1] - samples[rings - 2]).normalized;
                else
                    tangents[i] = (samples[i + 1] - samples[i - 1]).normalized;

                if (tangents[i].sqrMagnitude < 1e-8f) tangents[i] = Vector3.forward;
            }

            // ---- 初始标架 ----
            Vector3 refVec = Mathf.Abs(Vector3.Dot(tangents[0], Vector3.up)) > 0.9f
                ? Vector3.right
                : Vector3.up;
            Vector3 normal = (refVec - tangents[0] * Vector3.Dot(refVec, tangents[0])).normalized;
            Vector3 binormal = Vector3.Cross(tangents[0], normal).normalized;

            var normalsFrame = new Vector3[rings];
            var binormalsFrame = new Vector3[rings];
            normalsFrame[0] = normal;
            binormalsFrame[0] = binormal;

            // ---- 平行移动标架 ----
            for (int i = 1; i < rings; i++)
            {
                Quaternion rot = Quaternion.FromToRotation(tangents[i - 1], tangents[i]);
                normal = rot * normal;
                binormal = rot * binormal;
                // 重新正交化，防止误差累积
                normal = (normal - tangents[i] * Vector3.Dot(normal, tangents[i])).normalized;
                binormal = Vector3.Cross(tangents[i], normal).normalized;
                normalsFrame[i] = normal;
                binormalsFrame[i] = binormal;
            }

            // ---- 管壁顶点 ----
            var vertices = new List<Vector3>(rings * radialSegments);
            var uv = new List<Vector2>(rings * radialSegments);
            for (int i = 0; i < rings; i++)
            {
                for (int j = 0; j < radialSegments; j++)
                {
                    float a = Mathf.PI * 2f * j / radialSegments;
                    Vector3 offset = normalsFrame[i] * (Mathf.Cos(a) * radius)
                                   + binormalsFrame[i] * (Mathf.Sin(a) * radius);
                    vertices.Add(samples[i] + offset);
                    uv.Add(new Vector2(i / (float)(rings - 1), j / (float)radialSegments));
                }
            }

            // ---- 管壁三角形（双面发射，配合 Cull Off，保证任意绕序可见）----
            var triangles = new List<int>(rings * radialSegments * 12);
            for (int i = 0; i < rings - 1; i++)
            {
                for (int j = 0; j < radialSegments; j++)
                {
                    int jn = (j + 1) % radialSegments;
                    int a = i * radialSegments + j;
                    int b = (i + 1) * radialSegments + j;
                    int c = (i + 1) * radialSegments + jn;
                    int d = i * radialSegments + jn;
                    triangles.Add(a); triangles.Add(b); triangles.Add(d);
                    triangles.Add(d); triangles.Add(b); triangles.Add(c);
                    // 反向（内表面）
                    triangles.Add(a); triangles.Add(d); triangles.Add(b);
                    triangles.Add(d); triangles.Add(c); triangles.Add(b);
                }
            }

            // ---- 两端圆形封口：端面环顶点单独复制，保证法线平整 ----
            BuildCap(samples[0], tangents[0], normalsFrame[0], binormalsFrame[0], radius, radialSegments, vertices, uv, triangles, false);
            BuildCap(samples[rings - 1], tangents[rings - 1], normalsFrame[rings - 1], binormalsFrame[rings - 1], radius, radialSegments, vertices, uv, triangles, true);

            if (vertices.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void BuildCap(Vector3 center, Vector3 tangent, Vector3 normal, Vector3 binormal,
            float radius, int radialSegments, List<Vector3> vertices, List<Vector2> uv, List<int> triangles, bool endCap)
        {
            int centerIndex = vertices.Count;
            vertices.Add(center);
            uv.Add(new Vector2(0.5f, 0.5f));

            int start = vertices.Count;
            for (int j = 0; j < radialSegments; j++)
            {
                float a = Mathf.PI * 2f * j / radialSegments;
                Vector3 offset = normal * (Mathf.Cos(a) * radius) + binormal * (Mathf.Sin(a) * radius);
                vertices.Add(center + offset);
                uv.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(a), 0.5f + 0.5f * Mathf.Sin(a)));
            }

            for (int j = 0; j < radialSegments; j++)
            {
                int jn = (j + 1) % radialSegments;
                int a = start + j;
                int b = start + jn;
                if (endCap)
                {
                    triangles.Add(centerIndex); triangles.Add(b); triangles.Add(a);
                    triangles.Add(centerIndex); triangles.Add(a); triangles.Add(b);
                }
                else
                {
                    triangles.Add(centerIndex); triangles.Add(a); triangles.Add(b);
                    triangles.Add(centerIndex); triangles.Add(b); triangles.Add(a);
                }
            }
        }
    }
}
