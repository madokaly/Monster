using System;
using System.Collections.Generic;
using Framework;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterEntranceDescendStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("判定与特效锚点（怪物网络根，固定在最终落点）")]
        public Transform SelfTransform;

        [Tooltip("程序控制下落的视觉根（只改世界 Y，不改网络根）")]
        public Transform VisualRoot;

        [Tooltip("下落期间继续保持 trigger 的主碰撞体（落地恢复原状态）")]
        public Collider[] CollidersToTrigger;

        [Header("Descend")]
        [Tooltip("下落起始世界高度（米）")]
        public float DescendHeight = 18f;

        [Tooltip("下落时长（秒）")]
        public float DescendDuration = 1.7f;

        [Tooltip("落地后的原地收尾时长（秒）")]
        public float LandingRecoveryDuration = 0.5f;

        [Header("Landing Damage")]
        [Tooltip("落地结算层")]
        public LayerMask DamageLayer;

        [Tooltip("落地伤害（0 = 不结算）")]
        public int Damage = 80;

        [Tooltip("落地范围半径（米）")]
        public float Radius = 17.5f;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 0f;

        [Tooltip("高度过滤（<=0 不过滤）")]
        public float MaxAttackHeight = 3f;

        public override float Duration =>
            Mathf.Max(0f, DescendDuration) + Mathf.Max(0f, LandingRecoveryDuration);
    }

    /// <summary>
    /// 登场下落步骤：各端本地移动视觉根（ease-in 下落），网络根与判定中心固定在最终落点。
    /// 落地瞬间恢复显隐 / 碰撞 / 无敌并按受击方本地结算管线释放范围伤害；打断只保守恢复，不补结算。
    /// </summary>
    public class MonsterEntranceDescendStep : MonsterSkillStep
    {
        private readonly MonsterEntranceDescendStepConfig _descendConfig;
        private readonly HashSet<EntityId> _landingHitTargets = new();

        private Vector3 _visualRootOriginalPosition;
        private bool _visualRootWasActive;
        private bool[] _colliderTriggerStates = Array.Empty<bool>();
        private bool _landed;

        public MonsterEntranceDescendStep(MonsterModel model, MonsterEntranceDescendStepConfig config)
            : base(model, config)
        {
            _descendConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_descendConfig is null) return;

            _landed = false;
            _landingHitTargets.Clear();
            CaptureInitialState();

            if (HasStateAuthority)
            {
                _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
                _model.SetIsInvulnerable(true);
            }

            SetVisualVisible(true);
            SetCollidersTrigger(true);
            UpdateVisualPosition(GetStepElapsed(elapsed));
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_descendConfig is null || _landed) return;

            if (GetStepElapsed(elapsed) >= Mathf.Max(0f, _descendConfig.DescendDuration))
            {
                Land();
            }
        }

        protected override void OnStepVisualUpdate(float elapsed)
        {
            if (_descendConfig is null || _landed) return;

            UpdateVisualPosition(GetStepElapsed(elapsed));
        }

        protected override void OnStepExit()
        {
            RestoreInitialState();
        }

        protected override void OnStepDispose()
        {
            RestoreInitialState();
        }

        protected override void OnStepAuthorityChanged()
        {
            // 权威易主时链运行器会重置本步骤；本端成为权威后保守关闭无敌，避免永久无敌。
            if (HasStateAuthority && _model.IsInvulnerable)
            {
                _model.SetIsInvulnerable(false);
            }
        }

        #region Private Methods

        private float GetStepElapsed(float elapsed)
        {
            return elapsed - _config.StartOffset;
        }

        private void CaptureInitialState()
        {
            _visualRootWasActive = _descendConfig.VisualRoot != null && _descendConfig.VisualRoot.gameObject.activeSelf;
            _visualRootOriginalPosition = _descendConfig.VisualRoot != null
                ? _descendConfig.VisualRoot.position
                : Vector3.zero;

            var colliders = _descendConfig.CollidersToTrigger;
            _colliderTriggerStates = new bool[colliders?.Length ?? 0];
            for (int i = 0; i < _colliderTriggerStates.Length; i++)
            {
                _colliderTriggerStates[i] = colliders[i] != null && colliders[i].isTrigger;
            }
        }

        private void UpdateVisualPosition(float stepElapsed)
        {
            if (_descendConfig.VisualRoot == null) return;

            _descendConfig.VisualRoot.position = MonsterEntranceTiming.EvaluateDescendPosition(
                _visualRootOriginalPosition,
                _descendConfig,
                stepElapsed
            );
        }

        private void Land()
        {
            _landed = true;
            RestoreVisualAndCollision();

            if (_descendConfig.Damage > 0
                && _descendConfig.Radius > 0f
                && _descendConfig.SelfTransform != null)
            {
                SettleSphereDamage(
                    _descendConfig.SelfTransform.position,
                    _descendConfig.Radius,
                    _descendConfig.DamageLayer,
                    _descendConfig.MaxAttackHeight,
                    _descendConfig.Damage,
                    _descendConfig.HitForce,
                    _landingHitTargets,
                    Vector3.down
                );
            }
        }

        private void RestoreInitialState()
        {
            if (_descendConfig is null) return;

            RestoreVisualAndCollision();
            _landingHitTargets.Clear();
        }

        private void RestoreVisualAndCollision()
        {
            if (_descendConfig.VisualRoot != null)
            {
                _descendConfig.VisualRoot.position = _visualRootOriginalPosition;
                _descendConfig.VisualRoot.gameObject.SetActive(_visualRootWasActive);
            }

            var colliders = _descendConfig.CollidersToTrigger;
            if (colliders != null)
            {
                for (int i = 0; i < colliders.Length && i < _colliderTriggerStates.Length; i++)
                {
                    if (colliders[i] != null) colliders[i].isTrigger = _colliderTriggerStates[i];
                }
            }

            if (HasStateAuthority && _model.IsInvulnerable)
            {
                _model.SetIsInvulnerable(false);
            }
        }

        private void SetVisualVisible(bool visible)
        {
            if (_descendConfig.VisualRoot == null) return;
            _descendConfig.VisualRoot.gameObject.SetActive(visible);
        }

        private void SetCollidersTrigger(bool isTrigger)
        {
            if (_descendConfig.CollidersToTrigger == null) return;

            for (int i = 0; i < _descendConfig.CollidersToTrigger.Length; i++)
            {
                if (_descendConfig.CollidersToTrigger[i] != null)
                {
                    _descendConfig.CollidersToTrigger[i].isTrigger = isTrigger;
                }
            }
        }

        #endregion
    }
}