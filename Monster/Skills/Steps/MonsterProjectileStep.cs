using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Network;
using Fusion;
using Game.Components;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterProjectileStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("发射原点（怪物自身 Transform）")]
        public Transform SelfTransform;

        [Tooltip("弹道预制体（网络子实体·简易形态，挂 MonsterProjectile）")]
        public GameObject ProjectilePrefab;

        [Tooltip("发射位置偏移（相对怪物朝向）")]
        public Vector3 SpawnOffset = Vector3.zero;

        [Header("Projectile")]
        [Tooltip("弹道飞行速度（米/秒）")]
        public float ProjectileSpeed = 20f;

        [Tooltip("弹道最大飞行距离（超出自毁）")]
        public float MaxDistance = 50f;

        [Tooltip("弹道命中伤害")]
        public int Damage = 1;

        [Header("Detection")]
        [Tooltip("伤害结算层（弹道命中判定层，对层内目标发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("命中后延迟销毁弹道（秒，给各端命中特效留时间）")]
        public float HitDespawnDelay = 0.3f;
    }

    /// <summary>
    /// 弹道步骤（§16.4，点式）：Enter 时（权威端）生成弹道网络子实体（§0.1.2 简易形态），
    /// 由弹道权威端自行飞行判定，命中经 MsgID.ApplyDamage 出伤害；
    /// 本步骤持引用全权管理弹道生命周期（Dispose / 步骤退出清理）。
    /// 直线弹道版；抛物线 / 落地爆炸变体等真实需求出现再泛化（YAGNI）。
    /// </summary>
    public class MonsterProjectileStep : MonsterSkillStep
    {
        private readonly MonsterProjectileStepConfig _projectileConfig;

        /// <summary> 存活的弹道（权威端维护，Dispose / Exit 时清理） </summary>
        private readonly List<NetworkObject> _liveProjectiles = new();

        public MonsterProjectileStep(MonsterModel model, MonsterProjectileStepConfig config)
            : base(model, config)
        {
            _projectileConfig = config;
        }

        protected override void OnStepDispose()
        {
            // 父实体销毁 → 清理全部存活子实体（§0.1.2 生命周期契约）
            DespawnAllProjectiles();
        }

        protected override void OnStepExit()
        {
            // 施法结束 / 打断：清理存活弹道（保守收口）
            DespawnAllProjectiles();
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_projectileConfig is null) return;
            if (!HasStateAuthority) return;

            SpawnProjectileAsync().Forget();
        }

        #region Private Methods

        private async UniTaskVoid SpawnProjectileAsync()
        {
            if (_projectileConfig.ProjectilePrefab == null)
            {
                Logging.Error("[MonsterProjectileStep] SpawnProjectileAsync: ProjectilePrefab 为 null，请检查 Inspector 引用。");
                return;
            }

            var selfTransform = _projectileConfig.SelfTransform;
            if (selfTransform == null) return;

            // 发射参数：权威端本地解析目标位置（即用即弃）
            Vector3 spawnPos = selfTransform.position
                               + selfTransform.rotation * _projectileConfig.SpawnOffset;
            Vector3 aimPos = spawnPos + selfTransform.forward;
            if (_model.CastTargetId.IsValid)
            {
                var targetObject = NetworkMgr.FindObject(_model.CastTargetId.NetId);
                if (targetObject != null) aimPos = targetObject.transform.position;
            }

            Vector3 direction = aimPos - spawnPos;
            if (direction.sqrMagnitude < 0.001f) direction = selfTransform.forward;
            direction = direction.normalized;

            float speed = _projectileConfig.ProjectileSpeed > 0f ? _projectileConfig.ProjectileSpeed : 20f;
            float maxDistance = _projectileConfig.MaxDistance > 0f ? _projectileConfig.MaxDistance : 50f;

            var projectileObj = await NetworkMgr.SpawnAsync(
                _projectileConfig.ProjectilePrefab,
                spawnPos,
                Quaternion.LookRotation(direction),
                onBeforeSpawned: (runner, netObj) =>
                {
                    // Spawn 前预初始化（仅权威端写入 [Networked] 首帧状态）
                    if (netObj.TryGetComponent(out MonsterProjectile projectile))
                    {
                        projectile.PreInit(
                            direction,
                            speed,
                            maxDistance,
                            Mathf.Max(1, _projectileConfig.Damage),
                            _model.Id,
                            _model.CastTargetId,
                            _projectileConfig.DamageLayer
                        );
                    }
                }
            );

            if (projectileObj == null)
            {
                Logging.Error("[MonsterProjectileStep] SpawnProjectileAsync: 弹道生成失败");
                return;
            }

            _liveProjectiles.Add(projectileObj);

            // 父实体（本步骤权威端）监听弹道飞行结束事实，延迟销毁（给各端命中特效留时间）
            if (projectileObj.TryGetComponent(out MonsterProjectile projectileComp))
            {
                projectileComp.OnFlightEnded += OnProjectileFlightEnded;
            }
        }

        private void OnProjectileFlightEnded(MonsterProjectile projectile)
        {
            // 守卫用弹道自身权威而非怪物根对象权威：
            // 弹道 NetworkObject 的权威固定为生成端，怪物权威易主后原生成端仍负责其弹道销毁
            if (projectile == null || !projectile.HasStateAuthority) return;

            projectile.OnFlightEnded -= OnProjectileFlightEnded;

            var projectileObj = projectile.Object;
            if (projectileObj == null || !projectileObj.IsValid) return;

            _liveProjectiles.Remove(projectileObj);
            DespawnProjectileAsync(projectileObj, _projectileConfig.HitDespawnDelay).Forget();
        }

        private static async UniTaskVoid DespawnProjectileAsync(NetworkObject projectileObj, float delay)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(Mathf.Max(0f, delay)));

            if (projectileObj == null || !projectileObj.IsValid) return;
            NetworkMgr.Despawn(projectileObj);
        }

        private void DespawnAllProjectiles()
        {
            for (int i = _liveProjectiles.Count - 1; i >= 0; i--)
            {
                var projectile = _liveProjectiles[i];
                if (projectile != null && projectile.IsValid)
                {
                    NetworkMgr.Despawn(projectile);
                }
            }
            _liveProjectiles.Clear();
        }

        #endregion
    }
}
