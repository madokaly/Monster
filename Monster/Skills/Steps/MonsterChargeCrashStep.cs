using System;
using System.Collections.Generic;
using Framework;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterChargeCrashStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("蓄力与冲撞驱动的怪物本体 Transform")]
        public Transform Body;

        [Tooltip("技能期间临时切为 trigger 的主碰撞体（退出 / 打断恢复原状态）")]
        public Collider[] CollidersToTrigger;

        [Header("Charge")]
        [Tooltip("蓄力 / 预警时长（秒）；0 = 跳过预警阶段并直接冲撞")]
        public float WarningDuration = 0.8f;

        [Tooltip("直线冲撞时长（秒）")]
        public float ChargeDuration = 0.45f;

        [Tooltip("直线冲撞距离（米；不追踪目标，不因命中提前停止）")]
        public float ChargeDistance = 10f;

        [Header("Stage Effects")]
        [Tooltip("蓄力 / 预警特效（挂载怪物本体，WarningDuration 后销毁；预警时长为 0 时不播放）")]
        public MonsterEffectSettings WarningEffect;

        [Tooltip("冲撞特效（挂载怪物本体，冲撞阶段显示）")]
        public MonsterEffectSettings ChargeEffect;

        [Header("Contact Damage")]
        [Tooltip("撞伤结算层")]
        public LayerMask DamageLayer;

        [Tooltip("撞伤伤害（0 = 不撞伤；冲撞阶段每目标只结算一次）")]
        public int ContactDamage = 80;

        [Tooltip("撞伤身体胶囊半径；冲撞时该竖直胶囊从起点扫掠到当前本体位置")]
        public float ContactHitRadius = 2.5f;

        [Tooltip("撞伤身体胶囊总高度（= 竖直轴长度 + 2 × 半径）")]
        public float ContactHitHeight = 7f;

        [Tooltip("撞伤身体胶囊中心相对本体位置的高度")]
        public float ContactHitCenterHeight = 3.5f;

        [Tooltip("撞伤受击方向反作用力")]
        public float HitForce = 5f;

        [Tooltip("撞伤高度过滤（<=0 不过滤）")]
        public float MaxAttackHeight = 3f;

        public override float Duration => Mathf.Max(0f, WarningDuration) + Mathf.Max(0f, ChargeDuration);
    }

    /// <summary>
    /// 定向冲撞步骤：进入时按目标锁定平面方向，蓄力期原地显示预警且不调整瞄准；
    /// 蓄力结束后沿锁定方向固定距离直线冲撞，冲撞特效挂载本体，撞伤由各端按本端
    /// 竖直身体胶囊从冲撞起点到当前位置的扫掠体积结算。
    /// </summary>
    public class MonsterChargeCrashStep : MonsterSkillStep
    {
        private readonly MonsterChargeCrashStepConfig _chargeCrashConfig;
        private readonly HashSet<EntityId> _contactHitTargets = new();

        private Vector3 _chargeStartPosition;
        private Vector3 _chargeDirection = Vector3.forward;
        private bool[] _colliderTriggerStates = Array.Empty<bool>();
        private bool _chargeStarted;
        private GameObject _warningEffect;
        private GameObject _chargeEffect;

        public MonsterChargeCrashStep(MonsterModel model, MonsterChargeCrashStepConfig crashConfig)
            : base(model, crashConfig)
        {
            _chargeCrashConfig = crashConfig;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_chargeCrashConfig is null) return;

            _contactHitTargets.Clear();
            float stepElapsed = GetStepElapsed(elapsed);
            _chargeStarted = stepElapsed >= MonsterChargeTiming.GetWarningEndTime(_chargeCrashConfig);
            _chargeDirection = ResolveLockedDirection();
            _chargeStartPosition = ResolveBodyPosition(
                _chargeDirection,
                MonsterChargeTiming.GetChargeProgress(_chargeCrashConfig, stepElapsed)
            );

            if (HasStateAuthority)
            {
                _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
                _model.PushBodyRotation(Quaternion.LookRotation(_chargeDirection, Vector3.up));
            }

            if (_chargeStarted)
            {
                SpawnChargeEffect();
            }
            else
            {
                _warningEffect = MonsterSkillPresentation.PlayEffect(
                    _chargeCrashConfig.WarningEffect,
                    MonsterChargeTiming.GetWarningEndTime(_chargeCrashConfig),
                    _chargeCrashConfig.Body
                );
            }

            CaptureInitialState();
            SetCollidersTrigger(true);
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_chargeCrashConfig is null) return;

            float stepElapsed = GetStepElapsed(elapsed);
            if (!_chargeStarted && stepElapsed >= MonsterChargeTiming.GetWarningEndTime(_chargeCrashConfig))
            {
                StartCharge();
            }

            if (HasStateAuthority
                && _chargeStarted
                && !IsContentEnded
                && _chargeCrashConfig.ChargeDuration > 0f)
            {
                MoveBodyAlongLockedDirection(stepElapsed);
            }

            if (_chargeStarted && !IsContentEnded && _chargeCrashConfig.ContactDamage > 0)
            {
                SettleContactDamage();
            }
        }

        protected override void OnStepExit()
        {
            ClearEffects();
            RestoreInitialState();
            _contactHitTargets.Clear();
            _chargeStarted = false;
        }

        protected override void OnStepDispose()
        {
            ClearEffects();
            RestoreInitialState();
            _contactHitTargets.Clear();
            _chargeStarted = false;
        }

        #region Private Methods

        private float GetStepElapsed(float elapsed)
        {
            return elapsed - _config.StartOffset;
        }

        private void CaptureInitialState()
        {
            var colliders = _chargeCrashConfig.CollidersToTrigger;
            _colliderTriggerStates = new bool[colliders?.Length ?? 0];
            for (int i = 0; i < _colliderTriggerStates.Length; i++)
            {
                _colliderTriggerStates[i] = colliders[i] != null && colliders[i].isTrigger;
            }
        }

        private void RestoreInitialState()
        {
            var colliders = _chargeCrashConfig.CollidersToTrigger;
            if (colliders == null) return;

            for (int i = 0; i < colliders.Length && i < _colliderTriggerStates.Length; i++)
            {
                if (colliders[i] != null) colliders[i].isTrigger = _colliderTriggerStates[i];
            }
        }

        private void SetCollidersTrigger(bool isTrigger)
        {
            if (_chargeCrashConfig.CollidersToTrigger == null) return;

            for (int i = 0; i < _chargeCrashConfig.CollidersToTrigger.Length; i++)
            {
                if (_chargeCrashConfig.CollidersToTrigger[i] != null)
                {
                    _chargeCrashConfig.CollidersToTrigger[i].isTrigger = isTrigger;
                }
            }
        }

        private Vector3 ResolveBodyPosition(Vector3 direction, float initialProgress)
        {
            if (_chargeCrashConfig.Body == null) return Vector3.zero;

            return _chargeCrashConfig.Body.position
                   - direction * Mathf.Max(0f, _chargeCrashConfig.ChargeDistance) * initialProgress;
        }

        /// <summary>
        /// 权威端在蓄力前最后一次读取目标并锁定平面方向；代理端使用 Aim 后同步到本端的
        /// 本体朝向。蓄力与冲撞阶段都不再读取目标，满足预警方向与实际冲撞方向一致。
        /// </summary>
        private Vector3 ResolveLockedDirection()
        {
            var body = _chargeCrashConfig.Body;
            Vector3 fallback = body != null ? body.forward : Vector3.forward;
            fallback.y = 0f;
            if (fallback.sqrMagnitude <= 0.0001f) fallback = Vector3.forward;

            if (!HasStateAuthority || body == null)
            {
                return fallback.normalized;
            }

            if (!TryGetTargetPosition(_model.CastTargetId, out var targetPosition)
                || !MonsterChargeTiming.TryResolvePlanarDirection(
                    body.position,
                    targetPosition,
                    out Vector3 direction))
            {
                return fallback.normalized;
            }

            return direction;
        }

        private void StartCharge()
        {
            DestroyWarningEffect();
            _chargeStarted = true;
            SpawnChargeEffect();
        }

        private void MoveBodyAlongLockedDirection(float stepElapsed)
        {
            var body = _chargeCrashConfig.Body;
            if (body == null) return;

            float progress = MonsterChargeTiming.GetChargeProgress(_chargeCrashConfig, stepElapsed);
            body.position = MonsterChargeTiming.EvaluatePosition(
                _chargeStartPosition,
                _chargeDirection,
                _chargeCrashConfig.ChargeDistance,
                progress
            );
        }

        private void SpawnChargeEffect()
        {
            float lifetime = _chargeCrashConfig.ChargeDuration + Mathf.Max(0f, _chargeCrashConfig.EndOffset);
            _chargeEffect = MonsterSkillPresentation.PlayEffect(
                _chargeCrashConfig.ChargeEffect,
                lifetime,
                _chargeCrashConfig.Body
            );
        }

        private void SettleContactDamage()
        {
            var body = _chargeCrashConfig.Body;
            if (body == null || _chargeCrashConfig.ContactHitRadius <= 0f) return;

            MonsterChargeTiming.GetContactCapsuleAxisPoints(
                _chargeCrashConfig,
                _chargeStartPosition,
                out Vector3 startLowerPoint,
                out Vector3 startUpperPoint
            );
            MonsterSkillDamage.SettleSweptCapsule(
                _model.Id,
                startLowerPoint,
                startUpperPoint,
                body.position - _chargeStartPosition,
                _chargeCrashConfig.ContactHitRadius,
                _chargeCrashConfig.DamageLayer,
                _chargeCrashConfig.MaxAttackHeight,
                _chargeCrashConfig.ContactDamage,
                _chargeCrashConfig.HitForce,
                _contactHitTargets,
                _chargeDirection
            );
        }

        private void DestroyWarningEffect()
        {
            if (_warningEffect != null)
            {
                UnityEngine.Object.Destroy(_warningEffect);
            }
            _warningEffect = null;
        }

        private void ClearEffects()
        {
            DestroyWarningEffect();

            if (_chargeEffect != null)
            {
                UnityEngine.Object.Destroy(_chargeEffect);
            }
            _chargeEffect = null;
        }

        #endregion
    }
}
