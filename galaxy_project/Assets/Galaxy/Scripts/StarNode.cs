// 挂在每颗星体上的数据锚点：从 GameObject 反查图数据。
// 将来的 raycast 悬停/点击高亮、HUD 详情面板都从这里取数据。
using UnityEngine;

namespace Galaxy
{
    public class StarNode : MonoBehaviour
    {
        [System.NonSerialized] public int NodeIndex;      // nodes 下标，== galaxy.json 的 id
        [System.NonSerialized] public GalaxyNode Data;    // 引用 DTO（同一份数据的只读视图）
    }
}
