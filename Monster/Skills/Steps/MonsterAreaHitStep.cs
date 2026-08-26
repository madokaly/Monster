using System;
using System.Collections.Generic;
using Framework;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterAreaHitStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("技能判定原点（怪物自身 Transform）")]
        public Transform SelfTransform;

        [Header("Damage")]
        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("单次结算伤害")]
        public int Damage = 1;

        [Tooltip("伤害判定半径")]
        public float Radius = 3f;

        [Tooltip("高度过滤（受击目标与判定原点高度差超过该值不命中；<=0 不过滤）")]
        public float MaxAttackHeight;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 5f;

        [Tooltip("每次伤害最大目标数（0 = 无限制）")]
        public int MaxTargets;

        [Tooltip("窗口时长（秒）：>0 窗口式 [StartOffset, StartOffset+Window] 连续检测；0 点式 StartOffset 时刻一次性结算")]
        public float Window;

        public override float Duration => Window;
    }

    /// <summary>
    /// 范围结算步骤：自圆心 OverlapSphere 结算（共享结算管线）。
    /// 窗口式（Window > 0）：窗口内每 tick 检测，同目标窗口内一次；
    /// 点式（Window = 0）：Enter 时刻一次性结算（即 Combo 段窗口与 PulseArea 时间点的共同原子，§16.4）。
    /// 权威端结算；无本地表现。
    /// </summary>
    public class MonsterAreaHitStep : MonsterSkillStep
    {
        private readonly MonsterAreaHitStepConfig _areaConfig;

        /// <summary> 窗口式结算的目标去重集（窗口内每目标一次） </summary>
        private readonly HashSet<EntityId> _hitTargets = new();

        public MonsterAreaHitStep(MonsterModel model, MonsterAreaHitStepConfig config)
            : base(model, config)
        {
            _areaConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_areaConfig is null) return;

            // 进入表现（StepEffects / StepSounds）由基类 Enter 统一播放（§16.3）
            if (!HasStateAuthority) return;

            _hitTargets.Clear();

            if (_areaConfig.Window > 0f) return;

            // 点式：一次性结算
            Settle();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_areaConfig is null) return;
            if (!HasStateAuthority) return;
            if (_areaConfig.Window <= 0f) return;

            // 窗口式：每 tick 检测（去重集窗口内每目标一次）
            Settle();
        }

        protected override void OnStepExit()
        {
            _hitTargets.Clear();
        }

        protected override void OnStepAuthorityChanged()
        {
            _hitTargets.Clear();
        }

        #region Private Methods

        private void Settle()
        {
            var self = _areaConfig.SelfTransform;
            if (self == null) return;

            SettleSphereDamage(
                self.position,
                _areaConfig.Radius,
                _areaConfig.DamageLayer,
                _areaConfig.MaxAttackHeight,
                _areaConfig.Damage,
                _areaConfig.HitForce,
                _hitTargets,
                self.forward,
                _areaConfig.MaxTargets
            );
        }

        #endregion
    }
}
