// ============================================================================
// 粒子氛围：星系尺度的缓慢星尘（纯视觉装饰）。
//
// 全代码搭建一个 ParticleSystem：球形发射区（半径 = 星系取景半径）、低速
// 漂移、长寿命、微小尺寸、低透明度 —— 像悬浮在太空里的微尘，给静态画面
// 添一点呼吸感。粒子在世界空间模拟：相机旋转时它们像真的星尘一样留在原地。
//
// 材质用 Sprites/Default（无纹理 = 白色软点，被顶点色/主色调制；随管线
// 始终可用，无剥离风险）。不参与任何交互。
// ============================================================================
using UnityEngine;
using UnityEngine.Rendering;

namespace Galaxy
{
    public class GalaxyParticles : MonoBehaviour
    {
        private const float Lifetime = 30f;   // 寿命同时决定"填满到稳态"的时间尺度
        private const int AbsMin = 400;
        private const int AbsMax = 8000;

        private ParticleSystem m_Ps;
        private ParticleSystem.EmissionModule m_Emission;

        /// <summary>当前粒子规模（设置面板显示用）。</summary>
        public int CurrentCount { get; private set; }

        /// <summary>默认规模公式：随文件数线性增长（大星系更多星尘）。</summary>
        public static int DefaultCount(int fileCount, int dustBase, int dustPerFile)
        {
            return Mathf.Clamp(dustBase + fileCount * dustPerFile, AbsMin, AbsMax);
        }

        public void Configure(float galaxyRadius, int particleCount)
        {
            m_Ps = gameObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = m_Ps.main;
            main.loop = true;
            main.startLifetime = Lifetime;
            main.startSpeed = 0.06f;             // 极慢漂移
            main.startSize = new ParticleSystem.MinMaxCurve(0.04f, 0.13f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 0f;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.55f, 0.75f, 1f, 0.05f),
                new Color(0.85f, 0.92f, 1f, 0.16f));

            m_Emission = m_Ps.emission;

            ParticleSystem.ShapeModule shape = m_Ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = galaxyRadius;

            // 用尺寸曲线做淡入淡出（避免 alpha 渐变的模块复杂度）：0 → 峰值 → 0
            ParticleSystem.SizeOverLifetimeModule size = m_Ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.15f),
                new Keyframe(0.3f, 1f),
                new Keyframe(1f, 0.1f)));

            var pr = GetComponent<ParticleSystemRenderer>();
            pr.material = new Material(Shader.Find("Sprites/Default")) { name = "galaxy-dust" };
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.shadowCastingMode = ShadowCastingMode.Off;
            pr.receiveShadows = false;

            SetCount(particleCount);
            m_Ps.Play();
        }

        /// <summary>
        /// 实时调整粒子规模（设置面板）：上限与发射率一起改 —— 发射率 = 目标数/寿命，
        /// 升降都只影响新增部分（已有粒子自然老去），约一个寿命内平滑过渡。
        /// </summary>
        public void SetCount(int count)
        {
            if (m_Ps == null) return;
            count = Mathf.Clamp(count, 0, AbsMax);
            CurrentCount = count;
            m_Ps.maxParticles = Mathf.Max(count, 1);   // maxParticles 下限为 1（0 不合法）
            m_Emission.rateOverTime = count / Lifetime;
        }
    }
}
