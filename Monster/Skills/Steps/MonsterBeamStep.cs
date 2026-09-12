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
        [Tooltip("本体 Transform（yaw 匀角速转向目标 + 光束水平向来源；空时旋转跳过、水平向兜底发射挂点前向）")]
        public Transform Body;

        [Tooltip("光束发射挂点（如嘴部骨骼）")]
        public Transform MouthTransform;

        [Tooltip("发射挂点兜底（MouthTransform 为空时使用，如怪物自身 Transform）")]
        public Transform FallbackTransform;

        [Header("Beam")]
        [Tooltip("充能时长（秒，步骤起点起算；充能期只播充能特效不伤害）")]
        public float ChargeDuration = 1.8f;

        [Tooltip("吐息时长（秒；充能结束后持续伤害与追踪）")]
        public float BeamDuration = 7.2f;

        [Tooltip("本体 yaw 匀角速转向目标（度/秒；充能起点至内容结束，光束水平跟踪只由身体转动承担）")]
        public float TurnSpeed = 120f;

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
        public float HitForce = 0f;

        [Header("Effects")]
        [Tooltip("充能特效（预挂于怪物 prefab、默认隐藏；进入步骤播放，充能结束隐藏；摆放以 prefab 预挂为准）")]
        public ParticleSystem ChargingEffect;

        [Tooltip("光束线渲染器（预挂于怪物 prefab、默认隐藏；吐息期由步骤每帧驱动端点 = 挂点 → 视觉射线终点）")]
        public LineRenderer BeamRenderer;

        [Tooltip("击中特效（预挂于怪物 prefab、默认隐藏；视觉射线命中时移动到终点播放，未命中隐藏）")]
        public ParticleSystem HitEffect;

        public override float Duration => ChargeDuration + BeamDuration;
    }

    /// <summary>
    /// 持续追踪光束步骤：充能 → 吐息（持续伤害 + 追踪目标）。
    /// 本体 yaw 匀角速转向目标（权威端逐帧驱动、NetworkTransform 同步代理端；Enter 停 Follower 让位），
    /// 光束水平向锁定本体前向——水平跟踪只由身体转动承担，俯仰按目标高度即时瞄准；
    /// 各端吐息期每帧沿挂点 → 本方向本端 SphereCast（障碍截断）结算
    /// （受击方本地结算——判定几何与各端视觉光束同源，同目标按 DamageTickInterval 去重）；
    /// 每帧视觉更新驱动预挂特效（零实例化）：充能粒子播 / 停、
    /// 光束线端点（挂点 → 本地细射线命中 / 最远点，ObstacleLayer | DamageLayer 截断）、击中特效显隐。
    /// </summary>
    public class MonsterBeamStep : MonsterSkillStep
    {
        private readonly MonsterBeamStepConfig _beamConfig;

        private readonly RaycastHit[] _castBuffer = new RaycastHit[16];

        /// <summary> 本端：同目标最近命中时间（tick 间隔去重，各端独立） </summary>
        private readonly Dictionary<EntityId, float> _lastHitTimes = new();

        /// <summary> 本端：吐息窗口是否已开始（充能结束时置位） </summary>
        private bool _beamActive;

        public MonsterBeamStep(MonsterModel model, MonsterBeamStepConfig config)
            : base(model, config)
        {
            _beamConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_beamConfig is null) return;

            _beamActive = false;
            _lastHitTimes.Clear();

            if (_beamConfig.Body == null)
            {
                Logging.Error("[MonsterBeamStep] OnStepEnter: Body 为 null，身体转向与光束水平锁定不可用"
                              + "（水平向兜底发射挂点前向），请检查 Inspector 引用。");
            }

            // 权威端：停 Follower 让位（施放期间由步骤驱动本体旋转，Chase 同款；施法结束 AI 恢复寻路）
            if (HasStateAuthority)
            {
                _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
            }

            ShowEffect(_beamConfig.ChargingEffect);
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_beamConfig is null) return;

            // 本体 yaw 匀角速转向目标（权威端；充能起点至内容结束，无目标不转，姿态经 NetworkTransform 同步）
            if (HasStateAuthority && !IsContentEnded)
            {
                TurnBodyTowardTarget();
            }

            float stepElapsed = elapsed - _config.StartOffset;

            // 各端本地：充能结束 → 进入吐息（隐藏充能特效、显示光束线并立即驱动一次端点）
            if (!_beamActive && stepElapsed >= MonsterBeamTiming.GetBeamStartTime(_beamConfig))
            {
                _beamActive = true;

                HideEffect(_beamConfig.ChargingEffect);
                ShowBeam();
            }

            // 各端：吐息期每 tick 结算（受击方本地）
            if (!MonsterBeamTiming.IsDamageActive(_beamConfig, stepElapsed)) return;

            SettleBeam();
        }

        protected override void OnStepVisualUpdate(float elapsed)
        {
            if (_beamConfig is null) return;
            if (!_beamActive) return;

            UpdateBeamVisual();
        }

        protected override void OnStepExit()
        {
            _lastHitTimes.Clear();
            _beamActive = false;

            HideEffect(_beamConfig?.ChargingEffect);
            HideBeam();
            HideEffect(_beamConfig?.HitEffect);
        }

        #region Private Methods

        /// <summary>
        /// 光束结算（各端受击方本地）：SphereCast（挂点 → 光束方向，障碍截断），
        /// 仅结算 SA 在本端的目标，同目标 tick 间隔去重（非本端目标不记录去重）。
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

            float now = Time.time;
            for (int i = 0; i < count; i++)
            {
                var hit = _castBuffer[i];
                if (hit.collider == null) continue;

                var tag = hit.collider.GetComponentInParent<EntityTag>();
                if (tag == null) continue;

                EntityId targetId = tag.Id;
                if (!targetId.IsValid) continue;

                // 受击方本地结算：非本端目标不结算、不记录去重，交目标权威端自己判定
                if (!MonsterSkillDamage.IsTargetAuthoritativeHere(targetId)) continue;

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
                    HitPoint = hit.point,
                    HitDirection = direction,
                    Force = _beamConfig.HitForce,
                };

                Msger.Send(MsgID.ApplyDamage, targetId, damageData);
                Msger.Send(MsgID.ApplyHit, targetId, hitData);
            }
        }

        /// <summary>
        /// 光束方向（水平锁定 / 垂直自由）：水平向 = 本体前向水平投影（Body 未接线兜底发射挂点前向），
        /// 俯仰 = 目标高度即时瞄准（无目标 pitch 0）；几何收在 MonsterBeamTiming（Editor 预演共享）。
        /// </summary>
        private Vector3 ResolveBeamDirection(Transform mouth)
        {
            Vector3 targetPos = default;
            bool hasTarget = _model.CastTargetId.IsValid
                             && TryGetTargetPosition(_model.CastTargetId, out targetPos);
            return MonsterBeamTiming.ResolveDirection(
                mouth.position,
                ResolveYawForward(mouth),
                hasTarget,
                targetPos
            );
        }

        private Vector3 ResolveYawForward(Transform mouth)
        {
            if (_beamConfig.Body != null) return _beamConfig.Body.forward;
            return mouth != null ? mouth.forward : Vector3.forward;
        }

        /// <summary>
        /// 本体 yaw 匀角速转向目标（权威端逐帧）：目标向量水平投影 LookRotation +
        /// RotateTowards（无总角限）；无目标 / 水平距退化不转，结束与打断均不恢复朝向。
        /// 经 Model.PushBodyRotation 由 MoveModule 落地——updateRotation 开启时
        /// FollowerEntity 的 ECS 同步系统每帧把内部旋转写回 transform，
        /// 步骤直写 body.rotation 会被当场拽回（A* 兼容层内聚于 MoveModule）。
        /// </summary>
        private void TurnBodyTowardTarget()
        {
            var body = _beamConfig.Body;
            if (body == null) return;
            if (!TryGetTargetPosition(_model.CastTargetId, out var targetPos)) return;

            Vector3 toTarget = targetPos - body.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.001f) return;

            Quaternion desiredRot = Quaternion.LookRotation(toTarget);
            float maxDegrees = _beamConfig.TurnSpeed * Mathf.Max(0f, Time.deltaTime);

            _model.PushBodyRotation(Quaternion.RotateTowards(body.rotation, desiredRot, maxDegrees));
        }

        private Transform ResolveMouth()
        {
            return _beamConfig.MouthTransform != null
                ? _beamConfig.MouthTransform
                : _beamConfig.FallbackTransform;
        }

        /// <summary>
        /// 每帧视觉更新（各端本地）：光束方向 → 细射线取视觉终点（ObstacleLayer | DamageLayer 截断，
        /// 未命中取最远点）→ 驱动光束线端点与击中特效显隐。与权威端 SphereCast 伤害判定互相独立，
        /// 细射线与球判定的擦边偏差可接受。
        /// </summary>
        private void UpdateBeamVisual()
        {
            var mouth = ResolveMouth();
            if (mouth == null) return;

            Vector3 origin = mouth.position;
            Vector3 direction = ResolveBeamDirection(mouth);
            if (direction.sqrMagnitude < 0.001f) return;

            bool hasHit = Physics.Raycast(
                origin,
                direction,
                out RaycastHit hit,
                _beamConfig.BeamMaxDistance,
                _beamConfig.ObstacleLayer | _beamConfig.DamageLayer,
                QueryTriggerInteraction.Ignore
            );
            Vector3 endPoint = hasHit ? hit.point : origin + direction * _beamConfig.BeamMaxDistance;

            if (_beamConfig.BeamRenderer != null)
            {
                _beamConfig.BeamRenderer.SetPosition(0, origin);
                _beamConfig.BeamRenderer.SetPosition(1, endPoint);
            }

            UpdateHitEffect(hasHit, endPoint);
        }

        private void ShowBeam()
        {
            if (_beamConfig.BeamRenderer == null)
            {
                Logging.Error("[MonsterBeamStep] ShowBeam: BeamRenderer 为 null，请检查 Inspector 引用。");
                return;
            }

            _beamConfig.BeamRenderer.gameObject.SetActive(true);

            // 立即驱动一次端点，避免显示序列化残留端点一帧
            UpdateBeamVisual();
        }

        private void HideBeam()
        {
            if (_beamConfig?.BeamRenderer == null) return;

            _beamConfig.BeamRenderer.gameObject.SetActive(false);
        }

        private void UpdateHitEffect(bool hasHit, Vector3 endPoint)
        {
            var hitEffect = _beamConfig.HitEffect;
            if (hitEffect == null) return;

            if (hasHit)
            {
                if (!hitEffect.gameObject.activeSelf)
                {
                    hitEffect.gameObject.SetActive(true);
                }
                hitEffect.transform.position = endPoint;
                if (!hitEffect.isPlaying)
                {
                    hitEffect.Play(true);
                }
            }
            else
            {
                HideEffect(hitEffect);
            }
        }

        private static void ShowEffect(ParticleSystem effect)
        {
            if (effect == null) return;

            effect.gameObject.SetActive(true);
            effect.Play(true);
        }

        private static void HideEffect(ParticleSystem effect)
        {
            if (effect == null) return;

            effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            effect.gameObject.SetActive(false);
        }

        #endregion
    }
}
