using Framework.Core;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 怪物受击 IK 组件（被动刷新，仅由 Module 调用）。
    /// 包装 RootMotion FinalIK 的 HitReaction，权威端本地播放。
    /// </summary>
    public class MonsterHitReaction : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("FinalIK 受击反应组件")]
        private RootMotion.FinalIK.HitReaction _hitReaction;
        [SerializeField]
        [Tooltip("受击碰撞体")]
        private Collider _hitCollider;

        /// <summary>
        /// 触发受击 IK
        /// </summary>
        public void Hit(Vector3 force, Vector3 pos)
        {
            if (_hitReaction == null) return;

            _hitReaction.Hit(_hitCollider, force, pos);
        }
    }
}
