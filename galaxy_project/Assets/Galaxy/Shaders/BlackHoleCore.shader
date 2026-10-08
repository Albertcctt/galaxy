// ============================================================================
// 黑洞核心着色器：不透明纯黑 + 写深度。
//
// 为什么不能用 Sprites/Default（透明队列、不写深度）：吸积盘与螺旋粒子都是
// "单渲染器"——远半侧与近半侧在同一次绘制里，透明排序只对整个渲染器生效，
// 远侧的光会画在黑球之上（光"穿过"黑洞，物理错误）。本 shader 让核心进入
// 不透明队列并写深度：核心后方的一切透明几何（盘远侧、粒子远侧、背后实体）
// 被深度测试裁剪 —— 光被黑洞吸收，不能穿过。
// Cull Back：只画正面半球（黑体无内壁）。
// ============================================================================
Shader "Galaxy/BlackHoleCore"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            ZWrite On
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(0, 0, 0, 1);   // 纯黑：无光照、无高光
            }
            ENDCG
        }
    }
}
