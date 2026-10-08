// ============================================================================
// 黑洞（散文件宇宙专属，动态装饰 v2：黑色核心 + 发光吸积系统）。
//
// 组成（全部尺寸 ∝ 核心半径 R，由 Builder 给定）：
//   1) 黑色核心      —— 无光照纯黑球（引力井的视觉体）
//   2) 吸积盘带      —— 半透明环带（三角条带 + 顶点色径向渐变：内缘热亮、
//                        外缘淡出；逐段哈希扰动透明度制造"气流"，自旋可见）
//   3) 光子环        —— 核心轮廓处的多层相机朝向亮环（billboard，每帧重建；
//                        HDR 色吃 Bloom，"发光天体"的远距可读性）
//   4) 双层虚线轨道  —— 平盘 + 62° 斜盘的反向自旋虚线环
//   5) 螺旋吸入流    —— 局部空间粒子系统：绕 Y 公转 + 径向负速度 => 发光微尘
//                        沿盘面螺旋坠向核心（抵达事件视界前淡出）
//   6) 节奏          —— 双频呼吸 + 周期耀斑（光子环/盘带/转速短暂增亮加快）
//
// 为什么需要"扰动/虚线"：完美轴对称的盘或环绕轴自旋在视觉上是静止的
//（旋转不变性）——气流扰动与虚线缺口才是让运动可见的东西。
//
// 定位：纯装饰 —— 不参与拾取（GalaxyPicker 只认实体列表）、不参与碰撞。
// 动画只依赖 Time.time；几何在 Build 时成型（光子环随相机每帧重建，数百顶点）。
// ============================================================================
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Galaxy
{
    public class BlackHole : MonoBehaviour
    {
        private const int RingSegments = 40;      // 虚线轨道每圈弧段数
        private const float RingDuty = 0.62f;     // 弧段占空比
        private const int PhotonSegments = 48;    // 光子环圆段数
        private static readonly float[] PhotonRadii = { 1.04f, 1.10f, 1.18f, 1.30f, 1.45f };
        private static readonly float[] PhotonAlphas = { 0.95f, 0.45f, 0.25f, 0.13f, 0.07f };

        private float m_CoreRadius;
        private float m_Phase;
        private Transform m_Ring1;
        private Transform m_Ring2;

        private Mesh m_PhotonMesh;
        private readonly List<Vector3> m_PhotonVerts = new List<Vector3>();
        private readonly List<Color> m_PhotonColors = new List<Color>();
        private Color m_PhotonHot;

        private Mesh m_DiskMesh;
        private Material m_DiskMat;
        private Color m_DiskInner;

        private ParticleSystem m_Spiral;

        // 运行时创建的资产（几何/材质不随 GameObject 销毁回收）：统一登记，
        // OnDestroy 时释放，避免每次 Rebuild 泄漏
        private readonly List<Object> m_RuntimeAssets = new List<Object>();

        /// <summary>构建黑洞（coreRadius = 黑色核心半径；tint = 吸积系统色相 = 宇宙色调；
        /// feeders = 本宇宙全部实体的世界坐标与色调 —— 每实体一条"吞噬光束"）。</summary>
        public void Build(float coreRadius, Color tint, List<Vector3> feederPositions, List<Color> feederColors)
        {
            m_CoreRadius = coreRadius;
            m_Phase = coreRadius * 7.13f;   // 每个黑洞相位错开（同尺寸也各异，纯视觉）

            Color hot = Color.Lerp(tint, Color.white, 0.55f);
            m_PhotonHot = new Color(hot.r * 1.55f, hot.g * 1.55f, hot.b * 1.55f, 1f);   // HDR：吃 Bloom

            BuildCore(coreRadius, tint);
            BuildAccretionDisk(coreRadius, tint, hot);
            BuildPhotonRing(hot);
            m_Ring1 = CreateRing("AccretionRing1", coreRadius * 1.5f, 0f,
                                 new Color(tint.r, tint.g, tint.b, 0.55f));
            m_Ring2 = CreateRing("AccretionRing2", coreRadius * 1.9f, 62f,
                                 new Color(tint.r, tint.g, tint.b, 0.32f));
            BuildSpiral(coreRadius, tint, hot);
            BuildFeederStreams(feederPositions, feederColors);
        }

        // ---- 7) 吞噬光束：本宇宙每个实体 → 黑洞一条"内收光焰"——
        //      实体端最宽（发射口），沿路径单调收细（越靠近黑洞越细），尖端
        //      停在黑洞外 2.2×核心半径处：与光子环之间留出明确空隙，绝不接触
        //      核心。颜色沿程从自身色渐变到白热；内部保留一条白热芯线。
        //      短划沿焰体朝黑洞流动（"向内收缩"的动势）。
        //      几何静止（实体不动），动画全在 shader 的 _Time；走正常深度
        //      （StreamLine 着色器），被前景实体正常遮挡。----
        private const int ConeSegments = 10;   // 焰体周向分段
        // 光尖到黑洞【表面】的间隙（×核心半径）：光终点距中心 = 核心半径 ×(1+本值)。
        // 用"表面 + 间隙"的表达而非"中心距离倍数"，保证空隙随核心大小等比成立
        private const float StreamSurfaceGap = 0.6f;

        private void BuildFeederStreams(List<Vector3> positions, List<Color> colors)
        {
            if (positions == null || positions.Count == 0) return;

            // 光终点半径（距核心中心）：核心半径 + 表面间隙 —— 全部光的末端统一由该公式决定
            float tipR = m_CoreRadius * (1f + StreamSurfaceGap);
            var coreVerts = new List<Vector3>();
            var coreCols = new List<Color>();
            var coreUvs = new List<Vector2>();
            var coneVerts = new List<Vector3>();
            var coneCols = new List<Color>();
            var coneUvs = new List<Vector2>();
            var coneTris = new List<int>();

            for (int i = 0; i < positions.Count; i++)
            {
                // 世界坐标转本地（黑洞仅位置+脉冲缩放，旋转为单位）——顶点随脉冲一起呼吸
                Vector3 local = transform.InverseTransformPoint(positions[i]);
                float len = local.magnitude;
                if (len < tipR + 0.05f) continue;
                // 尖端位置 = 放射方向 × 终点半径（本侧、核心之外）。注意不能写
                // 成"指向黑洞的方向 × 半径"——那会把端点点到实体【对侧】，
                // 光便从实体贯穿核心（实测根因：光穿过黑洞）
                Vector3 radial = local / len;        // 单位放射向量（黑洞中心 → 实体）
                Vector3 end = radial * tipR;         // 尖端：与实体同侧、距表面留有间隙
                Vector3 dir = -radial;               // 锥轴方向（指向黑洞），供截面基向量
                Color hue = i < colors.Count ? colors[i] : Color.white;
                float seed = (i % 7) * 0.137f;

                // (a) 白热内芯：1px 流光线（与焰体同端同尖）
                Color coreNear = hue; coreNear.a = 0.42f;
                Color coreHot = new Color(1.6f, 1.6f, 1.6f, 0.9f);
                coreVerts.Add(local); coreCols.Add(coreNear); coreUvs.Add(new Vector2(0f, seed));
                coreVerts.Add(end);   coreCols.Add(coreHot);  coreUvs.Add(new Vector2(len, seed));

                // (b) 内收光焰：实体端最宽 → 沿程收细 → 尖端细如针（越近越细）
                float rE = Mathf.Clamp(len * 0.14f, m_CoreRadius * 0.35f, m_CoreRadius * 1.2f);
                float rM = rE * 0.55f;                                    // 中段：已收过半
                float rT = Mathf.Max(rE * 0.12f, m_CoreRadius * 0.08f);   // 尖端：细
                Vector3 mid = Vector3.Lerp(local, end, 0.55f);

                // 任意与 dir 垂直的基（锥截面圆的两个轴）
                Vector3 upRef = Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right;
                Vector3 rgt = Vector3.Normalize(Vector3.Cross(upRef, dir));
                Vector3 up2 = Vector3.Cross(dir, rgt);

                Color cStart = hue; cStart.a = 0.35f;                         // 实体端：自身色
                Color cMid = Color.Lerp(hue, Color.white, 0.6f); cMid.a = 0.42f;
                Color cThroat = new Color(1.6f, 1.6f, 1.6f, 0.6f);            // 尖端：白热（细束）

                int baseIdx = coneVerts.Count;
                for (int ring = 0; ring < 3; ring++)
                {
                    Vector3 center = ring == 0 ? local : ring == 1 ? mid : end;
                    float radius = ring == 0 ? rE : ring == 1 ? rM : rT;
                    Color col = ring == 0 ? cStart : ring == 1 ? cMid : cThroat;
                    float u = ring == 0 ? 0f : ring == 1 ? len * 0.55f : len;
                    for (int s = 0; s < ConeSegments; s++)
                    {
                        float a = (s / (float)ConeSegments) * Mathf.PI * 2f;
                        coneVerts.Add(center + (rgt * Mathf.Cos(a) + up2 * Mathf.Sin(a)) * radius);
                        coneCols.Add(col);
                        // uv.y 逐段小偏移：焰面各条"经线"的短划相位错开，像火苗的摇曳
                        coneUvs.Add(new Vector2(u, seed + s * 0.09f));
                    }
                }
                for (int ring = 0; ring < 2; ring++)
                {
                    int r0 = baseIdx + ring * ConeSegments;
                    int r1 = r0 + ConeSegments;
                    for (int s = 0; s < ConeSegments; s++)
                    {
                        int s1 = (s + 1) % ConeSegments;
                        coneTris.Add(r0 + s); coneTris.Add(r1 + s); coneTris.Add(r0 + s1);
                        coneTris.Add(r0 + s1); coneTris.Add(r1 + s); coneTris.Add(r1 + s1);
                    }
                }
            }
            if (coreVerts.Count == 0) return;

            Shader shader = Shader.Find("Galaxy/StreamLine");
            if (shader == null)
            {
                Debug.LogWarning("[BlackHole] 未找到 Galaxy/StreamLine，吞噬光束退回 OverlayLine");
                shader = Shader.Find("Galaxy/OverlayLine");
            }
            var mat = NewAsset(new Material(shader) { name = "black-hole-streams" });
            mat.SetFloat("_DashLength", 0.9f);
            mat.SetFloat("_FlowSpeed", 1.3f);
            mat.SetFloat("_DashDuty", 0.6f);

            BuildStreamObject("FeederStreamCore", coreVerts, coreCols, coreUvs, null, mat);
            BuildStreamObject("FeederStreamCone", coneVerts, coneCols, coneUvs, coneTris, mat);
        }

        private void BuildStreamObject(string name, List<Vector3> verts, List<Color> cols,
                                       List<Vector2> uvs, List<int> tris, Material mat)
        {
            var mesh = NewAsset(new Mesh { name = name });
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetColors(cols);
            mesh.SetUVs(0, uvs);
            if (tris == null)
            {
                var indices = new int[verts.Count];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
                mesh.SetIndices(indices, MeshTopology.Lines, 0);
            }
            else
            {
                mesh.SetTriangles(tris, 0);
            }
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // ---- 1) 黑色核心（Galaxy/BlackHoleCore：不透明纯黑 + 写深度 ——
        //      核心后方的一切（盘远侧/粒子远侧/背后实体）被深度裁剪，
        //      "光被吸收、不能穿过"）----
        private void BuildCore(float r, Color tint)
        {
            GameObject core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            core.name = "Core";
            Destroy(core.GetComponent<Collider>());
            core.transform.SetParent(transform, false);
            core.transform.localScale = Vector3.one * (r * 2f);
            MeshRenderer cmr = core.GetComponent<MeshRenderer>();
            Shader shader = Shader.Find("Galaxy/BlackHoleCore");
            if (shader == null)
            {
                Debug.LogWarning("[BlackHole] 未找到 Galaxy/BlackHoleCore，核心退回 Sprites/Default（光会穿过核心）");
                shader = Shader.Find("Sprites/Default");
            }
            var mat = NewAsset(new Material(shader) { name = "black-hole-core" });
            if (mat.HasProperty("_Color")) mat.color = Color.black;
            cmr.sharedMaterial = mat;
            cmr.shadowCastingMode = ShadowCastingMode.Off;
            cmr.receiveShadows = false;
            // 注：tint 参数保留给未来（如加入极微弱的边缘余光）；当前核心保持纯黑
            _ = tint;
        }

        // ---- 2) 吸积盘带：平躺 XZ 面的半透明环带 ----
        private void BuildAccretionDisk(float r, Color tint, Color hot)
        {
            const int seg = 64;
            float rIn = r * 1.25f, rOut = r * 2.15f;
            var verts = new Vector3[seg * 4];
            var colors = new Color[seg * 4];
            var tris = new int[seg * 6];
            m_DiskInner = new Color(hot.r * 1.2f, hot.g * 1.2f, hot.b * 1.2f, 0.40f);   // HDR 内缘
            Color outer = new Color(tint.r, tint.g, tint.b, 0.015f);

            for (int i = 0; i < seg; i++)
            {
                float a0 = (i / (float)seg) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)seg) * Mathf.PI * 2f;
                // 逐段"气流"扰动：透明度 0.55~1.0（轴对称盘的自旋否则不可见）
                float wobble = 0.55f + 0.45f * Mathf.PerlinNoise(i * 0.37f + m_Phase, m_Phase * 0.7f);
                Vector3 in0 = new Vector3(Mathf.Cos(a0) * rIn, 0f, Mathf.Sin(a0) * rIn);
                Vector3 in1 = new Vector3(Mathf.Cos(a1) * rIn, 0f, Mathf.Sin(a1) * rIn);
                Vector3 out0 = new Vector3(Mathf.Cos(a0) * rOut, 0f, Mathf.Sin(a0) * rOut);
                Vector3 out1 = new Vector3(Mathf.Cos(a1) * rOut, 0f, Mathf.Sin(a1) * rOut);
                int b = i * 4;
                verts[b] = in0; verts[b + 1] = out0; verts[b + 2] = in1; verts[b + 3] = out1;
                Color ci = m_DiskInner; ci.a *= wobble;
                colors[b] = ci; colors[b + 1] = outer; colors[b + 2] = ci; colors[b + 3] = outer;
                int t = i * 6;
                tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 2;
                tris[t + 3] = b + 2; tris[t + 4] = b + 1; tris[t + 5] = b + 3;
            }

            m_DiskMesh = NewAsset(new Mesh { name = "black-hole-disk" });
            m_DiskMesh.SetVertices(verts);
            m_DiskMesh.SetColors(colors);
            m_DiskMesh.SetTriangles(tris, 0);
            m_DiskMesh.RecalculateBounds();

            var go = new GameObject("AccretionDisk");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = m_DiskMesh;
            m_DiskMat = NewAsset(new Material(Shader.Find("Sprites/Default")) { name = "black-hole-disk" });
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = m_DiskMat;   // 耀斑时改 _Color 增亮（每黑洞独享实例）
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // ---- 3) 光子环：多层相机朝向同心亮环（billboard，每帧 LateUpdate 重建）----
        private void BuildPhotonRing(Color hot)
        {
            m_PhotonMesh = NewAsset(new Mesh { name = "black-hole-photon" });
            m_PhotonMesh.MarkDynamic();

            var go = new GameObject("PhotonRing");
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = m_PhotonMesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = NewAsset(new Material(Shader.Find("Sprites/Default")) { name = "black-hole-photon" });
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            _ = hot;
            RebuildPhotonRing();   // 先建一帧，避免首帧空网格
        }

        private void RebuildPhotonRing()
        {
            if (m_PhotonMesh == null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            Vector3 right = cam.transform.right;
            Vector3 up = cam.transform.up;

            m_PhotonVerts.Clear();
            m_PhotonColors.Clear();
            for (int layer = 0; layer < PhotonRadii.Length; layer++)
            {
                float radius = m_CoreRadius * PhotonRadii[layer];
                Color c = m_PhotonHot;
                c.a = PhotonAlphas[layer];
                for (int i = 0; i < PhotonSegments; i++)
                {
                    float a0 = (i / (float)PhotonSegments) * Mathf.PI * 2f;
                    float a1 = ((i + 1) / (float)PhotonSegments) * Mathf.PI * 2f;
                    m_PhotonVerts.Add(right * (Mathf.Cos(a0) * radius) + up * (Mathf.Sin(a0) * radius));
                    m_PhotonVerts.Add(right * (Mathf.Cos(a1) * radius) + up * (Mathf.Sin(a1) * radius));
                    m_PhotonColors.Add(c);
                    m_PhotonColors.Add(c);
                }
            }
            var indices = new int[m_PhotonVerts.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            m_PhotonMesh.Clear();
            m_PhotonMesh.SetVertices(m_PhotonVerts);
            m_PhotonMesh.SetColors(m_PhotonColors);
            m_PhotonMesh.SetIndices(indices, MeshTopology.Lines, 0);
            m_PhotonMesh.RecalculateBounds();
        }

        // ---- 5) 螺旋吸入流：局部空间粒子（绕 Y 公转 + 径向负速度，自动螺旋）----
        private void BuildSpiral(float r, Color tint, Color hot)
        {
            var go = new GameObject("SpiralInflow");
            go.transform.SetParent(transform, false);
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);   // XY 发射圆 → 平躺盘面

            var ps = go.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            // 行程调校的核心：|径向速度| × 寿命 ≈ 平均发射半径 —— 粒子恰好"走到
            // 球心附近"寿命耗尽（此前速度过快，粒子冲过球心在另一侧乱绕、再被
            // 寿命硬截断 => 看起来像闪烁；实测修复）
            main.startLifetime = 1.5f;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(r * 0.05f, r * 0.13f);
            main.maxParticles = 200;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;   // orbital 绕本组件圆心（= 黑洞）
            main.gravityModifier = 0f;
            main.startColor = Color.white;

            ParticleSystem.EmissionModule em = ps.emission;
            em.rateOverTime = 60f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = r * 2.2f;
            shape.radiusThickness = 0.2f;       // 环带发射（窄环，起点集中 => 终点也集中）

            ParticleSystem.VelocityOverLifetimeModule vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
            vel.orbitalY = 3.4f;                 // 公转
            vel.radial = -1.05f;                 // 径向内落：1.05 × 1.5s ≈ 1.58 ≈ 到球心

            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 0.6f), new Keyframe(0.35f, 1f), new Keyframe(1f, 0.05f)));   // 缩入中心

            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(hot, 0.45f),
                    new GradientColorKey(tint, 1f),
                },
                new[]
                {
                    // 长段缓降：消亡是"渐渐没入中心"，不是到点熄灭（防闪烁）
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(0.85f, 0.45f),
                    new GradientAlphaKey(0.35f, 0.78f),
                    new GradientAlphaKey(0f, 0.98f),
                });
            col.color = new ParticleSystem.MinMaxGradient(grad);

            var pr = go.GetComponent<ParticleSystemRenderer>();
            pr.material = NewAsset(new Material(Shader.Find("Sprites/Default")) { name = "black-hole-spiral" });
            pr.renderMode = ParticleSystemRenderMode.Billboard;
            pr.shadowCastingMode = ShadowCastingMode.Off;
            pr.receiveShadows = false;

            m_Spiral = ps;
            ps.Play();
        }

        // ---- 4) 虚线轨道环（自旋可见性：完美整圆旋转不变，断弧才看得见）----
        private Transform CreateRing(string name, float radius, float tiltX, Color color)
        {
            var verts = new List<Vector3>(RingSegments * 2);
            var colors = new List<Color>(RingSegments * 2);
            float arc = Mathf.PI * 2f / RingSegments;
            for (int i = 0; i < RingSegments; i++)
            {
                float a0 = i * arc;
                float a1 = a0 + arc * RingDuty;
                verts.Add(new Vector3(Mathf.Cos(a0) * radius, 0f, Mathf.Sin(a0) * radius));
                verts.Add(new Vector3(Mathf.Cos(a1) * radius, 0f, Mathf.Sin(a1) * radius));
                colors.Add(color);
                colors.Add(color);
            }

            var mesh = NewAsset(new Mesh { name = name });
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            var indices = new int[verts.Count];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localRotation = Quaternion.Euler(tiltX, 0f, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = NewAsset(new Material(Shader.Find("Sprites/Default")) { name = name + "-mat" });
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go.transform;
        }

        // ---- 6) 节奏：双频呼吸 + 周期耀斑 ----
        private void Update()
        {
            float t = Time.time;
            float dt = Time.deltaTime;

            // 耀斑：sin 高次幂 => 大部分时间为 0、周期性短促尖峰
            float f = Mathf.Max(0f, Mathf.Sin(t * 0.42f + m_Phase));
            float flare = Mathf.Pow(f, 8f);

            float spin = 1f + 3.5f * flare;
            if (m_Ring1 != null) m_Ring1.Rotate(0f, 26f * dt * spin, 0f, Space.Self);
            if (m_Ring2 != null) m_Ring2.Rotate(0f, -17f * dt * spin, 0f, Space.Self);

            // 双频呼吸（比单频更像"活着"）
            float s = 1f + 0.02f * Mathf.Sin(t * 1.1f + m_Phase) + 0.015f * Mathf.Sin(t * 2.7f + 1.3f);
            transform.localScale = new Vector3(s, s, s);

            // 盘带整带随耀斑增亮（_Color 乘积驱动，顶点色与透明度保持）
            if (m_DiskMat != null)
            {
                float k = 1f + 1.8f * flare;
                m_DiskMat.SetColor("_Color", new Color(k, k, k, 1f));
            }
        }

        private void LateUpdate()
        {
            RebuildPhotonRing();   // 相机可能已移动：光子环保持正对
        }

        // 运行时资产生命周期管理（原生对象必须显式释放；编辑模式用 Immediate 变体）
        private T NewAsset<T>(T asset) where T : Object
        {
            m_RuntimeAssets.Add(asset);
            return asset;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < m_RuntimeAssets.Count; i++)
            {
                Object o = m_RuntimeAssets[i];
                if (o == null) continue;
                if (Application.isPlaying) Destroy(o);
                else DestroyImmediate(o);
            }
            m_RuntimeAssets.Clear();
        }
    }
}
