using UnityEngine;

namespace BezierStudio
{
    /// <summary>
    /// 观察界面（F1）：仿 Unity Scene 视图的自由飞行相机。
    /// 按住鼠标右键拖动 = 转向；W/A/S/D = 平移，Q/E = 下降/上升；
    /// 鼠标滚轮 = 前后移动；按住中键拖动 = 平行移动。
    /// 组件仅在观察界面启用，设计/旋转界面由主控脚本接管相机。
    /// </summary>
    public class FreeLookCamera : MonoBehaviour
    {
        public float lookSensitivity = 0.12f;
        public float moveSpeed = 8f;
        public float wheelSensitivity = 6f;

        private float yaw;
        private float pitch;
        private float speedScale = 1f;

        void OnEnable()
        {
            Vector3 e = transform.eulerAngles;
            yaw = e.y;
            pitch = e.x;
        }

        /// <summary>
        /// 观察界面切换正视/侧视/俯视时，把相机瞬移到指定位置和朝向，
        /// 并同步内部 yaw/pitch，避免下一次右键转向时角度跳变。
        /// </summary>
        public void SnapTo(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);
            Vector3 e = rotation.eulerAngles;
            yaw = e.y;
            pitch = e.x;
            speedScale = 1f; // 重置速度档位
        }

        void Update()
        {
            // ---- 右键转向 ----
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * lookSensitivity * 5f;
                pitch -= Input.GetAxis("Mouse Y") * lookSensitivity * 5f;
                pitch = Mathf.Clamp(pitch, -89f, 89f);
            }
            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

            // ---- 键盘移动 ----
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) move += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) move += Vector3.back;
            if (Input.GetKey(KeyCode.A)) move += Vector3.left;
            if (Input.GetKey(KeyCode.D)) move += Vector3.right;
            if (Input.GetKey(KeyCode.E)) move += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) move += Vector3.down;

            float wheel = Input.GetAxis("Mouse ScrollWheel");
            speedScale = Mathf.Clamp(speedScale * (1f + wheel), 0.05f, 50f);

            if (move.sqrMagnitude > 0f)
            {
                transform.position += transform.rotation * move.normalized * moveSpeed * speedScale * Time.deltaTime;
            }

            // ---- 滚轮前后 ----
            if (Mathf.Abs(wheel) > 0f && !Input.GetMouseButton(1))
            {
                transform.position += transform.forward * wheel * wheelSensitivity * speedScale;
            }

            // ---- 中键平移 ----
            if (Input.GetMouseButton(2))
            {
                float dx = -Input.GetAxis("Mouse X") * moveSpeed * speedScale * Time.deltaTime;
                float dy = -Input.GetAxis("Mouse Y") * moveSpeed * speedScale * Time.deltaTime;
                transform.position += transform.right * dx + transform.up * dy;
            }
        }
    }
}
