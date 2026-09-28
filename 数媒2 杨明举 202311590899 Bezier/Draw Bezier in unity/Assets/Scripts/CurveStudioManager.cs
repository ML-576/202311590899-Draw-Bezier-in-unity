using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace BezierStudio
{
    public enum WorkMode { Observe, Design, Rotate }

    public enum PlaneViewType { Front, Side, Top }

    /// <summary>
    /// Bezier 参数曲线建模与交互 总控：
    /// F1 观察界面 / F2 设计界面 / F3 旋转界面。
    /// </summary>
    public class CurveStudioManager : MonoBehaviour
    {
        // ---------------- 视图平面描述 ----------------
        private struct ViewPlane
        {
            public string label;
            public KeyCode key;
            public Vector3 cameraDir;     // 相对原点的相机方向（单位向量）
            public Quaternion cameraRot;
            public Vector3 planeNormal;

            public ViewPlane(string label, KeyCode key, Vector3 cameraDir, Quaternion cameraRot, Vector3 planeNormal)
            {
                this.label = label;
                this.key = key;
                this.cameraDir = cameraDir;
                this.cameraRot = cameraRot;
                this.planeNormal = planeNormal;
            }
        }

        private ViewPlane[] planes;

        // ---------------- 状态 ----------------
        private WorkMode mode = WorkMode.Design;
        private PlaneViewType planeView = PlaneViewType.Front;
        private float camDistance = 10f;

        private Camera cam;
        private FreeLookCamera freeLook;

        // 绘制 / 编辑
        private CurveModel active;
        private bool isDraft;
        private bool addPointMode;
        private bool deletePointMode;

        private GizmoPoint dragPoint;
        private Vector3 pressMousePos;
        private bool dragging;
        private bool pressConsumed;
        private int pendingAnchorIndex = -1;
        private float pendingAnchorTime;
        private CurveModel pendingModel;
        private float pendingModelTime;
        private const float DoubleClickTime = 0.35f;
        private const float DragThresholdPx = 8f;

        // 旋转
        private CurveModel rotateTarget;
        private Vector3 lastRotateMouse;
        private Quaternion rotateBeforeQuaternion; // 本次拖拽开始时的旋转，用于判定是否真的发生了旋转

        // 旋转历史（撤销/重做），最多保留 20 次
        private struct RotateSnapshot
        {
            public CurveModel model;
            public Quaternion rotation;
            public RotateSnapshot(CurveModel m, Quaternion r) { model = m; rotation = r; }
        }
        private readonly List<RotateSnapshot> rotateUndoStack = new List<RotateSnapshot>();
        private readonly List<RotateSnapshot> rotateRedoStack = new List<RotateSnapshot>();
        private const int MaxRotateHistory = 20;

        // UI / 参数
        private Color currentColor = Color.white;
        private string radiusText = "1";
        private bool pickerOpen;
        private bool quitDialog;
        private bool quitConfirmed;

        private readonly List<Rect> guiScreenRects = new List<Rect>();

        private GUIStyle boldLabel;
        private GUIStyle hintLabel;

        // ================================================================
        void Awake()
        {
            planes = new ViewPlane[]
            {
                new ViewPlane("正视", KeyCode.Alpha1, new Vector3(0f, 0f, -1f), Quaternion.Euler(0f, 0f, 0f),   new Vector3(0f, 0f, 1f)),
                new ViewPlane("侧视", KeyCode.Alpha2, new Vector3(1f, 0f, 0f),  Quaternion.Euler(0f, -90f, 0f), new Vector3(1f, 0f, 0f)),
                new ViewPlane("俯视", KeyCode.Alpha3, new Vector3(0f, 1f, 0f),  Quaternion.Euler(90f, 0f, 0f),  new Vector3(0f, 1f, 0f)),
            };

            // 退出确认保险（独立构建时生效；编辑器中由 Editor 脚本兜底）
            Application.wantsToQuit += OnWantsToQuit;
        }

        void Start()
        {
            SetupCamera();
            CreateWorldAxes();
            currentColor = RandomCurveColor();
            EnterMode(WorkMode.Design, true);
        }

        private bool OnWantsToQuit()
        {
            if (quitConfirmed) return true;
            quitDialog = true;
            return false;
        }

        // ---------------- 场景基础 ----------------
        private void SetupCamera()
        {
            cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.13f, 0.13f, 0.15f);
                cam.fieldOfView = 60f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 2000f;
            }
            freeLook = cam.GetComponent<FreeLookCamera>();
            if (freeLook == null) freeLook = cam.gameObject.AddComponent<FreeLookCamera>();
            freeLook.enabled = false;
        }

        private void CreateWorldAxes()
        {
            MakeAxis("Axis_X", Vector3.right, new Color(1f, 0.35f, 0.3f));
            MakeAxis("Axis_Y", Vector3.up, new Color(0.35f, 1f, 0.35f));
            MakeAxis("Axis_Z", Vector3.forward, new Color(0.4f, 0.6f, 1f));
        }

        private void MakeAxis(string name, Vector3 axis, Color c)
        {
            var go = new GameObject(name);
            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = CurveModel.NewColoredMaterial(c);
            lr.useWorldSpace = true;
            lr.widthMultiplier = 0.025f;
            lr.positionCount = 2;
            lr.SetPosition(0, -axis * 4f);
            lr.SetPosition(1, axis * 4f);
        }

        private ViewPlane Plane { get { return planes[(int)planeView]; } }

        private float GizmoSize { get { return camDistance * 0.03f; } }

        // ================================================================
        // 模式 / 平面切换
        // ================================================================
        private void EnterMode(WorkMode m, bool resetView)
        {
            mode = m;
            rotateTarget = null;
            if (m != WorkMode.Observe)
            {
                freeLook.enabled = false;
                if (resetView)
                {
                    planeView = PlaneViewType.Front;
                    camDistance = 10f; // 每次进入设计/旋转界面默认正视、距原点10米
                }
                ApplyCamera();
            }
            else
            {
                freeLook.enabled = true; // 观察界面：相机完全自由
            }
        }

        private void SetPlane(PlaneViewType p)
        {
            planeView = p;
            camDistance = 10f; // 切换平面时相机归位到距原点10米
            ApplyCamera();
        }

        private void ApplyCamera()
        {
            var vp = Plane;
            cam.transform.SetPositionAndRotation(vp.cameraDir * camDistance, vp.cameraRot);
        }

        // ================================================================
        // 每帧输入
        // ================================================================
        void Update()
        {
            // 退出确认菜单弹出时，屏蔽一切其它按键（Esc 无法跳过）
            if (quitDialog) return;

            // 在半径输入框打字时不响应快捷键
            bool typing = GUI.GetNameOfFocusedControl() == "RadiusField";

            if (!typing) HandleGlobalKeys();

            if (mode == WorkMode.Design)
            {
                if (!typing) HandleDesignKeys();
                HandleDesignMouse();
                if (active != null)
                {
                    active.SetGizmoScale(GizmoSize);
                    if (active.color != currentColor) active.SetColor(currentColor); // 改色实时生效
                }
                ApplyCamera();
            }
            else if (mode == WorkMode.Rotate)
            {
                if (!typing) HandleRotateKeys();
                HandleRotateMouse();
                ApplyCamera();
            }
            else if (mode == WorkMode.Observe)
            {
                if (!typing) HandleObserveKeys();
                // 观察界面不接管相机位置，FreeLookCamera 全权控制自由飞行
            }

            // 设计 / 旋转界面下滚轮前后缩放相机距离（不缩放模型）
            if (mode != WorkMode.Observe && !MouseOverGUI())
            {
                float w = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(w) > 0f)
                    camDistance = Mathf.Clamp(camDistance * (1f - w), 1.5f, 40f);
            }
        }

        private void HandleGlobalKeys()
        {
            if (Input.GetKeyDown(KeyCode.F1)) EnterMode(WorkMode.Observe, false);
            if (Input.GetKeyDown(KeyCode.F2)) EnterMode(WorkMode.Design, true);
            if (Input.GetKeyDown(KeyCode.F3)) EnterMode(WorkMode.Rotate, true);
        }

        private void HandleDesignKeys()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetPlane(PlaneViewType.Front);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SetPlane(PlaneViewType.Side);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SetPlane(PlaneViewType.Top);

            if (active != null)
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    ConfirmEdit();
                if (Input.GetKeyDown(KeyCode.Escape))
                    CancelEdit();
            }
            else
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                    quitDialog = true; // 空闲时 Esc：退出演示确认保险
            }
        }

        private void HandleRotateKeys()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetPlane(PlaneViewType.Front);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SetPlane(PlaneViewType.Side);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SetPlane(PlaneViewType.Top);
            if (Input.GetKeyDown(KeyCode.Escape)) quitDialog = true;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (ctrl && Input.GetKeyDown(KeyCode.Z))
            {
                if (shift) RedoRotate();   // Ctrl + Shift + Z 重做
                else UndoRotate();          // Ctrl + Z 撤销
            }
            else if (ctrl && Input.GetKeyDown(KeyCode.Y))
            {
                RedoRotate();               // Ctrl + Y 重做（兼容习惯）
            }
        }

        private void HandleObserveKeys()
        {
            if (Input.GetKeyDown(KeyCode.Alpha1)) SetPlaneObserve(PlaneViewType.Front);
            if (Input.GetKeyDown(KeyCode.Alpha2)) SetPlaneObserve(PlaneViewType.Side);
            if (Input.GetKeyDown(KeyCode.Alpha3)) SetPlaneObserve(PlaneViewType.Top);
            if (Input.GetKeyDown(KeyCode.Escape)) quitDialog = true;
        }

        /// <summary>
        /// 观察界面切换平面：相机瞬移到距原点 10 米的对应视角，
        /// 但不锁定 —— 之后仍可自由飞行/旋转。
        /// </summary>
        private void SetPlaneObserve(PlaneViewType p)
        {
            planeView = p;
            camDistance = 10f;
            var vp = planes[(int)p];
            freeLook.SnapTo(vp.cameraDir * camDistance, vp.cameraRot);
        }

        // ================================================================
        // 设计界面鼠标交互
        // ================================================================
        private void HandleDesignMouse()
        {
            if (MouseOverGUI()) return;

            Ray ray = cam.ScreenPointToRay(Input.mousePosition);

            // ---------------- 按下 ----------------
            if (Input.GetMouseButtonDown(0))
            {
                pressMousePos = Input.mousePosition;
                dragging = false;
                pressConsumed = false;
                dragPoint = null;

                if (active != null)
                {
                    GizmoPoint gp = active.PickGizmo(ray);
                    if (gp != null)
                    {
                        // 删除点模式：点中锚点即删除，点中其它点不响应拖拽
                        if (deletePointMode)
                        {
                            if (gp.kind == PointKind.Anchor)
                                active.RemoveNodeAt(gp.nodeIndex);
                            pressConsumed = true;
                            return;
                        }
                        dragPoint = gp;
                    }
                }
                else
                {
                    // 空闲：为双击模型做准备
                    var m = PickModel(ray);
                    if (m == null) { pendingModel = null; }
                }
            }

            // ---------------- 拖拽 ----------------
            if (Input.GetMouseButton(0) && dragPoint != null && !pressConsumed)
            {
                if (!dragging && Vector3.Distance(Input.mousePosition, pressMousePos) > DragThresholdPx)
                    dragging = true;

                if (dragging)
                {
                    Vector3 worldPos;
                    if (ProjectMouseToPlane(ray, out worldPos))
                    {
                        Vector3 localPos = active.transform.InverseTransformPoint(worldPos);
                        bool independent = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                        active.MovePoint(dragPoint, localPos, independent);
                    }
                }
            }

            // ---------------- 抬起（判定单击 / 双击） ----------------
            if (Input.GetMouseButtonUp(0))
            {
                if (active != null)
                {
                    if (dragPoint != null)
                    {
                        if (!dragging && dragPoint.kind == PointKind.Anchor)
                        {
                            // 双击中间点：直线锚点 <-> 曲线锚点
                            if (pendingAnchorIndex == dragPoint.nodeIndex
                                && Time.time - pendingAnchorTime < DoubleClickTime)
                            {
                                active.ToggleSmooth(dragPoint.nodeIndex);
                                pendingAnchorIndex = -1;
                            }
                            else
                            {
                                pendingAnchorIndex = dragPoint.nodeIndex;
                                pendingAnchorTime = Time.time;
                            }
                        }
                        dragPoint = null;
                    }
                    else if (!pressConsumed && !deletePointMode)
                    {
                        Vector3 worldPos;
                        if (ProjectMouseToPlane(ray, out worldPos))
                        {
                            pendingAnchorIndex = -1;
                            Vector3 localPos = active.transform.InverseTransformPoint(worldPos);
                            if (isDraft)
                            {
                                active.AddNode(localPos); // 按先后顺序连线（默认直线）
                            }
                            else if (addPointMode)
                            {
                                InsertOrAppendNode(localPos);
                            }
                        }
                    }
                    pressConsumed = false;
                }
                else
                {
                    // 空闲：双击模型 -> 二次编辑
                    CurveModel m = PickModel(ray);
                    if (m != null)
                    {
                        if (pendingModel == m && Time.time - pendingModelTime < DoubleClickTime)
                        {
                            BeginReedit(m);
                            pendingModel = null;
                        }
                        else
                        {
                            pendingModel = m;
                            pendingModelTime = Time.time;
                        }
                    }
                }
            }
        }

        private void InsertOrAppendNode(Vector3 localPos)
        {
            // 优先在最近的一段折线内插入，否则追加到末尾
            int bestIndex = -1;
            float bestSqr = float.MaxValue;
            Vector3 bestPoint = localPos;
            var nodes = active.path.nodes;
            for (int i = 0; i < nodes.Count - 1; i++)
            {
                Vector3 a = nodes[i].position;
                Vector3 b = nodes[i + 1].position;
                Vector3 ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(localPos - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                Vector3 p = a + ab * t;
                float d = (p - localPos).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; bestIndex = i; bestPoint = p; }
            }
            float threshold = GizmoSize * 2.5f;
            if (bestIndex >= 0 && bestSqr < threshold * threshold)
                active.InsertNode(bestIndex, bestPoint);
            else
                active.AddNode(localPos);
        }

        private bool ProjectMouseToPlane(Ray ray, out Vector3 point)
        {
            var pl = new UnityEngine.Plane(Plane.planeNormal, Vector3.zero);
            float enter;
            if (pl.Raycast(ray, out enter))
            {
                point = ray.GetPoint(enter);
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        private CurveModel PickModel(Ray ray)
        {
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, 1000f))
                return hit.collider.GetComponentInParent<CurveModel>();
            return null;
        }

        // ================================================================
        // 旋转界面鼠标交互
        // ================================================================
        private void HandleRotateMouse()
        {
            if (MouseOverGUI()) return;

            if (Input.GetMouseButtonDown(0))
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                rotateTarget = PickModel(ray);
                lastRotateMouse = Input.mousePosition;
                if (rotateTarget != null)
                    rotateBeforeQuaternion = rotateTarget.transform.rotation; // 记录拖拽前旋转
            }

            if (Input.GetMouseButton(0) && rotateTarget != null)
            {
                Vector3 cur = Input.mousePosition;
                Vector3 delta = cur - lastRotateMouse;
                lastRotateMouse = cur;

                const float speed = 0.4f;
                if (Input.GetKey(KeyCode.X))
                {
                    rotateTarget.transform.Rotate(Vector3.right, delta.x * speed, Space.World);
                }
                else if (Input.GetKey(KeyCode.Y))
                {
                    rotateTarget.transform.Rotate(Vector3.up, delta.x * speed, Space.World);
                }
                else if (Input.GetKey(KeyCode.Z))
                {
                    rotateTarget.transform.Rotate(Vector3.forward, delta.x * speed, Space.World);
                }
                else
                {
                    rotateTarget.transform.Rotate(Vector3.up, delta.x * speed, Space.World);
                    rotateTarget.transform.Rotate(cam.transform.right, -delta.y * speed, Space.World);
                }
            }

            if (Input.GetMouseButtonUp(0))
            {
                // 仅在确有旋转变化时入栈，避免空操作污染历史
                if (rotateTarget != null &&
                    Quaternion.Angle(rotateBeforeQuaternion, rotateTarget.transform.rotation) > 0.01f)
                {
                    PushRotateUndo(rotateTarget, rotateBeforeQuaternion);
                    rotateRedoStack.Clear(); // 新操作清空重做栈
                }
                rotateTarget = null;
            }
        }

        // ---------------- 旋转历史（撤销 / 重做） ----------------
        private void PushRotateUndo(CurveModel model, Quaternion rotation)
        {
            rotateUndoStack.Add(new RotateSnapshot(model, rotation));
            if (rotateUndoStack.Count > MaxRotateHistory)
                rotateUndoStack.RemoveAt(0); // 超过 20 条，丢弃最早的记录
        }

        /// <summary>Ctrl+Z：回到上一次旋转前的状态</summary>
        private void UndoRotate()
        {
            if (rotateUndoStack.Count == 0) return;
            int last = rotateUndoStack.Count - 1;
            var entry = rotateUndoStack[last];
            rotateUndoStack.RemoveAt(last);

            if (entry.model != null)
            {
                // 撤销前把当前状态压入重做栈
                rotateRedoStack.Add(new RotateSnapshot(entry.model, entry.model.transform.rotation));
                entry.model.transform.rotation = entry.rotation;
            }
        }

        /// <summary>Ctrl+Shift+Z / Ctrl+Y：重做被撤销的旋转</summary>
        private void RedoRotate()
        {
            if (rotateRedoStack.Count == 0) return;
            int last = rotateRedoStack.Count - 1;
            var entry = rotateRedoStack[last];
            rotateRedoStack.RemoveAt(last);

            if (entry.model != null)
            {
                rotateUndoStack.Add(new RotateSnapshot(entry.model, entry.model.transform.rotation));
                entry.model.transform.rotation = entry.rotation;
            }
        }

        // ================================================================
        // 曲线的开始 / 确定 / 取消
        // ================================================================
        private void StartNewCurve()
        {
            if (active != null) return;

            currentColor = RandomCurveColor(); // 每次绘制自动随机配色
            var go = new GameObject("curve draft");
            var m = go.AddComponent<CurveModel>();
            m.color = currentColor;
            m.radiusCm = CurrentRadius();
            m.Init();
            m.BeginDraft(GizmoSize);

            active = m;
            isDraft = true;
            addPointMode = false;
            deletePointMode = false;
            pendingAnchorIndex = -1;
        }

        private void BeginReedit(CurveModel m)
        {
            active = m;
            isDraft = false;
            addPointMode = false;
            deletePointMode = false;
            currentColor = m.color;
            radiusText = m.radiusCm.ToString("0.###", CultureInfo.InvariantCulture);
            m.BeginEdit(GizmoSize);
        }

        private void ConfirmEdit()
        {
            if (active == null || active.path.nodes.Count < 2) return;

            float r = CurrentRadius();
            active.SetColor(currentColor);
            active.Commit(r);

            if (isDraft)
                active.gameObject.name = FindNextCurveName(); // curve n 自动识别命名

            active = null;
            isDraft = false;
            addPointMode = false;
            deletePointMode = false;
        }

        private void CancelEdit()
        {
            if (active == null) return;
            if (isDraft)
                Destroy(active.gameObject); // 取消：丢弃本次全部操作
            else
                active.RevertEdit();        // 二次编辑取消：恢复原模型
            active = null;
            isDraft = false;
            addPointMode = false;
            deletePointMode = false;
        }

        /// <summary>扫描场景根节点，找最小的未被占用编号（自动补缺、识别改名）</summary>
        private string FindNextCurveName()
        {
            var used = new HashSet<string>();
            var scene = gameObject.scene;
            foreach (var root in scene.GetRootGameObjects()) used.Add(root.name);

            int n = 1;
            while (used.Contains("curve " + n.ToString(CultureInfo.InvariantCulture))) n++;
            return "curve " + n.ToString(CultureInfo.InvariantCulture);
        }

        private float CurrentRadius()
        {
            float r;
            if (float.TryParse(radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out r) && r > 0f)
                return r;
            return 1f; // 默认 1cm
        }

        private Color RandomCurveColor()
        {
            return Color.HSVToRGB(Random.value, Random.Range(0.65f, 0.9f), 1f);
        }

        // ================================================================
        // IMGUI
        // ================================================================
        void OnGUI()
        {
            InitStyles();
            guiScreenRects.Clear();

            DrawTopToolbar();

            if (mode == WorkMode.Design) DrawDesignPanel();
            else if (mode == WorkMode.Rotate) DrawRotatePanel();
            else DrawObserveHint();

            DrawBottomHint();

            // 编辑中：右下 确定 / 取消
            if (mode == WorkMode.Design && active != null)
                DrawConfirmBar();

            // 退出演示确认菜单（Esc 不能跳过，必须手动点）
            if (quitDialog) DrawQuitDialog();
        }

        private void InitStyles()
        {
            if (boldLabel == null)
            {
                boldLabel = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
                hintLabel = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                hintLabel.normal.textColor = new Color(0.85f, 0.85f, 0.8f);
            }
        }

        /// <summary>记录 GUI 矩形（转屏幕坐标），用于判断鼠标是否在界面上</summary>
        private Rect TR(Rect r)
        {
            guiScreenRects.Add(new Rect(r.x, Screen.height - r.yMax, r.width, r.height));
            return r;
        }

        private bool MouseOverGUI()
        {
            Vector2 m = Input.mousePosition;
            foreach (var r in guiScreenRects)
                if (r.Contains(m)) return true;
            return false;
        }

        private void DrawTopToolbar()
        {
            const float w = 470f, h = 34f;
            var rect = TR(new Rect((Screen.width - w) * 0.5f, 10f, w, h));
            GUI.BeginGroup(rect);
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none);
            var br = new Rect(6, 4, 150, 26);
            if (ModeButton(br, "观察界面 F1", mode == WorkMode.Observe)) EnterMode(WorkMode.Observe, false);
            br.x += 156;
            if (ModeButton(br, "设计界面 F2", mode == WorkMode.Design)) EnterMode(WorkMode.Design, true);
            br.x += 156;
            if (ModeButton(br, "旋转界面 F3", mode == WorkMode.Rotate)) EnterMode(WorkMode.Rotate, true);
            GUI.EndGroup();
        }

        private bool ModeButton(Rect r, string text, bool on)
        {
            Color old = GUI.backgroundColor;
            if (on) GUI.backgroundColor = new Color(0.45f, 0.7f, 1f);
            bool v = GUI.Button(r, text);
            GUI.backgroundColor = old;
            return v;
        }

        private bool ToggleButton(Rect r, string text, bool on)
        {
            Color old = GUI.backgroundColor;
            if (on) GUI.backgroundColor = new Color(1f, 0.75f, 0.3f);
            bool v = GUI.Button(r, text);
            GUI.backgroundColor = old;
            return v;
        }

        private void DrawDesignPanel()
        {
            const float w = 246f;
            var panel = TR(new Rect(10f, 54f, w, 210f));
            GUI.BeginGroup(panel);
            GUI.Box(new Rect(0, 0, w, 210f), GUIContent.none);

            GUI.Label(new Rect(10, 6, 60, 20), "平面", boldLabel);
            for (int i = 0; i < 3; i++)
            {
                var r = new Rect(10 + i * 76f, 26f, 70f, 26f);
                bool on = (int)planeView == i;
                if (ModeButton(r, planes[i].label + " " + (i + 1), on))
                    SetPlane((PlaneViewType)i);
            }

            GUI.Label(new Rect(10, 60, 100, 20), "操作", boldLabel);
            Color old = GUI.backgroundColor;
            GUI.enabled = active == null;
            if (GUI.Button(new Rect(60f, 58f, 176f, 24f), "曲线路径（新建）"))
                StartNewCurve();
            GUI.enabled = true;

            GUI.Label(new Rect(10, 90, 100, 20), "功能", boldLabel);
            if (ToggleButton(new Rect(60f, 88f, 86f, 24f), "添加点", addPointMode))
            {
                addPointMode = !addPointMode;
                deletePointMode = false;
            }
            if (ToggleButton(new Rect(150f, 88f, 86f, 24f), "删除点", deletePointMode))
            {
                deletePointMode = !deletePointMode;
                addPointMode = false;
            }

            // 半径：可改数值，单位固定显示 cm 不可改
            GUI.Label(new Rect(10, 122, 60, 20), "半径", boldLabel);
            GUI.SetNextControlName("RadiusField");
            radiusText = GUI.TextField(new Rect(60f, 120f, 70f, 22f), radiusText, 8);
            GUI.Label(new Rect(134f, 121f, 100f, 20f), "cm（单位固定）");

            // 颜色选区
            GUI.Label(new Rect(10, 152, 60, 20), "颜色", boldLabel);
            var colorBtn = new Rect(60f, 150f, 86f, 24f);
            GUI.backgroundColor = currentColor;
            if (GUI.Button(colorBtn, GUIContent.none)) pickerOpen = !pickerOpen;
            GUI.backgroundColor = old;
            GUI.Label(new Rect(150f, 153f, 90f, 20f), "点击自选颜色");

            GUI.Label(new Rect(10, 182f, 230f, 24f),
                active != null
                    ? (isDraft ? "正在绘制新曲线" : "正在二次编辑模型")
                    : "点击「曲线路径」开始，或双击已有模型", hintLabel);
            GUI.EndGroup();

            if (pickerOpen) DrawColorPicker(10f, 270f);
        }

        private void DrawColorPicker(float x, float y)
        {
            const float w = 246f, h = 128f;
            TR(new Rect(x, y, w, h));
            GUI.BeginGroup(new Rect(x, y, w, h));
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none);

            GUI.Label(new Rect(10, 4, 200, 18), "R", hintLabel);
            currentColor.r = GUI.HorizontalSlider(new Rect(30f, 8f, 200f, 18f), currentColor.r, 0f, 1f);
            GUI.Label(new Rect(10, 28f, 200, 18), "G", hintLabel);
            currentColor.g = GUI.HorizontalSlider(new Rect(30f, 32f, 200f, 18f), currentColor.g, 0f, 1f);
            GUI.Label(new Rect(10, 52f, 200, 18), "B", hintLabel);
            currentColor.b = GUI.HorizontalSlider(new Rect(30f, 56f, 200f, 18f), currentColor.b, 0f, 1f);

            Color[] presets =
            {
                Color.red, new Color(1f,0.5f,0f), Color.yellow, Color.green,
                Color.cyan, Color.blue, Color.magenta, Color.white
            };
            for (int i = 0; i < presets.Length; i++)
            {
                Color old = GUI.backgroundColor;
                GUI.backgroundColor = presets[i];
                if (GUI.Button(new Rect(10f + i * 28f, 84f, 24f, 24f), GUIContent.none))
                    currentColor = presets[i];
                GUI.backgroundColor = old;
            }
            if (GUI.Button(new Rect(180f, 84f, 56f, 24f), "随机"))
                currentColor = RandomCurveColor();
            GUI.EndGroup();
        }

        private void DrawRotatePanel()
        {
            const float w = 246f, h = 158f;
            var panel = TR(new Rect(10f, 54f, w, h));
            GUI.BeginGroup(panel);
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none);
            GUI.Label(new Rect(10, 6, 60, 20), "平面", boldLabel);
            for (int i = 0; i < 3; i++)
            {
                if (ModeButton(new Rect(10 + i * 76f, 26f, 70f, 26f), planes[i].label + " " + (i + 1), (int)planeView == i))
                    SetPlane((PlaneViewType)i);
            }

            // 撤销 / 还原按钮，无历史时置灰
            Color oldBg = GUI.backgroundColor;
            bool couldUndo = rotateUndoStack.Count > 0;
            bool couldRedo = rotateRedoStack.Count > 0;

            GUI.enabled = couldUndo;
            if (couldUndo) GUI.backgroundColor = new Color(0.45f, 0.7f, 1f);
            if (GUI.Button(new Rect(10f, 58f, 110f, 26f), "撤销 Ctrl+Z")) UndoRotate();
            GUI.enabled = true;
            GUI.backgroundColor = oldBg;

            GUI.enabled = couldRedo;
            if (couldRedo) GUI.backgroundColor = new Color(0.45f, 0.7f, 1f);
            if (GUI.Button(new Rect(126f, 58f, 110f, 26f), "还原 Ctrl+Shift+Z")) RedoRotate();
            GUI.enabled = true;
            GUI.backgroundColor = oldBg;

            GUI.Label(new Rect(10, 92f, 236f, 60f),
                "点击模型后拖动旋转；按住 X / Y / Z 可限定单轴旋转。\n滚轮仅缩放视角距离，模型大小不变。\n撤销/重做仅记录旋转操作，最多 20 步。", hintLabel);
            GUI.EndGroup();
        }

        private void DrawObserveHint()
        {
            const float w = 246f, h = 124f;
            var panel = TR(new Rect(10f, 54f, w, h));
            GUI.BeginGroup(panel);
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none);

            GUI.Label(new Rect(10, 6, 60, 20), "平面", boldLabel);
            for (int i = 0; i < 3; i++)
            {
                if (ModeButton(new Rect(10 + i * 76f, 26f, 70f, 26f), planes[i].label + " " + (i + 1), (int)planeView == i))
                    SetPlaneObserve((PlaneViewType)i);
            }

            GUI.Label(new Rect(10, 58f, 230f, 64f),
                "观察界面（相机自由，不可编辑模型）：\n右键拖动转向，W/A/S/D 移动，Q/E 升降，\n滚轮前进，中键平移；1/2/3 快速归位视角。", hintLabel);
            GUI.EndGroup();
        }

        private void DrawBottomHint()
        {
            string msg;
            if (mode == WorkMode.Design)
            {
                if (active != null)
                    msg = "单击空白加点连线（默认直线）｜双击点变曲线锚点｜拖控制点弯曲（Ctrl=单侧）｜Enter 确定 / Esc 取消";
                else
                    msg = "双击模型可重新编辑曲线｜红/绿/蓝线为 X/Y/Z 世界轴";
            }
            else if (mode == WorkMode.Rotate)
            {
                msg = rotateTarget != null
                    ? "正在旋转：" + rotateTarget.name
                    : "红/绿/蓝线为 X/Y/Z 世界轴";
            }
            else msg = "";
            if (!string.IsNullOrEmpty(msg))
                GUI.Label(new Rect(12f, Screen.height - 28f, Screen.width - 24f, 22f), msg, hintLabel);
        }

        private void DrawConfirmBar()
        {
            float w = 190f, h = 32f;
            var bar = TR(new Rect(Screen.width - w - 20f, Screen.height - h - 18f, w, h));
            GUI.BeginGroup(bar);
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none);
            bool canConfirm = active != null && active.path.nodes.Count >= 2;
            Color old = GUI.backgroundColor;
            GUI.enabled = canConfirm;
            GUI.backgroundColor = new Color(0.35f, 0.85f, 0.45f);
            if (GUI.Button(new Rect(8f, 4f, 82f, 24f), "确定 Enter")) ConfirmEdit();
            GUI.enabled = true;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.4f);
            if (GUI.Button(new Rect(98f, 4f, 82f, 24f), "取消 Esc")) CancelEdit();
            GUI.backgroundColor = old;
            GUI.EndGroup();
        }

        private void DrawQuitDialog()
        {
            const float w = 380f, h = 168f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            TR(rect);
            GUI.BeginGroup(rect);
            GUI.Box(new Rect(0, 0, w, h), GUIContent.none);
            GUI.Label(new Rect(20f, 16f, w - 40f, 26f), "确认退出演示 / 游玩模式？", boldLabel);
            GUI.Label(new Rect(20f, 48f, w - 40f, 60f),
                "该确认窗口不能用 Esc 关闭，必须手动点击按钮。\n选择「继续演示」将返回当前界面。");

            Color old = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.4f);
            if (GUI.Button(new Rect(30f, h - 44f, 140f, 32f), "确认退出"))
            {
                quitDialog = false;
                DoQuit();
            }
            GUI.backgroundColor = new Color(0.35f, 0.85f, 0.45f);
            if (GUI.Button(new Rect(w - 170f, h - 44f, 140f, 32f), "继续演示"))
            {
                quitDialog = false;
            }
            GUI.backgroundColor = old;
            GUI.EndGroup();
        }

        private void DoQuit()
        {
#if UNITY_EDITOR
            // 通知 Editor 保险脚本：本次退出已确认，不再弹编辑器确认框
            EditorPrefs.SetInt("BezierStudio_ConfirmedQuit", 1);
            EditorApplication.isPlaying = false;
#else
            quitConfirmed = true;
            Application.Quit();
#endif
        }
    }
}
