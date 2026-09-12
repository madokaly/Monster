using System;
using System.Collections.Generic;
using Framework.Core;
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

        [Tooltip("弹道预制体（本地简易子实体，挂 MonsterProjectile）")]
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

        [Header("Projectile Sound")]
        [Tooltip("弹道飞行音效路径数组（随机其一，各端本地播，音源跟随本次生成的弹道；空 = 不播）")]
        public string[] ProjectileSounds;

        [Header("Detection")]
        [Tooltip("伤害结算层（本端弹道命中判定层；仅目标权威端发送 ApplyDamage）")]
        public LayerMask DamageLayer;

        [Tooltip("判定胶囊半径（米）")]
        public float HitRadius = 2f;

        [Tooltip("判定胶囊总高度（米，含两端球冠；小于 2 × HitRadius 时按 2 × HitRadius 计算）")]
        public float HitHeight = 8f;

        [Tooltip("判定胶囊中心偏移（弹道本地空间，用于对齐视觉弹道中心）")]
        public Vector3 HitCenterOffset = new Vector3(0f, -2f, 3f);

        [Tooltip("命中后延迟销毁弹道（秒，给本端命中特效留时间）")]
        public float HitDespawnDelay = 0.3f;

        /// <summary> 步骤内容时长 = 最大飞行时长 + 命中反馈保留时长（弹道自身生命窗口） </summary>
        public override float Duration => MonsterProjectileTiming.GetFlightDuration(this) + Mathf.Max(0f, HitDespawnDelay);
    }

    /// <summary>
    /// 弹道步骤：Enter 时各端本地实例化弹道（本地简易子实体），
    /// 由弹道按本端时钟自行飞行与命中表现；伤害只在受击方权威端经 Msger 结算。
    /// 本步骤持引用管理弹道生命周期（施法中断 / Dispose 清理飞行实例，已结束实例等待延迟销毁）。
    /// 直线弹道版；抛物线 / 落地爆炸变体等真实需求出现再泛化（YAGNI）。
    /// </summary>
    public class MonsterProjectileStep : MonsterSkillStep
    {
        private readonly MonsterProjectileStepConfig _projectileConfig;

        /// <summary> 存活的弹道（含已结束待延迟销毁的反馈实例，Dispose 时统一回收） </summary>
        private readonly List<MonsterProjectile> _liveProjectiles = new();

        public MonsterProjectileStep(MonsterModel model, MonsterProjectileStepConfig config)
            : base(model, config)
        {
            _projectileConfig = config;
        }

        protected override void OnStepDispose()
        {
            // 父实体销毁 → 清理全部本地弹道与命中反馈
            DestroyAllProjectiles();
        }

        protected override void OnStepExit()
        {
            // 重叠守卫会在后续步骤进入时截断本步骤窗口；弹道是步骤持有的 的本地生成物，
            // 允许跨重叠步骤窗口继续本端飞行，清理统一挂到施法中断 / 父实体销毁。
        }

        protected override void OnStepCastAborted()
        {
            // 施法结束 / 中断：停止仍在飞行的弹道；已结束弹道保留命中反馈至延迟销毁
            for (int i = _liveProjectiles.Count - 1; i >= 0; i--)
            {
                var projectile = _liveProjectiles[i];
                if (projectile != null && !projectile.FlightEnded)
                {
                    UnityEngine.Object.Destroy(projectile.gameObject);
                }
            }
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_projectileConfig is null) return;

            RemoveDestroyedProjectiles();

            var selfTransform = _projectileConfig.SelfTransform;
            if (selfTransform == null || _projectileConfig.ProjectilePrefab == null)
            {
                Logging.Error("[MonsterProjectileStep] OnStepEnter: SelfTransform / ProjectilePrefab 为空，请检查 Inspector 引用。");
                return;
            }

            // 各端用本端姿态与目标副本解析发射方向；轨迹只服务本端表现与受击方本地结算
            Vector3 spawnPos = MonsterProjectileTiming.GetSpawnPosition(_projectileConfig);
            bool hasTarget = TryGetTargetPosition(_model.CastTargetId, out Vector3 aimPos);
            Vector3 direction = MonsterProjectileTiming.ResolveDirection(
                _projectileConfig,
                spawnPos,
                hasTarget,
                aimPos
            );

            var projectileObj = UnityEngine.Object.Instantiate(
                _projectileConfig.ProjectilePrefab,
                spawnPos,
                Quaternion.LookRotation(direction)
            );

            if (!projectileObj.TryGetComponent(out MonsterProjectile projectile))
            {
                Logging.Error(
                    $"[MonsterProjectileStep] OnStepEnter: ProjectilePrefab 缺 MonsterProjectile 组件 ({_projectileConfig.ProjectilePrefab.name})。"
                );
                UnityEngine.Object.Destroy(projectileObj);
                return;
            }

            projectile.Initialize(
                direction,
                MonsterProjectileTiming.GetSpeed(_projectileConfig),
                MonsterProjectileTiming.GetMaxDistance(_projectileConfig),
                Mathf.Max(1, _projectileConfig.Damage),
                _model.Id,
                _projectileConfig.DamageLayer,
                MonsterProjectileTiming.GetHitRadius(_projectileConfig),
                MonsterProjectileTiming.GetHitHeight(_projectileConfig),
                _projectileConfig.HitCenterOffset,
                Mathf.Max(0f, _projectileConfig.HitDespawnDelay)
            );

            MonsterSkillPresentation.PlayRandomSound(_projectileConfig.ProjectileSounds, projectile.transform);
            _liveProjectiles.Add(projectile);
        }

        #region Private Methods

        private void RemoveDestroyedProjectiles()
        {
            for (int i = _liveProjectiles.Count - 1; i >= 0; i--)
            {
                if (_liveProjectiles[i] == null) _liveProjectiles.RemoveAt(i);
            }
        }

        private void DestroyAllProjectiles()
        {
            for (int i = 0; i < _liveProjectiles.Count; i++)
            {
                if (_liveProjectiles[i] != null)
                {
                    UnityEngine.Object.Destroy(_liveProjectiles[i].gameObject);
                }
            }

            _liveProjectiles.Clear();
        }

        #endregion
    }
}



