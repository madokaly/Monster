using System;
using System.Collections.Generic;
using Framework;
using Framework.Core;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterSlamStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("技能判定原点（怪物自身 Transform）")]
        public Transform SelfTransform;

        [Header("Damage")]
        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("震击伤害")]
        public int Damage = 1;

        [Tooltip("震击范围半径")]
        public float Radius = 5f;

        [Tooltip("高度过滤（受击目标与判定原点高度差超过该值不命中；<=0 不过滤）")]
        public float MaxAttackHeight;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 10f;
    }

    /// <summary>
    /// 震击步骤（点式，§16.4）：StartOffset 时刻权威端单次范围结算，特效各端本地播。
    /// 组合示例：落地震击 = [Chase(0.6s 起), Slam(1.2s 起)]。
    /// </summary>
    public class MonsterSlamStep : MonsterSkillStep
    {
        private readonly MonsterSlamStepConfig _slamConfig;

        public MonsterSlamStep(MonsterModel model, MonsterSlamStepConfig config) : base(model, config)
        {
            _slamConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_slamConfig is null) return;

            // 进入表现（StepEffects / StepSounds）由基类 Enter 统一播放（§16.3）
            if (!HasStateAuthority) return;

            Settle();
        }

        #region Private Methods

        /// <summary>
        /// 权威端单次范围结算（共享结算管线）。
        /// </summary>
        private void Settle()
        {
            var self = _slamConfig.SelfTransform;
            if (self == null) return;

            var hitTargets = new HashSet<EntityId>();
            SettleSphereDamage(
                self.position,
                _slamConfig.Radius,
                _slamConfig.DamageLayer,
                _slamConfig.MaxAttackHeight,
                _slamConfig.Damage,
                _slamConfig.HitForce,
                hitTargets,
                self.forward
            );
        }

        #endregion
    }
}
