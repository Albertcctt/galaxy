// ============================================================================
// 宇宙球着色器：菲涅尔边缘光环 —— 能量护罩/气泡的标准画法。
//
// 球面正面近乎全透明（_BaseAlpha ≈ 0.03，不遮内部星团），只在"视线擦过
// 球面"的轮廓处亮起一圈光环（菲涅尔项 pow(1 - dot(N, V), _RimPower)）：
// 法线与视线夹角越大（越靠近球体边缘），亮度越高。
//
// 渲染约定：
//   - Queue 由材质在 C# 侧设为 Transparent-30：先于内部实体（Transparent）
//     绘制，内容永远叠在球面之上，规避大透明球同队列排序跳变的闪烁；
//   - ZWrite Off + ZTest LEqual：不污染深度，被不透明物正常遮挡；
//   - Cull Back：只画正面半球，菲涅尔峰恰好在轮廓处成环；
//   - 颜色保持 LDR（≤1）：不给 HDR 自发光，避免 URP Bloom 把光环糊成白团。
// ============================================================================
Shader "Galaxy/Bubble"
{
    Properties
    {
        _Color     ("Tint", Color) = (0.5, 0.8, 1.0, 1)
        _BaseAlpha ("Base Alpha", Range(0, 0.5)) = 0.03
        _RimAlpha  ("Rim Alpha", Range(0, 1)) = 0.5
        _RimPower  ("Rim Power", Range(0.5, 8)) = 3
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
            Cull Back
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            float _BaseAlpha;
            float _RimAlpha;
            float _RimPower;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 pos         : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 worldPos    : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.worldPos);
                float fresnel = pow(1.0 - saturate(dot(n, viewDir)), _RimPower);
                float alpha = saturate(_BaseAlpha + fresnel * _RimAlpha);
                // 光环处极轻微向白色提亮（0.15：仅避免纯色相发闷；外壳整体保持暗淡）
                float3 col = lerp(_Color.rgb, float3(1, 1, 1), fresnel * 0.15);
                return fixed4(col, alpha * _Color.a);
            }
            ENDCG
        }
    }
}
