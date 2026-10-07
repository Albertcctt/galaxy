// ============================================================================
// 宇宙名碑：每帧面向相机（billboard）。
//
// 球壳外的文件夹名如果固定朝向，轨道旋转到背面就变成镜像/侧视不可读；
// 每帧把碑面朝向相机（相当于把文字的局部坐标架对齐相机坐标架），
// 任何观察角度下都是正对的可读文字。C++ 类比：这就是渲染循环里的一次
// 视图矩阵对齐 —— 每帧把模型的旋转直接赋值成相机的旋转。
// ============================================================================
using UnityEngine;

namespace Galaxy
{
    public class UniverseLabel : MonoBehaviour
    {
        private void LateUpdate()
        {
            Camera cam = Camera.main;
            if (cam != null) transform.rotation = cam.transform.rotation;
        }
    }
}
