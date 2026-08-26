using Framework.Core;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 怪物渲染组件（被动刷新，仅由 Module 调用）：
    /// 动画驱动、血条、身体显隐、眩晕特效。
    /// </summary>
    public class MonsterRendering : MonoBehaviour
    {
        private static readonly int AnimIdState = Animator.StringToHash("State");

        [Header("Animation")]
        [SerializeField]
        [Tooltip("动画控制器")]
        private Animator _animator;

        [Header("Battle UI")]
        [SerializeField]
        [Tooltip("血条（Michsky ProgressBar）")]
        private Michsky.UI.Reach.ProgressBar _healthBar;

        [Header("Body")]
        [SerializeField]
        [Tooltip("死亡爆炸前需要隐藏的身体网格")]
        private SkinnedMeshRenderer[] _bodyRenderers;

        [Header("Stun Vfx")]
        [SerializeField]
        [Tooltip("眩晕特效 prefab")]
        private GameObject _dizzyVfxPrefab;

        [SerializeField]
        [Tooltip("眩晕特效挂点（通常为头部附近骨骼）")]
        private Transform _dizzyAttachBone;

        [SerializeField]
        [Tooltip("眩晕特效相对挂点的本地位置")]
        private Vector3 _dizzyVfxLocalPosition = new(-1f, 0f, 0f);

        [SerializeField]
        [Tooltip("眩晕特效相对挂点的本地旋转")]
        private Vector3 _dizzyVfxLocalEulerAngles = new(-90f, 90f, 0f);

        private GameObject _dizzyVfxInstance;

        private void Awake()
        {
            if (_animator == null) _animator = GetComponent<Animator>();
        }

        /// <summary>
        /// 播放动画（Animator State 参数）
        /// </summary>
        public void SetAnimId(int animId)
        {
            if (_animator == null)
            {
                Logging.Error("[MonsterRendering] SetAnimId: _animator 为 null，请检查 Inspector 引用。");
                return;
            }

            _animator.SetInteger(AnimIdState, animId);
        }

        /// <summary>
        /// 更新血条
        /// </summary>
        public void UpdateHp(int hp, int maxHp)
        {
            if (_healthBar == null)
            {
                Logging.Error("[MonsterRendering] UpdateHp: _healthBar 为 null，请检查 Inspector 引用。");
                return;
            }

            _healthBar.maxValue = maxHp;
            _healthBar.SetValue(hp);
        }

        /// <summary>
        /// 开关身体渲染（死亡爆炸前隐藏遮丑）
        /// </summary>
        public void SetBodyActive(bool active)
        {
            if (_bodyRenderers == null) return;

            for (int i = 0; i < _bodyRenderers.Length; i++)
            {
                var renderer = _bodyRenderers[i];
                if (renderer == null) continue;
                if (renderer.enabled != active) renderer.enabled = active;
            }
        }

        /// <summary>
        /// 显示眩晕特效（幂等）
        /// </summary>
        public void ShowDizzyVfx()
        {
            if (_dizzyVfxPrefab != null && _dizzyAttachBone != null && _dizzyVfxInstance == null)
            {
                _dizzyVfxInstance = Instantiate(_dizzyVfxPrefab, _dizzyAttachBone);
                _dizzyVfxInstance.transform.localPosition = _dizzyVfxLocalPosition;
                _dizzyVfxInstance.transform.localRotation = Quaternion.Euler(_dizzyVfxLocalEulerAngles);
            }
        }

        /// <summary>
        /// 隐藏眩晕特效（幂等）
        /// </summary>
        public void HideDizzyVfx()
        {
            if (_dizzyVfxInstance == null) return;

            Destroy(_dizzyVfxInstance);
            _dizzyVfxInstance = null;
        }
    }
}
