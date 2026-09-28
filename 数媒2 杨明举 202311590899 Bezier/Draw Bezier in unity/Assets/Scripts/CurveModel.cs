using System.Collections.Generic;
using UnityEngine;

namespace BezierStudio
{
    /// <summary>挂在锚点 / 控制点小球上，供鼠标拾取</summary>
    public class GizmoPoint : MonoBehaviour
    {
        public int nodeIndex;
        public PointKind kind;
    }

    /// <summary>
    /// 一条 Bezier 曲线模型：负责路径数据、圆管网格渲染、
    /// 设计状态下的锚点/控制点可视化与拖拽响应。
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public class CurveModel : MonoBehaviour
    {
        public BezierPath path = new BezierPath();
        public float radiusCm = 1f;
        public Color color = Color.white;

        public bool IsEditing { get { return editing; } }

        private bool editing;
        private BezierPath backup;

        private MeshRenderer tubeRenderer;
        private MeshCollider tubeCollider;
        private Material tubeMaterial;

        private LineRenderer mainLine;
        private Material lineMaterial;

        private readonly List<GameObject> gizmoObjects = new List<GameObject>();
        private readonly Dictionary<int, Transform> pointTransforms = new Dictionary<int, Transform>();
        private readonly List<HandleLine> handleLines = new List<HandleLine>();

        private float gizmoScale = 0.3f;

        private static Material anchorMaterial;
        private static Material handleMaterial;
        private static Material handleLineMaterial;

        private struct HandleLine
        {
            public LineRenderer line;
            public int nodeIndex;
            public PointKind kind;
        }

        // ------------------------------------------------------------------
        // 材质工具：优先使用 Resources 内自带的 unlit shader，
        // 再回退到工程内置 shader，兼容 Built-in / URP 管线。
        // ------------------------------------------------------------------
        public static Shader FindUnlitShader()
        {
            Shader sh = Resources.Load<Shader>("BezierUnlit");
            if (sh != null) return sh;

            string[] candidates =
            {
                "Unlit/Color",
                "Universal Render Pipeline/Unlit",
                "Sprites/Default",
                "Standard",
                "Diffuse"
            };
            foreach (string name in candidates)
            {
                sh = Shader.Find(name);
                if (sh != null) return sh;
            }
            return null;
        }

        public static Material NewColoredMaterial(Color c)
        {
            Shader sh = FindUnlitShader();
            Material m = sh != null ? new Material(sh) : new Material(Shader.Find("Diffuse"));
            ApplyColor(m, c);
            return m;
        }

        public static void ApplyColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            m.color = c;
        }

        private static Material GetSharedAnchorMaterial()
        {
            if (anchorMaterial == null) anchorMaterial = NewColoredMaterial(new Color(1f, 0.84f, 0.2f));
            return anchorMaterial;
        }

        private static Material GetSharedHandleMaterial()
        {
            if (handleMaterial == null) handleMaterial = NewColoredMaterial(Color.white);
            return handleMaterial;
        }

        private static Material GetSharedHandleLineMaterial()
        {
            if (handleLineMaterial == null) handleLineMaterial = NewColoredMaterial(new Color(0.85f, 0.85f, 0.85f));
            return handleLineMaterial;
        }

        // ------------------------------------------------------------------
        // 初始化
        // ------------------------------------------------------------------
        public void Init()
        {
            var mf = GetComponent<MeshFilter>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();

            tubeRenderer = GetComponent<MeshRenderer>();
            if (tubeRenderer == null) tubeRenderer = gameObject.AddComponent<MeshRenderer>();

            tubeCollider = GetComponent<MeshCollider>();
            if (tubeCollider == null) tubeCollider = gameObject.AddComponent<MeshCollider>();

            tubeMaterial = NewColoredMaterial(color);
            tubeRenderer.sharedMaterial = tubeMaterial;
            tubeRenderer.enabled = false;
            tubeCollider.enabled = false;

            // 路径预览线
            var lineGo = new GameObject("PathLine");
            lineGo.transform.SetParent(transform, false);
            mainLine = lineGo.AddComponent<LineRenderer>();
            lineMaterial = NewColoredMaterial(color);
            mainLine.sharedMaterial = lineMaterial;
            mainLine.useWorldSpace = false;
            mainLine.numCapVertices = 2;
            mainLine.numCornerVertices = 2;
            mainLine.enabled = false;
        }

        public void SetColor(Color c)
        {
            color = c;
            ApplyColor(tubeMaterial, c);
            ApplyColor(lineMaterial, c);
        }

        // ------------------------------------------------------------------
        // 网格生成 / 二次编辑切换
        // ------------------------------------------------------------------
        public void RebuildTubeMesh()
        {
            var samples = path.SamplePath();
            Mesh mesh = CurveTubeBuilder.Build(samples, radiusCm * 0.01f); // 1cm = 0.01m
            var mf = GetComponent<MeshFilter>();
            if (mf.sharedMesh != null) DestroyMesh(mf.sharedMesh);
            mf.sharedMesh = mesh;
            tubeCollider.sharedMesh = mesh;
        }

        private void DestroyMesh(Mesh mesh)
        {
            if (Application.isPlaying) Object.Destroy(mesh);
            else Object.DestroyImmediate(mesh);
        }

        /// <summary>确定：生成圆管模型</summary>
        public void Commit(float radiusCmValue)
        {
            radiusCm = Mathf.Max(0.01f, radiusCmValue);
            editing = false;
            RebuildTubeMesh();
            tubeRenderer.enabled = true;
            tubeCollider.enabled = true;
            mainLine.enabled = false;
            DestroyGizmos();
        }

        /// <summary>双击模型：进入二次编辑</summary>
        public void BeginEdit(float gizmoSize)
        {
            backup = path.Clone();
            editing = true;
            gizmoScale = gizmoSize;
            tubeRenderer.enabled = false;
            tubeCollider.enabled = false;
            mainLine.enabled = true;
            RebuildGizmos();
            RefreshEditVisuals();
        }

        /// <summary>Esc：撤销二次编辑，恢复模型渲染</summary>
        public void RevertEdit()
        {
            if (backup != null) path = backup;
            backup = null;
            editing = false;
            tubeRenderer.enabled = true;
            tubeCollider.enabled = true;
            mainLine.enabled = false;
            DestroyGizmos();
        }

        /// <summary>新草稿直接进入绘制状态</summary>
        public void BeginDraft(float gizmoSize)
        {
            editing = true;
            gizmoScale = gizmoSize;
            mainLine.enabled = true;
            RebuildGizmos();
            RefreshEditVisuals();
        }

        public bool IsDraft
        {
            get { return editing && backup == null; }
        }

        // ------------------------------------------------------------------
        // 节点结构操作
        // ------------------------------------------------------------------
        public void AddNode(Vector3 localPos)
        {
            path.nodes.Add(new BezierNode(localPos));
            if (editing) RebuildGizmos();
            RefreshEditVisuals();
        }

        /// <summary>在第 afterIndex 个节点之后插入</summary>
        public void InsertNode(int afterIndex, Vector3 localPos)
        {
            path.nodes.Insert(Mathf.Clamp(afterIndex + 1, 0, path.nodes.Count), new BezierNode(localPos));
            if (editing) RebuildGizmos();
            RefreshEditVisuals();
        }

        public bool RemoveNodeAt(int index)
        {
            if (path.nodes.Count <= 2) return false; // 至少保留两个点
            path.nodes.RemoveAt(index);
            RebuildGizmos();
            RefreshEditVisuals();
            return true;
        }

        /// <summary>双击节点：直线锚点 &lt;-&gt; 平滑曲线锚点</summary>
        public void ToggleSmooth(int index)
        {
            BezierNode n = path.nodes[index];
            if (n.smooth)
            {
                n.smooth = false;
                n.broken = false;
                n.handleIn = Vector3.zero;
                n.handleOut = Vector3.zero;
            }
            else
            {
                Vector3 dir;
                bool hasPrev = index > 0;
                bool hasNext = index < path.nodes.Count - 1;
                if (hasPrev && hasNext)
                    dir = (path.nodes[index + 1].position - path.nodes[index - 1].position).normalized;
                else if (hasNext)
                    dir = (path.nodes[index + 1].position - n.position).normalized;
                else if (hasPrev)
                    dir = (n.position - path.nodes[index - 1].position).normalized;
                else
                    dir = Vector3.right;

                float len = 0.3f;
                if (hasPrev) len = Mathf.Max(len, Vector3.Distance(n.position, path.nodes[index - 1].position) * 0.33f);
                if (hasNext) len = Mathf.Max(len, Vector3.Distance(n.position, path.nodes[index + 1].position) * 0.33f);

                n.smooth = true;
                n.broken = false;
                n.handleOut = dir * len;
                n.handleIn = -dir * len;
            }
            RebuildGizmos();
            RefreshEditVisuals();
        }

        // ------------------------------------------------------------------
        // 拖拽
        // ------------------------------------------------------------------
        public void MovePoint(GizmoPoint p, Vector3 localPos, bool independent)
        {
            if (p == null) return;
            BezierNode n = path.nodes[p.nodeIndex];
            if (n == null) return;

            if (p.kind == PointKind.Anchor)
            {
                n.position = localPos;
            }
            else if (p.kind == PointKind.HandleOut)
            {
                n.handleOut = localPos - n.position;
                if (independent) n.broken = true;
                else
                {
                    n.broken = false;
                    n.handleIn = -n.handleOut; // 镜像联动
                }
            }
            else // HandleIn
            {
                n.handleIn = localPos - n.position;
                if (independent) n.broken = true;
                else
                {
                    n.broken = false;
                    n.handleOut = -n.handleIn; // 镜像联动
                }
            }
            RefreshEditVisuals();
        }

        // ------------------------------------------------------------------
        // 编辑可视化
        // ------------------------------------------------------------------
        public void SetGizmoScale(float s)
        {
            gizmoScale = s;
            foreach (var go in gizmoObjects)
            {
                if (go == null) continue;
                var gp = go.GetComponent<GizmoPoint>();
                float scale = gp != null && gp.kind == PointKind.Anchor ? s : s * 0.6f;
                go.transform.localScale = Vector3.one * scale;
            }
            if (mainLine != null) mainLine.widthMultiplier = s * 0.12f;
            foreach (var hl in handleLines)
            {
                if (hl.line != null) hl.line.widthMultiplier = s * 0.07f;
            }
        }

        private GameObject CreateSphere(string name, Material sharedMat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.SetParent(transform, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = sharedMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        private void RebuildGizmos()
        {
            DestroyGizmos();
            pointTransforms.Clear();

            for (int i = 0; i < path.nodes.Count; i++)
            {
                // 锚点
                var anchorGo = CreateSphere("Anchor_" + i, GetSharedAnchorMaterial());
                var gp = anchorGo.AddComponent<GizmoPoint>();
                gp.nodeIndex = i;
                gp.kind = PointKind.Anchor;
                gizmoObjects.Add(anchorGo);
                pointTransforms[PointKey(i, PointKind.Anchor)] = anchorGo.transform;

                BezierNode n = path.nodes[i];
                if (n.smooth)
                {
                    // 入控制点（除首节点外）
                    if (i > 0)
                    {
                        CreateHandle(i, PointKind.HandleIn, n.position + n.handleIn);
                    }
                    // 出控制点（除末节点外）
                    if (i < path.nodes.Count - 1)
                    {
                        CreateHandle(i, PointKind.HandleOut, n.position + n.handleOut);
                    }
                }
            }
            SetGizmoScale(gizmoScale);
        }

        private void CreateHandle(int i, PointKind kind, Vector3 localPos)
        {
            var go = CreateSphere(kind + "_" + i, GetSharedHandleMaterial());
            var gp = go.AddComponent<GizmoPoint>();
            gp.nodeIndex = i;
            gp.kind = kind;
            go.transform.localPosition = localPos;
            gizmoObjects.Add(go);
            pointTransforms[PointKey(i, kind)] = go.transform;

            var lineGo = new GameObject("HandleLine_" + kind + "_" + i);
            lineGo.transform.SetParent(transform, false);
            var lr = lineGo.AddComponent<LineRenderer>();
            lr.sharedMaterial = GetSharedHandleLineMaterial();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.numCapVertices = 2;
            handleLines.Add(new HandleLine { line = lr, nodeIndex = i, kind = kind });
        }

        private static int PointKey(int node, PointKind kind)
        {
            return node * 4 + (int)kind;
        }

        /// <summary>拖拽过程中只更新位置，不重建对象</summary>
        public void RefreshEditVisuals()
        {
            if (!editing || mainLine == null) return;

            // 全部使用局部坐标：即使整条模型在旋转界面被转过，编辑手柄/预览线仍与网格重合
            for (int i = 0; i < path.nodes.Count; i++)
            {
                BezierNode n = path.nodes[i];
                Transform t;
                if (pointTransforms.TryGetValue(PointKey(i, PointKind.Anchor), out t))
                    t.localPosition = n.position;
                if (pointTransforms.TryGetValue(PointKey(i, PointKind.HandleIn), out t))
                    t.localPosition = n.position + n.handleIn;
                if (pointTransforms.TryGetValue(PointKey(i, PointKind.HandleOut), out t))
                    t.localPosition = n.position + n.handleOut;
            }

            foreach (var hl in handleLines)
            {
                if (hl.line == null) continue;
                BezierNode n = path.nodes[hl.nodeIndex];
                hl.line.SetPosition(0, n.position);
                hl.line.SetPosition(1, n.position + (hl.kind == PointKind.HandleIn ? n.handleIn : n.handleOut));
            }

            var samples = path.SamplePath(30f, 4, 200);
            mainLine.positionCount = samples.Count;
            mainLine.SetPositions(samples.ToArray());
        }

        public GizmoPoint PickGizmo(Ray ray)
        {
            var hits = Physics.RaycastAll(ray, 1000f);
            float best = float.MaxValue;
            GizmoPoint bestPoint = null;
            foreach (var h in hits)
            {
                var gp = h.collider.GetComponent<GizmoPoint>();
                if (gp != null && gp.transform.IsChildOf(transform) && h.distance < best)
                {
                    best = h.distance;
                    bestPoint = gp;
                }
            }
            return bestPoint;
        }

        private void DestroyGizmos()
        {
            foreach (var go in gizmoObjects)
            {
                if (go != null)
                {
                    if (Application.isPlaying) Object.Destroy(go);
                    else Object.DestroyImmediate(go);
                }
            }
            gizmoObjects.Clear();

            foreach (var hl in handleLines)
            {
                if (hl.line != null)
                {
                    if (Application.isPlaying) Object.Destroy(hl.line.gameObject);
                    else Object.DestroyImmediate(hl.line.gameObject);
                }
            }
            handleLines.Clear();
        }

        void OnDestroy()
        {
            var mf = GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) DestroyMesh(mf.sharedMesh);
            if (tubeMaterial != null) Object.Destroy(tubeMaterial);
            if (lineMaterial != null) Object.Destroy(lineMaterial);
        }
    }
}
