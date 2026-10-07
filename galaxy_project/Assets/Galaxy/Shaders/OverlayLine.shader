// ============================================================================
// 高亮叠加线着色器：ZTest Always —— 永远画在场景所有几何之上（"照穿"效果）。
//
// 用途：选中实体的依赖链连线 + 选中光晕。密集星核里普通深度的线会被前景
// 实体遮挡，链路看不全；叠加层保证"选中的结构"永远可见。
// Queue = Overlay 保证在所有不透明/透明几何之后绘制；ZWrite Off 不污染
// 深度缓冲。颜色全部走顶点色（与主连线网格同思路：一次 DrawCall 画完）。
// ============================================================================
Shader "Galaxy/OverlayLine"
{
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Overlay"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}
