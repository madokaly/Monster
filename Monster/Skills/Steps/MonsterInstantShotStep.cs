using System;
using Framework;
using Framework.Core;
using Framework.Network;
using Game.DTOs;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterInstantShotStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("技能释放原点（怪物自身 Transform，命中方向与特效兜底用）")]
        public Transform SelfTransform;

        [Header("Muzzle Effect")]
        [Tooltip("炮口特效 prefab（纯表现，各端本地实例化）")]
        public GameObject MuzzleEffectPrefab;

        [Tooltip("炮口特效挂点（空则挂释放原点）")]
        public Transform MuzzleAttachPoint;

        [Tooltip("炮口特效相对挂点的本地位置偏移")]
        public Vector3 MuzzleOffset = Vector3.zero;

        [Tooltip("炮口特效相对挂点的本地旋转（欧拉角）")]
        public Vector3 MuzzleRotation = Vector3.zero;

        [Tooltip("炮口特效缩放（默认 1）")]
        public Vector3 MuzzleScale = Vector3.one;

        [Tooltip("炮口特效自毁时长（秒，<=0 兜底 0.6s）")]
        public float MuzzleDestroyTime = 0.6f;

        [Header("Damage")]
        [Tooltip("命中伤害（对 CastTargetId 直接结算）")]
        public int Damage = 1;

        [Tooltip("命中方向反作用力")]
        public float HitForce = 0f;

        [Tooltip("伤害结算延迟（秒，相对步骤起点；0 = Enter 时结算）")]
        public float DamageOffset;

        public override float Duration => MonsterInstantShotTiming.GetDamageTime(this);
    }

    /// <summary>
    /// 瞬发步骤：对 CastTargetId 直接结算（无弹道瞬伤，单目标；
    /// 射程筛选由 AI 决策层保证，不做二次 OverlapSphere）。
    /// Enter 各端本地播炮口特效；各端在 DamageOffset 后尝试结算（0 = Enter 即尝试），
    /// 仅目标 SA 在本端才由本端结算（受击方本地结算，命中方向用本端真实位置）。
    /// </summary>
    public class MonsterInstantShotStep : MonsterSkillStep
    {
        private readonly MonsterInstantShotStepConfig _instantShotConfig;

        /// <summary> 本端：本次施放是否已结算（施放间复用需重置） </summary>
        private bool _damageResolved;

        public MonsterInstantShotStep(MonsterModel model, MonsterInstantShotStepConfig config) : base(model, config)
        {
            _instantShotConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_instantShotConfig is null) return;

            _damageResolved = false;

            PlayMuzzleEffect();

            if (MonsterInstantShotTiming.GetDamageTime(_instantShotConfig) > 0f) return;

            TryResolveDamage();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_instantShotConfig is null) return;
            if (_damageResolved) return;
            float damageTime = MonsterInstantShotTiming.GetDamageTime(_instantShotConfig);
            if (damageTime <= 0f) return;

            if (elapsed < _config.StartOffset + damageTime) return;

            TryResolveDamage();
        }

        #region Private Methods

        /// <summary>
        /// 到点尝试结算（各端本端时钟）：目标无效 → 落已结算标记收口；
        /// 目标 SA 不在本端 → 不落标记（目标权威中途迁入本端时仍可补结算）。
        /// </summary>
        private void TryResolveDamage()
        {
            var targetId = _model.CastTargetId;
            if (!targetId.IsValid)
            {
                _damageResolved = true;
                return;
            }

            if (!MonsterSkillDamage.IsTargetAuthoritativeHere(targetId)) return;

            _damageResolved = true;
            ResolveDamage(targetId);
        }

        /// <summary>
        /// 结算：对 CastTargetId 直接 ApplyDamage / ApplyHit（方向取攻击者 → 目标当前位置）。
        /// </summary>
        private void ResolveDamage(EntityId targetId)
        {
            var selfTransform = _instantShotConfig.SelfTransform;
            if (selfTransform == null) return;

            Vector3 hitPoint = selfTransform.position;
            Vector3 hitDirection = selfTransform.forward;

            if (NetworkMgr.TryFindObject(targetId.NetId, out var targetObject))
            {
                hitPoint = targetObject.transform.position;
                hitDirection = hitPoint - selfTransform.position;
                hitDirection.y = 0f;
                if (hitDirection.sqrMagnitude > 0.001f)
                {
                    hitDirection = hitDirection.normalized;
                }
                else
                {
                    hitDirection = selfTransform.forward;
                }
            }

            var damageData = new DamageData
            {
                Damage = Mathf.Max(1, _instantShotConfig.Damage),
                AttackerId = _model.Id,
            };

            var hitData = new HitData
            {
                HitPoint = hitPoint,
                HitDirection = hitDirection,
                Force = _instantShotConfig.HitForce,
            };

            Msger.Send(MsgID.ApplyDamage, targetId, damageData);
            Msger.Send(MsgID.ApplyHit, targetId, hitData);
        }

        /// <summary>
        /// 各端本地播炮口特效：挂点实例化 + 本地偏移 / 旋转 / 缩放 + 到时自毁。
        /// </summary>
        private void PlayMuzzleEffect()
        {
            if (_instantShotConfig.MuzzleEffectPrefab == null)
            {
                Logging.Error("[MonsterInstantShotStep] PlayMuzzleEffect: MuzzleEffectPrefab 为 null，请检查 Inspector 引用。");
                return;
            }

            var attach = _instantShotConfig.MuzzleAttachPoint != null
                ? _instantShotConfig.MuzzleAttachPoint
                : _instantShotConfig.SelfTransform;
            if (attach == null) return;

            var effectObj = UnityEngine.Object.Instantiate(
                _instantShotConfig.MuzzleEffectPrefab,
                attach.position,
                attach.rotation,
                attach
            );
            effectObj.transform.localPosition = _instantShotConfig.MuzzleOffset;
            effectObj.transform.localEulerAngles = _instantShotConfig.MuzzleRotation;
            effectObj.transform.localScale = _instantShotConfig.MuzzleScale;

            float destroyTime = _instantShotConfig.MuzzleDestroyTime > 0f ? _instantShotConfig.MuzzleDestroyTime : 0.6f;
            UnityEngine.Object.Destroy(effectObj, Mathf.Max(0.1f, destroyTime));
        }

        #endregion
    }
}
