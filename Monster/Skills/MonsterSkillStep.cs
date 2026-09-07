using System.Collections.Generic;
using Framework;
using Framework.Network;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能原子步骤基类（运行时，由链运行器驱动，不自持生命周期、不监听 Model）。
    /// 生命周期：Enter（窗口开始，全端）→ Tick（窗口内逐 tick，全端）→ Exit（窗口结束，全端）；
    /// 窗口 = [StartOffset, StartOffset+TotalDuration]（内容 + 收尾延迟 EndOffset）——
    /// 内容结束后进入收尾段：表现 / VisualUpdate 继续、Exit 清理延迟，伤害类逻辑经 IsContentEnded 守卫停止。
    /// 零窗口步骤（TotalDuration = 0，点式且无收尾）Enter 即 Exit。
    /// VisualUpdate（窗口内逐帧 LateUpdate，全端；供需要每帧平滑的表现驱动，如 Beam 端点跟随）。
    /// 行为与状态逻辑（移动 / 选点 / 动画写入）由步骤内部按 HasStateAuthority 分流；
    /// 伤害判定与结算恒为受击方本地结算（各端本端时钟 + 本端真实位置，§1.7）；
    /// 步骤实例装配期创建、施放间复用（Enter / Exit 时重置中间态）。
    /// </summary>
    public abstract class MonsterSkillStep
    {
        protected readonly MonsterModel _model;
        protected readonly MonsterSkillStepConfig _config;

        /// <summary> 本端是否权威（由链运行器转发） </summary>
        public bool HasStateAuthority { get; private set; }

        /// <summary> 步骤窗口是否进行中（本端） </summary>
        public bool IsActive { get; private set; }

        /// <summary> 步骤内容是否已结束（本端时钟越过内容时长；收尾段恒 true，供内容逻辑守卫） </summary>
        protected bool IsContentEnded { get; private set; }

        protected MonsterSkillStep(MonsterModel model, MonsterSkillStepConfig config)
        {
            _model = model;
            _config = config;
        }

        #region Runner-driven Lifecycle

        internal void Enter(float elapsed)
        {
            IsActive = true;
            IsContentEnded = false;

            // 步骤进入表现（各端本地随机播，先于子类逻辑；与链级进入表现叠加，§16.3）
            if (_config != null)
            {
                MonsterSkillPresentation.PlayRandomEffect(_config.StepEffects);
                MonsterSkillPresentation.PlayRandomSound(_config.StepSounds, _config.SoundAttachPoint);
            }

            OnStepEnter(elapsed);
        }

        internal void Tick(float elapsed)
        {
            // 内容结束边沿（本端时钟越过内容时长）：内容逻辑完成、步骤进入收尾段
            if (!IsContentEnded
                && _config != null
                && elapsed >= _config.StartOffset + _config.Duration)
            {
                IsContentEnded = true;
                OnStepContentEnded();
            }

            OnStepTick(elapsed);
        }

        internal void VisualUpdate(float elapsed)
        {
            OnStepVisualUpdate(elapsed);
        }

        internal void Exit()
        {
            OnStepExit();
            IsActive = false;
        }

        internal void Dispose()
        {
            OnStepDispose();
        }

        internal void AuthorityChanged(bool hasStateAuthority)
        {
            HasStateAuthority = hasStateAuthority;
            OnStepAuthorityChanged();
        }

        #endregion

        #region Overridables

        protected abstract void OnStepEnter(float elapsed);

        protected virtual void OnStepTick(float elapsed) { }

        protected virtual void OnStepVisualUpdate(float elapsed) { }

        /// <summary>
        /// 内容结束（本端时钟越过内容时长；随 Tick 边沿触发一次）。收尾段开始——
        /// 表现继续、窗口延迟到 TotalDuration 结束；在此停伤 / 停内容逻辑。
        /// 步骤在内容结束前被截断（打断 / 重叠截断）时不触发，收口走 OnStepExit。
        /// </summary>
        protected virtual void OnStepContentEnded() { }

        protected virtual void OnStepExit() { }

        protected virtual void OnStepDispose() { }

        protected virtual void OnStepAuthorityChanged() { }

        #endregion

        #region Helpers

        /// <summary>
        /// 权威端本地解析目标位置（即用即弃：只取位置，不缓存引用，§1.6）。
        /// </summary>
        public static bool TryGetTargetPosition(EntityId targetId, out Vector3 position)
        {
            position = default;
            if (!targetId.IsValid) return false;

            if (!NetworkMgr.TryFindObject(targetId.NetId, out var targetObject)) return false;

            position = targetObject.transform.position;
            return true;
        }

        /// <summary>
        /// 球形范围结算（共享管线 MonsterSkillDamage 的薄包装，受击方本地结算 §1.7）：
        /// hitTargets 由调用方维护（窗口内每目标一次，点间 / 段间可重复命中）；
        /// maxTargets > 0 时限制单次结算目标数（0 = 无限制）；maxAttackHeight <= 0 时不过滤高度。
        /// </summary>
        protected void SettleSphereDamage(
            Vector3 center,
            float radius,
            LayerMask damageLayer,
            float maxAttackHeight,
            int damage,
            float hitForce,
            HashSet<EntityId> hitTargets,
            Vector3 fallbackDirection,
            int maxTargets = 0)
        {
            if (_model is null) return;

            MonsterSkillDamage.SettleSphere(
                _model.Id,
                center,
                radius,
                damageLayer,
                maxAttackHeight,
                damage,
                hitForce,
                hitTargets,
                fallbackDirection,
                maxTargets
            );
        }

        #endregion
    }
}
