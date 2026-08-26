using System;
using Framework.Core;
using Game.Components;
using Game.DTOs;
using Gameplay.Components;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterMeleeStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("近战处理器组件（刀刃扫掠检测；命中层级 _hitMask 与打击力度 _hitForce 在组件自身序列化配置）")]
        public MeleeHandler[] MeleeHandlers;

        [Header("Cast Window")]
        [Tooltip("近战检测窗口时长（秒；旧制 = 开满整个施放 = 技能动画时长）")]
        public float Window;

        [Header("Damage")]
        [Tooltip("单次挥击伤害（开窗前经 SetDamageOverride 写入，prefab 上关闭挥速缩放）")]
        public int Damage = 1;

        public override float Duration => Window;
    }

    /// <summary>
    /// 近战步骤（§16.4）：窗口开启 MeleeHandler（仅权威端），窗口结束关闭；
    /// 命中经 MeleeHandler 的 OnMeleeHit 事件上报 → 组装 DamageData / HitData → 双总线结算。
    /// 伤害以步骤 Config.Damage 为单一可信源（开窗前经 SetDamageOverride 写入）；
    /// 命中过滤以组件 _hitMask 层级为准（层即规则），每目标冷却由组件自身维护。
    /// 窗口时长 = Duration（>0 窗口式；0 时配合链内后续步骤表达，本步骤即点式开关脉冲）。
    /// </summary>
    public class MonsterMeleeStep : MonsterSkillStep
    {
        private readonly MonsterMeleeStepConfig _meleeConfig;

        public MonsterMeleeStep(MonsterModel model, MonsterMeleeStepConfig config)
            : base(model, config)
        {
            _meleeConfig = config;

            RegisterComponentListeners();
        }

        protected override void OnStepDispose()
        {
            ClearComponentListeners();
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_meleeConfig is null) return;
            if (_meleeConfig.MeleeHandlers == null)
            {
                Logging.Error($"[MonsterMeleeStep] OnStepEnter: 未配置 MeleeHandler");
                return;
            }

            // 进入表现（StepEffects / StepSounds）由基类 Enter 统一播放（§16.3）
            if (!HasStateAuthority) return;

            // 权威端：伤害覆盖（Config 单一可信源）+ 开启刀刃检测
            for (int i = 0; i < _meleeConfig.MeleeHandlers.Length; i++)
            {
                var handler = _meleeConfig.MeleeHandlers[i];
                if (handler == null) continue;
                handler.SetDamageOverride(Mathf.Max(1, _meleeConfig.Damage));
                handler.enabled = true;
            }
        }

        protected override void OnStepExit()
        {
            if (_meleeConfig is null) return;
            if (!HasStateAuthority) return;

            SetMeleeActive(false);
        }

        protected override void OnStepAuthorityChanged()
        {
            // 权威易主：保守关闭组件（等下次 Enter 再开）
            SetMeleeActive(false);
        }

        #region Registers

        private void RegisterComponentListeners()
        {
            if (_meleeConfig is null) return;
            if (_meleeConfig.MeleeHandlers == null) return;

            for (int i = 0; i < _meleeConfig.MeleeHandlers.Length; i++)
            {
                var handler = _meleeConfig.MeleeHandlers[i];
                if (handler == null) continue;
                handler.OnMeleeHit += OnMeleeHitHandler;
            }
        }

        private void ClearComponentListeners()
        {
            if (_meleeConfig is null) return;
            if (_meleeConfig.MeleeHandlers == null) return;

            for (int i = 0; i < _meleeConfig.MeleeHandlers.Length; i++)
            {
                var handler = _meleeConfig.MeleeHandlers[i];
                if (handler == null) continue;
                handler.OnMeleeHit -= OnMeleeHitHandler;
            }
        }

        #endregion

        #region Components Handlers

        private void OnMeleeHitHandler(
            EntityTag target,
            int damage,
            Vector3 hitPoint,
            Vector3 hitDirection,
            float force)
        {
            if (_model is null) return;
            if (_meleeConfig is null) return;
            if (!HasStateAuthority) return;

            var damageData = new DamageData { Damage = damage, AttackerId = _model.Id, };

            var hitData = new HitData
            {
                HitPoint = hitPoint, HitDirection = hitDirection, Force = force,
            };

            // 伤害结算 + 打击反馈（经通用命令到达目标权威端）
            Msger.Send(MsgID.ApplyDamage, target.Id, damageData);
            Msger.Send(MsgID.ApplyHit, target.Id, hitData);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 开关刀刃检测组件（幂等；关闭时顺带清除伤害覆盖）
        /// </summary>
        private void SetMeleeActive(bool active)
        {
            if (_meleeConfig is null) return;
            if (_meleeConfig.MeleeHandlers == null) return;

            for (int i = 0; i < _meleeConfig.MeleeHandlers.Length; i++)
            {
                var handler = _meleeConfig.MeleeHandlers[i];
                if (handler == null) continue;

                handler.enabled = active;
                if (!active) handler.ClearDamageOverride();
            }
        }

        #endregion
    }
}
