using System;
using Framework.Core;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterEntranceConcealStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("最终落点网络根（warning 与下落判定的水平中心）")]
        public Transform SelfTransform;

        [Tooltip("登场前隐藏的视觉根（含模型 / UI；各端本地显隐）")]
        public Transform VisualRoot;

        [Tooltip("隐藏期间切为 trigger 的主碰撞体（登场恢复原状态）")]
        public Collider[] CollidersToTrigger;

        [Tooltip("落点预警特效（世界空间贴地生成；存活时长跟随 ConcealDuration）")]
        public MonsterEffectSettings WarningEffect;

        [Header("Conceal")]
        [Tooltip("预警 / 隐藏时长（秒）")]
        public float ConcealDuration = 1.5f;

        [Tooltip("隐藏阶段写入的本体绝对朝向（欧拉角；权威端经 Model.PushBodyRotation 落地）")]
        public Vector3 BodyRotationEuler = Vector3.zero;

        public override float Duration => Mathf.Max(0f, ConcealDuration);
    }

    /// <summary>
    /// 登场隐藏步骤：网络根固定不动，视觉与碰撞本地退出玩家交互；落点 warning 各端本地贴地生成。
    /// 无敌事实由权威端写入 Model，其他端经状态同步看到一致结果。
    /// </summary>
    public class MonsterEntranceConcealStep : MonsterSkillStep
    {
        private const float WARNING_GROUND_RAY_UP_OFFSET = 5f;
        private const float WARNING_GROUND_RAY_MAX_DISTANCE = 100f;

        private readonly MonsterEntranceConcealStepConfig _concealConfig;

        private bool _visualRootWasActive;
        private bool[] _colliderTriggerStates = Array.Empty<bool>();
        private GameObject _warningEffect;

        public MonsterEntranceConcealStep(MonsterModel model, MonsterEntranceConcealStepConfig config)
            : base(model, config)
        {
            _concealConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_concealConfig is null) return;

            if (HasStateAuthority)
            {
                _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
                _model.PushBodyRotation(Quaternion.Euler(_concealConfig.BodyRotationEuler));
                _model.SetIsInvulnerable(true);
            }

            PlayWarningEffect(GetStepElapsed(elapsed));
            CaptureInitialState();
            SetVisualVisible(false);
            SetCollidersTrigger(true);
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

        private void PlayWarningEffect(float stepElapsed)
        {
            var effect = _concealConfig.WarningEffect;
            var selfTransform = _concealConfig.SelfTransform;
            float remainingDuration = Mathf.Max(0f, _concealConfig.ConcealDuration - stepElapsed);
            if (effect?.Prefab == null || selfTransform == null || remainingDuration <= 0f) return;

            DestroyWarningEffect();

            Quaternion rotation = MonsterEntranceTiming.GetWarningRotation(selfTransform, effect);
            Vector3 position = ResolveWarningPosition(selfTransform.position)
                               + MonsterEntranceTiming.GetWarningOffset(selfTransform, effect);

            _warningEffect = UnityEngine.Object.Instantiate(effect.Prefab, position, rotation);
            _warningEffect.transform.localScale = MonsterEntranceTiming.GetWarningScale(selfTransform, effect);
            UnityEngine.Object.Destroy(_warningEffect, Mathf.Max(0.1f, remainingDuration));
        }

        private float GetStepElapsed(float elapsed)
        {
            return elapsed - _config.StartOffset;
        }

        private Vector3 ResolveWarningPosition(Vector3 fallbackPosition)
        {
            Vector3 rayOrigin = fallbackPosition + Vector3.up * WARNING_GROUND_RAY_UP_OFFSET;
            return Physics.Raycast(
                       rayOrigin,
                       Vector3.down,
                       out RaycastHit hit,
                       WARNING_GROUND_RAY_MAX_DISTANCE,
                       LayerMask.GetMask(Consts.GroundLayer),
                       QueryTriggerInteraction.Ignore
                   )
                ? MonsterEntranceTiming.GetWarningSurfacePosition(hit.point)
                : fallbackPosition;
        }

        private void DestroyWarningEffect()
        {
            if (_warningEffect == null) return;

            UnityEngine.Object.Destroy(_warningEffect);
            _warningEffect = null;
        }

        private void CaptureInitialState()
        {
            _visualRootWasActive = _concealConfig.VisualRoot != null && _concealConfig.VisualRoot.gameObject.activeSelf;

            var colliders = _concealConfig.CollidersToTrigger;
            _colliderTriggerStates = new bool[colliders?.Length ?? 0];
            for (int i = 0; i < _colliderTriggerStates.Length; i++)
            {
                _colliderTriggerStates[i] = colliders[i] != null && colliders[i].isTrigger;
            }
        }

        private void RestoreInitialState()
        {
            if (_concealConfig is null) return;

            DestroyWarningEffect();
            if (_concealConfig.VisualRoot != null)
            {
                _concealConfig.VisualRoot.gameObject.SetActive(_visualRootWasActive);
            }

            var colliders = _concealConfig.CollidersToTrigger;
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
            if (_concealConfig.VisualRoot == null) return;
            _concealConfig.VisualRoot.gameObject.SetActive(visible);
        }

        private void SetCollidersTrigger(bool isTrigger)
        {
            if (_concealConfig.CollidersToTrigger == null) return;

            for (int i = 0; i < _concealConfig.CollidersToTrigger.Length; i++)
            {
                if (_concealConfig.CollidersToTrigger[i] != null)
                {
                    _concealConfig.CollidersToTrigger[i].isTrigger = isTrigger;
                }
            }
        }

        #endregion
    }
}