// ============================================================================
// 吞噬光束着色器：流光虚线 —— 与 OverlayLine 同族的"流动短划"画法，
// 但走正常深度（ZTest LEqual）：光束应被前景实体正常遮挡（那是场景里的
// "被吞的光"，不是 UI 高亮，不该照穿一切）。
//
// uv 约定（由 BlackHole 写入）：uv.x = 距实体端的距离（0 在实体、长度在黑洞
// 端）→ 短划随 _Time 向 +uv.x 流动 = 朝黑洞方向被吞；uv.y = 每条束的相位偏移。
// 颜色走顶点色（实体端低调、黑洞端白热 HDR，吃 Bloom）。
// ============================================================================
Shader "Galaxy/StreamLine"
{
    Properties
    {
        _DashLength ("Dash Length (world units per cycle)", Float) = 1.1
        _FlowSpeed  ("Flow Speed (cycles per second)", Float) = 1.3
        _DashDuty   ("Dash Duty (0-1)", Range(0.05, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            ZTest LEqual
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _DashLength;
            float _FlowSpeed;
            float _DashDuty;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv    : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 c = i.color;
                float phase = frac(i.uv.x / max(_DashLength, 0.01) - _Time.y * _FlowSpeed + i.uv.y);
                c.a *= (phase < _DashDuty) ? 1.0 : 0.0;
                return c;
            }
            ENDCG
        }
    }
}
