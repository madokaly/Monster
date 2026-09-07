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
        [Tooltip("前摇时长（秒，步骤起点 → 开启刀刃检测；此段只播表现不判定）")]
        public float HitDelay;

        [Tooltip("伤害窗口时长（秒，刀刃检测开启 → 关闭；需 > 0，装配校验把关）")]
        public float Window;

        [Tooltip("后摇时长（秒，关闭刀刃检测 → 步骤结束；此段只播表现不判定）")]
        public float RecoveryDuration;

        [Header("Damage")]
        [Tooltip("单次挥击伤害（开窗时经 SetDamageOverride 写入，prefab 上关闭挥速缩放）")]
        public int Damage = 1;

        public override float Duration => HitDelay + Window + RecoveryDuration;
    }

    /// <summary>
    /// 近战步骤：三段相位 HitDelay 前摇 → Window 伤害窗口 → RecoveryDuration 后摇（时长由相位派生）。
    /// 各端在伤害窗口上升沿按本端时钟原子开窗（伤害覆盖 + 使能检测 + 刷新挥砍基准，防跨禁用期幻影挥砍；
    /// 动画经 AnimId 状态同步，各端时差 §1.7 偏差允许），下降沿 / 步骤退出关窗。
    /// 命中经 MeleeHandler 的 OnMeleeHit 事件上报 → 仅结算 SA 在本端的目标（受击方本地结算 §1.7）
    /// → 组装 DamageData / HitData → 双总线结算。
    /// 伤害以步骤 Config.Damage 为单一可信源（开窗时经 SetDamageOverride 写入）；
    /// 命中过滤以组件 _hitMask 层级为准（层即规则），每目标冷却由组件自身维护（各端独立）。
    /// </summary>
    public class MonsterMeleeStep : MonsterSkillStep
    {
        private readonly MonsterMeleeStepConfig _meleeConfig;

        /// <summary> 本端：伤害窗口是否开启（本端时钟上升 / 下降沿检测） </summary>
        private bool _hitWindowOpen;

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
            _hitWindowOpen = false;

            if (_meleeConfig is null) return;
            if (_meleeConfig.MeleeHandlers == null)
            {
                Logging.Error($"[MonsterMeleeStep] OnStepEnter: 未配置 MeleeHandler");
                return;
            }

            // 进入表现（StepEffects / StepSounds）由基类 Enter 统一播放（§16.3）；
            // 刀刃检测不在步骤起点开启，等各端 Tick 推进到伤害窗口上升沿
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_meleeConfig is null) return;

            // 伤害窗口门控（各端本端时钟，边沿驱动）：开窗 / 关窗各只做一次
            float stepElapsed = elapsed - _config.StartOffset;
            bool damageActive = MonsterMeleeTiming.IsDamageActive(_meleeConfig, stepElapsed);

            if (damageActive == _hitWindowOpen) return;

            if (damageActive) OpenHitWindow();
            else CloseHitWindow();
        }

        protected override void OnStepExit()
        {
            if (_meleeConfig is null) return;

            CloseHitWindow();
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

            // 受击方本地结算（§1.7）：只结算 SA 在本端的目标，其余端各自命中各自结算
            if (!MonsterSkillDamage.IsTargetAuthoritativeHere(target.Id)) return;

            var damageData = new DamageData { Damage = damage, AttackerId = _model.Id, };

            var hitData = new HitData
            {
                HitPoint = hitPoint,
                HitDirection = hitDirection,
                Force = force,
            };

            // 伤害结算 + 打击反馈（目标权威端就在本端，端内直接落地）
            Msger.Send(MsgID.ApplyDamage, target.Id, damageData);
            Msger.Send(MsgID.ApplyHit, target.Id, hitData);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 开启刀刃检测（原子三件事：伤害覆盖 → 使能 → 刷新挥砍基准，防跨禁用期幻影挥砍）
        /// </summary>
        private void OpenHitWindow()
        {
            if (_meleeConfig?.MeleeHandlers == null) return;

            _hitWindowOpen = true;

            for (int i = 0; i < _meleeConfig.MeleeHandlers.Length; i++)
            {
                var handler = _meleeConfig.MeleeHandlers[i];
                if (handler == null) continue;

                handler.SetDamageOverride(Mathf.Max(1, _meleeConfig.Damage));
                handler.enabled = true;
                handler.ResetSwingBasis();
            }
        }

        /// <summary>
        /// 关闭刀刃检测（幂等；顺带清除伤害覆盖）
        /// </summary>
        private void CloseHitWindow()
        {
            _hitWindowOpen = false;

            if (_meleeConfig?.MeleeHandlers == null) return;

            for (int i = 0; i < _meleeConfig.MeleeHandlers.Length; i++)
            {
                var handler = _meleeConfig.MeleeHandlers[i];
                if (handler == null) continue;

                handler.enabled = false;
                handler.ClearDamageOverride();
            }
        }

        #endregion
    }
}
