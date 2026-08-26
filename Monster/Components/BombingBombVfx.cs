using Framework.Core;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 定点轰炸导弹纯表现组件（各端本地）：高空下落 → 触地爆炸（粒子 + 音效）→ 到时自毁。
    /// 判定归 Bombing 步骤权威端结算，本组件不结算伤害（§1.5 事实与表现分流）。
    /// </summary>
    public class BombingBombVfx : MonoBehaviour
    {
        [Header("Visuals")]
        [SerializeField]
        [Tooltip("下落导弹视觉（触地时隐藏）")]
        private GameObject _missileObject;

        [SerializeField]
        [Tooltip("爆炸视觉（初始隐藏，触地时激活）")]
        private GameObject _explosionObject;

        [Header("Fall")]
        [SerializeField]
        [Tooltip("下落时长（秒）")]
        private float _fallDuration = 0.5f;

        [SerializeField]
        [Tooltip("初始下落高度（米，相对触地点）")]
        private float _spawnHeight = 40f;

        [Header("Explosion")]
        [SerializeField]
        [Tooltip("爆炸音效（空 = 不播）")]
        private string _explosionSound = "event:/game/creature/boss1_skill4fire";

        [SerializeField]
        [Tooltip("爆炸后自毁时长（秒）")]
        private float _explosionLifetime = 3f;

        private Vector3 _targetPosition;
        private float _elapsed;
        private bool _exploded;

        private void Awake()
        {
            _targetPosition = transform.position;

            if (_explosionObject != null) _explosionObject.SetActive(false);
            if (_missileObject != null) _missileObject.SetActive(true);
        }

        private void Update()
        {
            if (_exploded) return;

            _elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_elapsed / Mathf.Max(0.01f, _fallDuration));
            transform.position = Vector3.Lerp(_targetPosition + Vector3.up * _spawnHeight, _targetPosition, t);

            if (t >= 1f)
            {
                Explode();
            }
        }

        private void Explode()
        {
            _exploded = true;
            transform.position = _targetPosition;

            if (_missileObject != null) _missileObject.SetActive(false);
            if (_explosionObject != null) _explosionObject.SetActive(true);

            if (!string.IsNullOrEmpty(_explosionSound))
            {
                AudioMgr.Play(_explosionSound, transform);
            }

            Destroy(gameObject, Mathf.Max(0.1f, _explosionLifetime));
        }
    }
}
