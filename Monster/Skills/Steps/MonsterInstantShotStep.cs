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
        public float HitForce = 5f;

        [Tooltip("伤害结算延迟（秒，相对步骤起点；0 = Enter 时结算）")]
        public float DamageOffset;

        public override float Duration => Mathf.Max(0f, DamageOffset);
    }

    /// <summary>
    /// 瞬发步骤（§16.4）：对 CastTargetId 直接结算（无弹道瞬伤，单目标；
    /// 射程筛选由 AI 决策层保证，不做二次 OverlapSphere）。
    /// Enter 各端本地播炮口特效；权威端在 DamageOffset 后结算（0 = Enter 即结算）。
    /// </summary>
    public class MonsterInstantShotStep : MonsterSkillStep
    {
        private readonly MonsterInstantShotStepConfig _instantShotConfig;

        /// <summary> 权威端：本次施放是否已结算（施放间复用需重置） </summary>
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

            if (!HasStateAuthority) return;
            if (_instantShotConfig.DamageOffset > 0f) return;

            _damageResolved = true;
            ResolveDamage();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_instantShotConfig is null) return;
            if (!HasStateAuthority) return;
            if (_damageResolved) return;
            if (_instantShotConfig.DamageOffset <= 0f) return;

            if (elapsed < _config.StartOffset + _instantShotConfig.DamageOffset) return;

            _damageResolved = true;
            ResolveDamage();
        }

        #region Private Methods

        /// <summary>
        /// 权威端结算：对 CastTargetId 直接 ApplyDamage / ApplyHit（方向取攻击者 → 目标当前位置）。
        /// </summary>
        private void ResolveDamage()
        {
            var targetId = _model.CastTargetId;
            if (!targetId.IsValid) return;

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
                Damage = Mathf.Max(1, _instantShotConfig.Damage), AttackerId = _model.Id,
            };

            var hitData = new HitData
            {
                HitPoint = hitPoint, HitDirection = hitDirection, Force = _instantShotConfig.HitForce,
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
