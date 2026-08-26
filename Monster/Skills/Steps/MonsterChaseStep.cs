using System;
using System.Collections.Generic;
using Framework;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterChaseStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("追击期间直接驱动的本体 Transform（限角旋转 + 限距位移，NetworkTransform 同步）")]
        public Transform Body;

        [Header("Chase")]
        [Tooltip("追击窗口时长（秒；窗口 = [StartOffset, StartOffset+ChaseWindow]）")]
        public float ChaseWindow = 1f;

        [Tooltip("追击限角（度）")]
        public float ChaseAngle = 90f;

        [Tooltip("追击限距（米）")]
        public float ChaseDistance = 5f;

        [Tooltip("追击触发距离（目标距离超过该值才位移）")]
        public float ChaseStartDistance = 2f;

        [Header("Contact Damage（可选，0 = 不撞伤）")]
        [Tooltip("撞伤结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("撞伤伤害（窗口内每 tick 检测，同目标窗口内一次；0 = 不撞伤）")]
        public int ContactDamage;

        [Tooltip("撞伤判定半径")]
        public float ContactHitRadius = 2f;

        [Tooltip("撞伤受击方向反作用力")]
        public float HitForce = 5f;

        [Tooltip("撞伤高度过滤（受击目标与判定原点高度差超过该值不命中；<=0 不过滤）")]
        public float MaxAttackHeight;

        public override float Duration => ChaseWindow;
    }

    /// <summary>
    /// 追击步骤：窗口内限角旋转 + 限距位移逼近目标（单目标，§16.4）。
    /// Enter（权威端）记录起点 / 算落点（触发距离外才位移）/ 停 Follower 让位；
    /// Tick（权威端）按窗口进度 lerp 旋转与位移，可选每 tick 撞伤；
    /// 施放结束由 AI 恢复寻路（同旧制）。
    /// </summary>
    public class MonsterChaseStep : MonsterSkillStep
    {
        private readonly MonsterChaseStepConfig _chaseConfig;

        private Vector3 _chaseStartPos;
        private Quaternion _chaseStartRot;
        private Vector3 _chaseDestination;
        private bool _chaseWillMove;
        private readonly HashSet<EntityId> _contactHitTargets = new();

        public MonsterChaseStep(MonsterModel model, MonsterChaseStepConfig config)
            : base(model, config)
        {
            _chaseConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_chaseConfig is null) return;
            if (!HasStateAuthority) return;

            BeginChase();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_chaseConfig is null) return;
            if (!HasStateAuthority) return;
            if (_chaseConfig.ChaseWindow <= 0f) return;

            float t = Mathf.Clamp01((elapsed - _config.StartOffset) / _chaseConfig.ChaseWindow);
            StepChase(t);
        }

        protected override void OnStepExit()
        {
            _contactHitTargets.Clear();
        }

        protected override void OnStepAuthorityChanged()
        {
            _contactHitTargets.Clear();
        }

        #region Private Methods

        /// <summary>
        /// 追击开始：记录起点 / 算落点（触发距离外才位移）/ 停 Follower 让位。
        /// </summary>
        private void BeginChase()
        {
            var body = _chaseConfig.Body;
            if (body == null) return;

            _chaseStartPos = body.position;
            _chaseStartRot = body.rotation;
            _chaseDestination = _chaseStartPos;
            _chaseWillMove = false;
            _contactHitTargets.Clear();

            if (!TryGetTargetPosition(_model.CastTargetId, out var targetPos)) return;

            Vector3 toTargetInit = targetPos - _chaseStartPos;
            toTargetInit.y = 0f;
            float startDist = toTargetInit.magnitude;
            if (startDist < 0.001f) return;

            _chaseWillMove = startDist > _chaseConfig.ChaseStartDistance;
            if (_chaseWillMove)
            {
                Vector3 dir = toTargetInit / startDist;
                float maxDistance = _chaseConfig.ChaseDistance;
                float chaseStartDistance = _chaseConfig.ChaseStartDistance;

                if (startDist <= chaseStartDistance + maxDistance)
                {
                    // 可达：移到距离目标 chaseStartDistance 处
                    _chaseDestination = new Vector3(
                        targetPos.x - dir.x * chaseStartDistance,
                        _chaseStartPos.y,
                        targetPos.z - dir.z * chaseStartDistance
                    );
                }
                else
                {
                    // 太远：从起点向目标方向最多追 maxDistance
                    _chaseDestination = new Vector3(
                        _chaseStartPos.x + dir.x * maxDistance,
                        _chaseStartPos.y,
                        _chaseStartPos.z + dir.z * maxDistance
                    );
                }
            }

            // 停 Follower 让位（经 Model 事实，MoveModule 执行；施放结束由 AI 恢复）
            _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
        }

        /// <summary>
        /// 追击窗口内每 tick：限角旋转（朝向当前目标位置）+ 限距位移（固定落点 lerp）+ 可选撞伤。
        /// </summary>
        private void StepChase(float t)
        {
            var body = _chaseConfig.Body;
            if (body == null) return;

            if (TryGetTargetPosition(_model.CastTargetId, out var targetPos))
            {
                Vector3 toTarget = targetPos - body.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.001f)
                {
                    Quaternion desiredRot = Quaternion.LookRotation(toTarget);
                    Quaternion clampedRot = Quaternion.RotateTowards(_chaseStartRot, desiredRot, _chaseConfig.ChaseAngle);
                    body.rotation = Quaternion.Slerp(_chaseStartRot, clampedRot, t);
                }
            }

            if (_chaseWillMove)
            {
                body.position = Vector3.Lerp(_chaseStartPos, _chaseDestination, t);
            }

            if (_chaseConfig.ContactDamage > 0)
            {
                SettleContactDamage();
            }
        }

        /// <summary>
        /// 撞伤检测：共享结算管线（基类 SettleSphereDamage），同目标窗口内一次。
        /// </summary>
        private void SettleContactDamage()
        {
            var body = _chaseConfig.Body;
            if (body == null) return;
            if (_chaseConfig.ContactHitRadius <= 0f) return;

            SettleSphereDamage(
                body.position,
                _chaseConfig.ContactHitRadius,
                _chaseConfig.DamageLayer,
                _chaseConfig.MaxAttackHeight,
                _chaseConfig.ContactDamage,
                _chaseConfig.HitForce,
                _contactHitTargets,
                body.forward
            );
        }

        #endregion
    }
}
