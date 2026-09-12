using System;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterAimStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("需要转向目标的怪物本体 Transform（权威端经 Model 推送旋转，代理端经 NetworkTransform 同步）")]
        public Transform Body;

        [Header("Aim")]
        [Tooltip("瞄准窗口时长（秒；窗口 = [StartOffset, StartOffset+AimDuration]）")]
        public float AimDuration = 0.5f;

        [Tooltip("本体 yaw 匀角速转向目标（度/秒）")]
        public float TurnSpeed = 180f;

        public override float Duration => AimDuration;
    }

    /// <summary>
    /// 瞄准步骤：窗口内原地持续转向施放目标（无位移、无伤害）。
    /// 权威端停 Follower 让位，并每 tick 经 Model.PushBodyRotation 落地 yaw 旋转；
    /// 代理端不自行转向，位姿由 NetworkTransform 同步。步骤进入表现复用基类
    /// StepAnimIds / StepEffects / StepSounds（可作为蓄力 / 瞄准表现）。
    /// </summary>
    public class MonsterAimStep : MonsterSkillStep
    {
        private readonly MonsterAimStepConfig _aimConfig;

        public MonsterAimStep(MonsterModel model, MonsterAimStepConfig config)
            : base(model, config)
        {
            _aimConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_aimConfig is null) return;
            if (!HasStateAuthority) return;

            // 施放期间让位给技能步骤；步骤结束后 AI 下一 tick 自行恢复移动意图。
            _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_aimConfig is null) return;
            if (!HasStateAuthority) return;
            if (IsContentEnded) return;

            TurnBodyTowardTarget();
        }

        #region Private Methods

        /// <summary>
        /// 权威端 yaw 匀角速转向目标。经 Model.PushBodyRotation 由 MoveModule 落地，
        /// 避免 FollowerEntity 的 ECS 同步系统把步骤直写 Transform 的旋转拽回。
        /// </summary>
        private void TurnBodyTowardTarget()
        {
            var body = _aimConfig.Body;
            if (body == null) return;
            if (!TryGetTargetPosition(_model.CastTargetId, out var targetPos)) return;

            Vector3 toTarget = targetPos - body.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude <= 0.0001f) return;

            float maxDegrees = _aimConfig.TurnSpeed * Mathf.Max(0f, Time.deltaTime);
            Quaternion rotation = MonsterAimTiming.RotateTowardsPlanarTarget(
                body.rotation,
                toTarget,
                maxDegrees
            );
            _model.PushBodyRotation(rotation);
        }

        #endregion
    }
}
