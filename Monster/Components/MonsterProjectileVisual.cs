using Framework.Core;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 怪物弹道视觉组件（本地简易子实体的视觉拼装件）。
    /// 轮询同实体的 MonsterProjectile 本端命中事实，播放本地命中特效 / 音效。
    /// </summary>
    public class MonsterProjectileVisual : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("行为主体（同 GameObject 上的 MonsterProjectile）")]
        private MonsterProjectile _projectile;

        [Header("Hit Feedback")]
        [SerializeField]
        [Tooltip("命中特效 prefab")]
        private GameObject _hitEffectPrefab;

        [SerializeField]
        [Tooltip("命中音效事件")]
        private string _hitSound = "event:/game/weapon/laserlmg_hit";

        [SerializeField]
        [Tooltip("飞行拖尾（命中时隐藏）")]
        private GameObject[] _trailNodes;

        private bool _hitPlayed;

        private void Awake()
        {
            if (_projectile == null) _projectile = GetComponent<MonsterProjectile>();
        }

        private void Update()
        {
            if (_hitPlayed) return;
            if (_projectile == null) return;
            if (!_projectile.IsHit) return;

            _hitPlayed = true;
            PlayHitFeedback();
        }

        private void PlayHitFeedback()
        {
            if (_trailNodes != null)
            {
                for (int i = 0; i < _trailNodes.Length; i++)
                {
                    var node = _trailNodes[i];
                    if (node != null && node.activeSelf) node.SetActive(false);
                }
            }

            if (_hitEffectPrefab != null)
            {
                var fx = Instantiate(_hitEffectPrefab, _projectile.HitPoint, Quaternion.identity);
                Destroy(fx, 2f);
            }

            if (!string.IsNullOrEmpty(_hitSound))
            {
                AudioMgr.Play(_hitSound, transform);
            }
        }
    }
}


