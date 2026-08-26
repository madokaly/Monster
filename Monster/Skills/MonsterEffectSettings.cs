using System;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 共享表现特效设置（各步骤具名槽复用：AreaHit.HitEffect / Slam.LandingEffect / EffectStep.Effect，§16.4）。
    /// 语义：槽位所在步骤的语义时刻各端本地实例化一个纯表现 prefab（不结算伤害），到时自毁；Prefab 为空 = 不播。
    /// 挂点解析：AttachPoint 优先，其次 FallbackTransform，都空由所属步骤自行兜底（如怪物自身 Transform）。
    /// </summary>
    [Serializable]
    public class MonsterEffectSettings
    {
        [Tooltip("表现特效 prefab（纯表现，各端本地实例化；不应含伤害逻辑）")]
        public GameObject Prefab;

        [Tooltip("特效挂点（空则挂 FallbackTransform）")]
        public Transform AttachPoint;

        [Tooltip("特效挂点兜底（AttachPoint 为空时使用，如怪物自身 Transform）")]
        public Transform FallbackTransform;

        [Tooltip("特效相对挂点的本地位置偏移")]
        public Vector3 Offset = Vector3.zero;

        [Tooltip("特效相对挂点的本地旋转（欧拉角）")]
        public Vector3 Rotation = Vector3.zero;

        [Tooltip("特效缩放（默认 1）")]
        public Vector3 Scale = Vector3.one;

        [Tooltip("特效存活时长（秒，<=0 兜底 3s）")]
        public float Lifetime = 3f;
    }
}
