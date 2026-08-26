using System;
using System.Collections.Generic;
using Framework;
using Framework.Core;
using Game.Components;
using Game.DTOs;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterBeamStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("光束发射挂点（如嘴部骨骼；特效亦挂此点）")]
        public Transform MouthTransform;

        [Tooltip("发射挂点兜底（MouthTransform 为空时使用，如怪物自身 Transform）")]
        public Transform FallbackTransform;

        [Header("Beam")]
        [Tooltip("充能时长（秒，步骤起点起算；充能期只播充能特效不伤害）")]
        public float ChargeDuration = 1.8f;

        [Tooltip("吐息时长（秒；充能结束后持续伤害与追踪）")]
        public float BeamDuration = 7.2f;

        [Tooltip("光束半径（米，SphereCast 判定）")]
        public float BeamRadius = 0.3f;

        [Tooltip("光束最大长度（米）")]
        public float BeamMaxDistance = 30f;

        [Header("Damage")]
        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("单次命中伤害")]
        public int Damage = 1;

        [Tooltip("同一目标重复命中间隔（秒；<=0 时每 tick 均可命中）")]
        public float DamageTickInterval = 0.5f;

        [Tooltip("光束截断层（障碍截断光束长度；0 = 不截断）")]
        public LayerMask ObstacleLayer;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 5f;

        [Header("Effects")]
        [Tooltip("充能特效 prefab（纯表现，各端本地实例化，充能结束时自毁）")]
        public GameObject ChargingEffectPrefab;

        [Tooltip("光束特效 prefab（纯表现，各端本地实例化，吐息开始时生成、窗口结束销毁）")]
        public GameObject BeamEffectPrefab;

        [Tooltip("特效相对挂点的本地位置偏移")]
        public Vector3 EffectOffset = Vector3.zero;

        [Tooltip("特效相对挂点的本地旋转（欧拉角）")]
        public Vector3 EffectRotation = Vector3.zero;

        public override float Duration => ChargeDuration + BeamDuration;
    }

    /// <summary>
    /// 持续追踪光束步骤（§16.4）：充能 → 吐息（持续伤害 + 追踪目标）。
    /// 权威端吐息期每 tick 沿挂点 → 目标方向 SphereCast（障碍截断），同目标按 DamageTickInterval 去重；
    /// 光束方向追踪（不旋转骨骼）；各端本地播充能 / 光束特效。
    /// </summary>
    public class MonsterBeamStep : MonsterSkillStep
    {
        private readonly MonsterBeamStepConfig _beamConfig;

        private readonly RaycastHit[] _castBuffer = new RaycastHit[16];

        /// <summary> 权威端：同目标最近命中时间（tick 间隔去重） </summary>
        private readonly Dictionary<EntityId, float> _lastHitTimes = new();

        /// <summary> 本端：充能特效实例（充能结束时自毁） </summary>
        private GameObject _chargingEffectObj;

        /// <summary> 本端：光束特效实例（窗口结束 / 打断时销毁） </summary>
        private GameObject _beamEffectObj;

        /// <summary> 本端：光束是否已生成 </summary>
        private bool _beamSpawned;

        public MonsterBeamStep(MonsterModel model, MonsterBeamStepConfig config)
            : base(model, config)
        {
            _beamConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_beamConfig is null) return;

            _beamSpawned = false;
            _lastHitTimes.Clear();
            _chargingEffectObj = null;
            _beamEffectObj = null;

            SpawnChargingEffect();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_beamConfig is null) return;

            float stepElapsed = elapsed - _config.StartOffset;

            // 各端本地：充能结束 → 生成光束（方向 = 本端计算的光束方向）
            if (!_beamSpawned && stepElapsed >= _beamConfig.ChargeDuration)
            {
                _beamSpawned = true;
                SpawnBeamEffect();
            }

            if (!HasStateAuthority) return;

            // 权威端：吐息期每 tick 结算
            if (stepElapsed < _beamConfig.ChargeDuration) return;
            if (stepElapsed >= _beamConfig.ChargeDuration + _beamConfig.BeamDuration) return;

            SettleBeam();
        }

        protected override void OnStepExit()
        {
            _lastHitTimes.Clear();

            if (_chargingEffectObj != null)
            {
                UnityEngine.Object.Destroy(_chargingEffectObj);
                _chargingEffectObj = null;
            }

            if (_beamEffectObj != null)
            {
                UnityEngine.Object.Destroy(_beamEffectObj);
                _beamEffectObj = null;
            }
        }

        protected override void OnStepAuthorityChanged()
        {
            _lastHitTimes.Clear();
        }

        #region Private Methods

        /// <summary>
        /// 权威端光束结算：SphereCast（挂点 → 光束方向，障碍截断），同目标 tick 间隔去重。
        /// </summary>
        private void SettleBeam()
        {
            var mouth = ResolveMouth();
            if (mouth == null) return;

            Vector3 origin = mouth.position;
            Vector3 direction = ResolveBeamDirection(mouth);
            if (direction.sqrMagnitude < 0.001f) return;

            float castDistance = _beamConfig.BeamMaxDistance;

            // 障碍截断
            if (_beamConfig.ObstacleLayer != 0)
            {
                if (Physics.Raycast(origin, direction, out var obstacleHit, castDistance, _beamConfig.ObstacleLayer))
                {
                    castDistance = obstacleHit.distance;
                }
            }

            if (castDistance <= 0.01f) return;

            int count = Physics.SphereCastNonAlloc(
                origin,
                _beamConfig.BeamRadius,
                direction,
                _castBuffer,
                castDistance,
                _beamConfig.DamageLayer,
                QueryTriggerInteraction.Ignore
            );

            float now = HasStateAuthority ? Time.time : 0f;
            for (int i = 0; i < count; i++)
            {
                var hit = _castBuffer[i];
                if (hit.collider == null) continue;

                var tag = hit.collider.GetComponentInParent<EntityTag>();
                if (tag == null) continue;

                EntityId targetId = tag.Id;
                if (!targetId.IsValid) continue;

                // 同目标 tick 间隔去重
                if (_beamConfig.DamageTickInterval > 0f
                    && _lastHitTimes.TryGetValue(targetId, out float lastTime)
                    && now - lastTime < _beamConfig.DamageTickInterval)
                {
                    continue;
                }
                _lastHitTimes[targetId] = now;

                var damageData = new DamageData { Damage = _beamConfig.Damage, AttackerId = _model.Id, };

                var hitData = new HitData
                {
                    HitPoint = hit.point, HitDirection = direction, Force = _beamConfig.HitForce,
                };

                Msger.Send(MsgID.ApplyDamage, targetId, damageData);
                Msger.Send(MsgID.ApplyHit, targetId, hitData);
            }
        }

        /// <summary>
        /// 光束方向：目标位置 − 挂点位置（水平 + 垂直全向追踪；无目标时挂点 forward 兜底）。
        /// </summary>
        private Vector3 ResolveBeamDirection(Transform mouth)
        {
            Vector3 direction = mouth.forward;
            if (_model.CastTargetId.IsValid
                && TryGetTargetPosition(_model.CastTargetId, out var targetPos))
            {
                direction = targetPos - mouth.position;
                if (direction.sqrMagnitude > 0.001f)
                {
                    direction = direction.normalized;
                }
                else
                {
                    direction = mouth.forward;
                }
            }

            return direction;
        }

        private Transform ResolveMouth()
        {
            return _beamConfig.MouthTransform != null
                ? _beamConfig.MouthTransform
                : _beamConfig.FallbackTransform;
        }

        private void SpawnChargingEffect()
        {
            if (_beamConfig.ChargingEffectPrefab == null) return;

            var mouth = ResolveMouth();
            if (mouth == null) return;

            _chargingEffectObj = SpawnEffect(_beamConfig.ChargingEffectPrefab, mouth);
            if (_chargingEffectObj != null)
            {
                float chargeTime = Mathf.Max(0.1f, _beamConfig.ChargeDuration);
                UnityEngine.Object.Destroy(_chargingEffectObj, chargeTime);
            }
        }

        private void SpawnBeamEffect()
        {
            if (_beamConfig.BeamEffectPrefab == null)
            {
                Logging.Error("[MonsterBeamStep] SpawnBeamEffect: BeamEffectPrefab 为 null，请检查 Inspector 引用。");
                return;
            }

            var mouth = ResolveMouth();
            if (mouth == null) return;

            _beamEffectObj = SpawnEffect(_beamConfig.BeamEffectPrefab, mouth);
            if (_beamEffectObj != null)
            {
                // 光束朝向 = 本端计算方向（端点跟随由 prefab 内部处理）
                Vector3 direction = ResolveBeamDirection(mouth);
                if (direction.sqrMagnitude > 0.001f)
                {
                    _beamEffectObj.transform.rotation = Quaternion.LookRotation(direction);
                }
            }
        }

        /// <summary>
        /// 各端本地实例化特效（挂点 + 本地偏移 / 旋转），返回实例供步骤管理生命周期。
        /// </summary>
        private GameObject SpawnEffect(GameObject prefab, Transform attach)
        {
            var effectObj = UnityEngine.Object.Instantiate(prefab, attach.position, attach.rotation, attach);
            effectObj.transform.localPosition = _beamConfig.EffectOffset;
            effectObj.transform.localEulerAngles = _beamConfig.EffectRotation;
            return effectObj;
        }

        #endregion
    }
}
