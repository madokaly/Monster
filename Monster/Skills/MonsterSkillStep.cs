using System.Collections.Generic;
using Framework;
using Framework.Core;
using Framework.Network;
using Game.Components;
using Game.DTOs;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能原子步骤基类（运行时，由链运行器驱动，不自持生命周期、不监听 Model）。
    /// 生命周期：Enter（窗口开始，全端；点式步骤 Enter 即 Exit）→ Tick（窗口内逐 tick，全端）→ Exit（窗口结束，全端）。
    /// 权威端逻辑（判定与结算）与各端本地逻辑（特效）由步骤内部按 HasStateAuthority 分流（§16.3）；
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

        /// <summary> 球形范围结算的碰撞体缓冲（基类共享，各子类复用） </summary>
        private readonly Collider[] _overlapBuffer = new Collider[32];

        protected MonsterSkillStep(MonsterModel model, MonsterSkillStepConfig config)
        {
            _model = model;
            _config = config;
        }

        #region Runner-driven Lifecycle

        internal void Enter(float elapsed)
        {
            IsActive = true;

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
            OnStepTick(elapsed);
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
        /// 球形范围结算（共享管线）：OverlapSphere → 高度过滤 → 身份解析（即用即弃）
        /// → 组装 DamageData → ApplyDamage + ApplyHit。
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

            int count = Physics.OverlapSphereNonAlloc(
                center,
                radius,
                _overlapBuffer,
                damageLayer,
                QueryTriggerInteraction.Ignore
            );

            int settled = 0;
            for (int i = 0; i < count; i++)
            {
                if (maxTargets > 0 && settled >= maxTargets) break;

                var col = _overlapBuffer[i];
                if (col == null) continue;

                // 高度过滤（maxAttackHeight > 0 时生效）
                if (maxAttackHeight > 0f && Mathf.Abs(col.transform.position.y - center.y) > maxAttackHeight)
                {
                    continue;
                }

                // 身份解析：即用即弃，只提取 EntityId（§1.6）
                var tag = col.GetComponentInParent<EntityTag>();
                if (tag == null) continue;

                EntityId targetId = tag.Id;
                if (!targetId.IsValid) continue;
                if (!hitTargets.Add(targetId)) continue;

                Vector3 hitPoint = col.transform.position;
                Vector3 hitDirection = hitPoint - center;
                hitDirection.y = 0f;
                hitDirection = hitDirection.sqrMagnitude > 0.001f ? hitDirection.normalized : fallbackDirection;

                var damageData = new DamageData { Damage = damage, AttackerId = _model.Id, };

                var hitData = new HitData
                {
                    HitPoint = hitPoint, HitDirection = hitDirection, Force = hitForce,
                };

                Msger.Send(MsgID.ApplyDamage, targetId, damageData);
                Msger.Send(MsgID.ApplyHit, targetId, hitData);
                settled++;
            }
        }

        #endregion
    }
}
