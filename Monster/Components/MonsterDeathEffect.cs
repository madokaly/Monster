using Framework.Core;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 怪物死亡爆炸特效脚本（挂载于死亡特效 prefab）。
    /// 由 MonsterBattleViewModule 在各端本地播放（对象池管理）。
    /// </summary>
    public class MonsterDeathEffect : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("爆炸粒子")]
        private ParticleSystem _explosionEffect;

        [SerializeField]
        [Tooltip("物理爆炸（Rayfire），仅权威端触发避免多端重复模拟")]
        private RayFire.RayfireBomb _rayfireBomb;

        [SerializeField]
        [Tooltip("爆炸音效事件")]
        private string _explosionSound = "event:/game/other/monster_explosion";

        /// <summary>
        /// 播放死亡爆炸。
        /// </summary>
        /// <param name="bomb">是否触发物理爆炸（仅权威端传 true）</param>
        public void Play(bool bomb)
        {
            if (bomb && _rayfireBomb != null)
            {
                _rayfireBomb.Explode(0);
            }

            if (_explosionEffect != null)
            {
                _explosionEffect.Clear();
                _explosionEffect.Play();
            }

            if (!string.IsNullOrEmpty(_explosionSound))
            {
                AudioMgr.Play(_explosionSound, transform);
            }
        }
    }
}
