// ============================================================================
// 轨道相机：左键拖拽旋转 / 滚轮推拉 / 右键拖拽平移焦点。
//
// 输入系统说明：本项目 activeInputHandler = 1（仅新版 Input System），
// 所以必须使用 UnityEngine.InputSystem 的 Mouse.current API ——
// 旧版 Input.GetMouseButton 在这套配置下运行时会直接抛异常。
// 新版 API 是"按帧读取设备快照"模型：每帧从输入系统查询当前按键/位移状态。
//
// 阻尼说明：位置/朝向用 1 - e^(-k·dt) 的指数趋近，而不是 Vector3.Lerp(a,b,0.1f)。
// 后者是"每帧固定比例"，两帧率下速度不同；指数形式在数学上与帧率无关。
// ============================================================================
using UnityEngine;
using UnityEngine.InputSystem;

namespace Galaxy
{
    public class OrbitCamera : MonoBehaviour
    {
        [Header("初始位姿（脚本接管相机摆放，不用手调 Transform）")]
        public Vector3 target = Vector3.zero;
        public float initialYaw = 35f;
        public float initialPitch = 22f;
        public float initialDistance = 45f;

        [Header("范围")]
        public float minDistance = 2f;
        public float maxDistance = 400f;

        [Header("手感")]
        [Tooltip("每像素旋转角度（度）")]
        public float rotateDegreesPerPixel = 0.22f;
        [Tooltip("滚轮每格缩放比例（0.2 ≈ 每格 20%）")]
        public float zoomStepPerNotch = 0.2f;
        [Tooltip("平移速度（实际位移与控制距离成正比）")]
        public float panSpeed = 0.0018f;
        [Tooltip("阻尼响应速度（1/秒）：越大跟手越紧")]
        public float responsiveness = 14f;

        private float m_Yaw;
        private float m_Pitch;
        private float m_Distance;
        private bool m_HasOrbitOverride;

        /// <summary>
        /// 指针拦截钩子：返回 true 表示该屏幕点被 UI 面板占据（如打开着的代码
        /// 查看器/HUD）——此帧的鼠标拖拽/滚轮一律不驱动相机（"操作面板时不影响
        /// 背景"）。由 GalaxyPicker 在 OnEnable 时挂接、OnDisable 时卸载。
        /// </summary>
        public static System.Func<Vector2, bool> PointerBlocked;

        // 记录"标准取景"姿态：供 ReframeInstant 一键恢复（自动化截图用）
        private Vector3 m_OverrideTarget;
        private float m_OverrideYaw;
        private float m_OverridePitch;
        private float m_OverrideDistance;

        // 外部（GalaxyBuilder）按实际星系尺寸设定取景；Start 与 SetOrbit 谁先执行都收敛到同一结果
        public void SetOrbit(Vector3 newTarget, float distance)
        {
            m_HasOrbitOverride = true;
            target = newTarget;
            m_Yaw = initialYaw;
            m_Pitch = initialPitch;
            m_Distance = Mathf.Clamp(distance, minDistance, maxDistance);
            m_OverrideTarget = target;
            m_OverrideYaw = m_Yaw;
            m_OverridePitch = m_Pitch;
            m_OverrideDistance = m_Distance;
            ApplyPose(instant: true);
        }

        // 立即恢复"最后一次 SetOrbit 设定的取景姿态"，清掉用户手动缩放/旋转/平移的偏移。
        // 用途：自动化截图前调用，保证每轮截出的都是同一个标准视角（结果可比）。
        public void ReframeInstant()
        {
            if (!m_HasOrbitOverride) return;
            target = m_OverrideTarget;
            m_Yaw = m_OverrideYaw;
            m_Pitch = m_OverridePitch;
            m_Distance = m_OverrideDistance;
            ApplyPose(instant: true);
        }

        private void Start()
        {
            if (!m_HasOrbitOverride)
            {
                m_Yaw = initialYaw;
                m_Pitch = initialPitch;
                m_Distance = Mathf.Clamp(initialDistance, minDistance, maxDistance);
            }
            ApplyPose(instant: true);
        }

        private void LateUpdate()
        {
            ReadInput();
            ApplyPose(instant: false);
        }

        private void ReadInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;   // 没有鼠标设备（如无头运行）：保持静止

            // UI 拦截：指针悬停在面板（查看器/HUD）上时，拖拽/滚轮都不驱动相机
            if (PointerBlocked != null && PointerBlocked(mouse.position.ReadValue())) return;

            // 左键拖拽：旋转
            if (mouse.leftButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                m_Yaw += delta.x * rotateDegreesPerPixel;
                m_Pitch = Mathf.Clamp(m_Pitch - delta.y * rotateDegreesPerPixel, -89f, 89f);
            }

            // 滚轮：缩放。各平台/驱动"一格"的数值不一致（Windows 常见 ±120 或 ±1），
            // 因此只取符号做固定比例缩放 —— 对两种量纲都稳定。
            float scroll = mouse.scroll.ReadValue().y;
            if (!Mathf.Approximately(scroll, 0f))
            {
                float sign = Mathf.Sign(scroll);
                m_Distance = Mathf.Clamp(
                    m_Distance * Mathf.Exp(-sign * zoomStepPerNotch), minDistance, maxDistance);
            }

            // 右键拖拽：平移焦点。位移随距离等比放大，拉远时手感保持一致。
            if (mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                target -= (transform.right * delta.x + transform.up * delta.y) * panSpeed * m_Distance;
            }
        }

        private void ApplyPose(bool instant)
        {
            // 球面坐标 -> 笛卡尔：相机位于 target 上方 pitch 度、绕 yaw 的 -distance 处
            Quaternion rot = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            Vector3 desiredPos = target + rot * (Vector3.back * m_Distance);
            Quaternion desiredRot = Quaternion.LookRotation(target - desiredPos);

            if (instant)
            {
                transform.position = desiredPos;
                transform.rotation = desiredRot;
                return;
            }

            float k = 1f - Mathf.Exp(-responsiveness * Time.deltaTime);  // 帧率无关的趋近系数
            transform.position = Vector3.Lerp(transform.position, desiredPos, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, k);
        }
    }
}
