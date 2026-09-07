using System;
using System.Collections.Generic;
using Framework;
using Framework.Core;
using Game.Components;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterBombingStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("定位圆心（怪物自身 Transform；音效挂点）")]
        public Transform SelfTransform;

        [Header("Targeting")]
        [Tooltip("目标定位半径（以怪物为圆心 OverlapSphere 选目标）")]
        public float LocateRadius = 30f;

        [Tooltip("最多锁定目标数")]
        public int MaxTargets = 4;

        [Tooltip("追踪圈跟随时长（秒）")]
        public float TrackDuration = 4f;

        [Tooltip("锁定到第一发导弹的延迟（秒）")]
        public float LockDelay = 0.5f;

        [Header("Bombing")]
        [Tooltip("轰炸总段数")]
        public int BombWaves = 3;

        [Tooltip("两段轰炸之间的间隔（秒）")]
        public float BombInterval = 1f;

        [Tooltip("导弹坠落时长（秒；权威结算时刻 = 导弹生成时刻 + 本值）")]
        public float BombFallDuration = 3f;

        [Tooltip("单段爆炸伤害半径（米）")]
        public float ExplosionRadius = 5f;

        [Header("Damage")]
        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("单段爆炸伤害")]
        public int Damage = 1;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 5f;

        [Header("Effects")]
        [Tooltip("追踪圈特效 prefab（纯表现，各端本地实例化于目标脚下并跟随）")]
        public GameObject TrackingEffectPrefab;
        public Vector3 TrackingEffectPosOffset = Vector3.zero;
        public Vector3 TrackingEffectRotOffset = Vector3.zero;

        [Tooltip("锁定标记特效 prefab（纯表现，锁定时刻实例化于锁定位置）")]
        public GameObject LockEffectPrefab;
        public Vector3 LockEffectPosOffset = Vector3.zero;
        public Vector3 LockEffectRotOffset = Vector3.zero;

        [Tooltip("导弹 + 爆炸特效 prefab（纯表现，自带下落编排与爆炸音效；各端本地在导弹生成时刻实例化）")]
        public GameObject BombEffectPrefab;
        public Vector3 BombEffectPosOffset = Vector3.zero;
        public Vector3 BombEffectRotOffset = Vector3.zero;

        [Header("Sounds")]
        [Tooltip("追踪瞄准音效（空 = 不播）")]
        public string TrackingSoundPath;

        [Tooltip("锁定瞄准音效（空 = 不播）")]
        public string LockedSoundPath;

        [Tooltip("导弹发射音效（空 = 不播）")]
        public string MissileSoundPath;

        public override float Duration =>
            TrackDuration + LockDelay + Mathf.Max(0, BombWaves - 1) * BombInterval + BombFallDuration;
    }

    /// <summary>
    /// 定点轰炸步骤：定位附近目标 → 追踪圈跟随 → 锁定 → 多段轰炸（圆心在锁定位置）。
    /// 目标选择与锁定位置各端本地解析；各端在爆炸时刻按本端锁定位置逐段结算
    /// （受击方本地结算 §1.7——判定基准与本端轰炸表现同源，所见即所得，每段每目标一次）；
    /// 追踪圈跟随 / 锁定标记 / 导弹特效各端本地时钟播放。
    /// </summary>
    public class MonsterBombingStep : MonsterSkillStep
    {
        private readonly MonsterBombingStepConfig _bombingConfig;

        /// <summary> 本端选择的目标（表现跟随 / 锁定位置解析来源；各端各自解析） </summary>
        private readonly List<EntityId> _targetIds = new();

        /// <summary> 锁定位置（本端解析；本端结算基准，§1.7） </summary>
        private readonly List<Vector3> _lockedPositions = new();

        /// <summary> 本端：追踪圈特效实例（与 _targetIds 对齐；目标丢失置 null） </summary>
        private readonly List<GameObject> _trackingEffects = new();

        /// <summary> 本端：锁定标记特效实例 </summary>
        private readonly List<GameObject> _lockEffects = new();

        /// <summary> 本端：导弹特效实例 </summary>
        private readonly List<GameObject> _bombEffects = new();

        private bool _locked;

        /// <summary> 本端：下一个待生成导弹的段索引 </summary>
        private int _nextBombSpawnIndex;

        /// <summary> 本端：下一个待结算的段索引 </summary>
        private int _nextBombSettleIndex;

        /// <summary> 定位碰撞体缓冲 </summary>
        private readonly Collider[] _locateBuffer = new Collider[32];

        public MonsterBombingStep(MonsterModel model, MonsterBombingStepConfig config) : base(model, config)
        {
            _bombingConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_bombingConfig is null) return;

            ResetAll();
            LocateTargets();
            SpawnTrackingEffects();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_bombingConfig is null) return;

            float stepElapsed = elapsed - _config.StartOffset;

            // 追踪期：特效跟随目标
            if (!_locked && stepElapsed < _bombingConfig.TrackDuration)
            {
                FollowTrackingEffects();
            }

            // 锁定时刻
            if (!_locked && stepElapsed >= _bombingConfig.TrackDuration)
            {
                DoLock();
            }

            // 导弹生成（各端）与爆炸结算（权威端）
            UpdateBombs(stepElapsed);
        }

        protected override void OnStepExit()
        {
            DestroyAllEffects();
            ResetAll();
        }

        #region Targeting

        /// <summary>
        /// 本端定位：以怪物为圆心 OverlapSphere 选至多 MaxTargets 个目标（身份解析即用即弃）。
        /// </summary>
        private void LocateTargets()
        {
            _targetIds.Clear();

            var self = _bombingConfig.SelfTransform;
            if (self == null) return;
            if (_bombingConfig.LocateRadius <= 0f) return;

            int count = Physics.OverlapSphereNonAlloc(
                self.position,
                _bombingConfig.LocateRadius,
                _locateBuffer,
                _bombingConfig.DamageLayer,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < count && _targetIds.Count < _bombingConfig.MaxTargets; i++)
            {
                var col = _locateBuffer[i];
                if (col == null) continue;

                var tag = col.GetComponentInParent<EntityTag>();
                if (tag == null) continue;

                EntityId targetId = tag.Id;
                if (!targetId.IsValid) continue;
                if (_targetIds.Contains(targetId)) continue;

                _targetIds.Add(targetId);
            }
        }

        /// <summary>
        /// 追踪圈特效：为每个定位目标实例化一个（跟随由 FollowTrackingEffects 逐 tick 驱动）。
        /// </summary>
        private void SpawnTrackingEffects()
        {
            _trackingEffects.Clear();

            if (_bombingConfig.TrackingEffectPrefab == null) return;

            for (int i = 0; i < _targetIds.Count; i++)
            {
                GameObject effect = null;
                if (TryGetTargetPosition(_targetIds[i], out var targetPos))
                {
                    effect = UnityEngine.Object.Instantiate(
                        _bombingConfig.TrackingEffectPrefab,
                        targetPos + _bombingConfig.TrackingEffectPosOffset,
                        Quaternion.Euler(_bombingConfig.TrackingEffectRotOffset)
                    );
                    if (!string.IsNullOrEmpty(_bombingConfig.TrackingSoundPath))
                    {
                        AudioMgr.Play(_bombingConfig.TrackingSoundPath, effect.transform);
                    }
                }
                _trackingEffects.Add(effect);
            }
        }

        /// <summary>
        /// 追踪期逐 tick：特效跟随目标；目标丢失（脱战 / 死亡）销毁圈。
        /// </summary>
        private void FollowTrackingEffects()
        {
            for (int i = 0; i < _trackingEffects.Count; i++)
            {
                var effect = _trackingEffects[i];
                if (effect == null) continue;

                if (i < _targetIds.Count && TryGetTargetPosition(_targetIds[i], out var targetPos))
                {
                    effect.transform.position = targetPos + _bombingConfig.TrackingEffectPosOffset;
                }
                else
                {
                    UnityEngine.Object.Destroy(effect);
                    _trackingEffects[i] = null;
                }
            }
        }

        /// <summary>
        /// 锁定时刻（各端本地）：记录各目标当前位置为锁定位置（目标丢失跳过）、
        /// 销毁追踪圈、播锁定标记与瞄准音效。
        /// </summary>
        private void DoLock()
        {
            _locked = true;

            _lockedPositions.Clear();
            foreach (var targetId in _targetIds)
            {
                if (TryGetTargetPosition(targetId, out var targetPos))
                {
                    _lockedPositions.Add(targetPos);
                }
            }

            for (int i = 0; i < _trackingEffects.Count; i++)
            {
                if (_trackingEffects[i] != null)
                {
                    UnityEngine.Object.Destroy(_trackingEffects[i]);
                    _trackingEffects[i] = null;
                }
            }

            if (_bombingConfig.LockEffectPrefab != null)
            {
                _lockEffects.Clear();
                for (int i = 0; i < _lockedPositions.Count; i++)
                {
                    var lockEffect = UnityEngine.Object.Instantiate(
                        _bombingConfig.LockEffectPrefab,
                        _lockedPositions[i] + _bombingConfig.LockEffectPosOffset,
                        Quaternion.Euler(_bombingConfig.LockEffectRotOffset)
                    );
                    _lockEffects.Add(lockEffect);
                    if (!string.IsNullOrEmpty(_bombingConfig.LockedSoundPath))
                    {
                        AudioMgr.Play(_bombingConfig.LockedSoundPath, lockEffect.transform);
                    }
                }
            }
        }

        #endregion

        #region Bombing

        /// <summary>
        /// 导弹生成时刻 = 锁定 + LockDelay + 段索引 × BombInterval；
        /// 爆炸结算时刻 = 导弹生成时刻 + BombFallDuration。
        /// </summary>
        private void UpdateBombs(float stepElapsed)
        {
            // 各端本地：导弹特效生成 + 发射音效
            while (_nextBombSpawnIndex < _bombingConfig.BombWaves
                   && stepElapsed >= MonsterBombingTiming.GetBombSpawnTime(_bombingConfig, _nextBombSpawnIndex))
            {
                SpawnBombEffects();
                _nextBombSpawnIndex++;
            }

            // 各端：爆炸结算（受击方本地，§1.7，本端锁定位置为圆心）
            while (_nextBombSettleIndex < _bombingConfig.BombWaves
                   && stepElapsed >= MonsterBombingTiming.GetBombSettleTime(
                       _bombingConfig,
                       _nextBombSettleIndex
                   ))
            {
                SettleBomb();
                _nextBombSettleIndex++;
            }
        }

        /// <summary>
        /// 各端本地：为每个锁定位置实例化导弹特效（prefab 自带下落编排与爆炸音效）。
        /// </summary>
        private void SpawnBombEffects()
        {
            if (_bombingConfig.BombEffectPrefab == null) return;

            _bombEffects.Clear();
            for (int i = 0; i < _lockedPositions.Count; i++)
            {
                var bombEffect = UnityEngine.Object.Instantiate(
                    _bombingConfig.BombEffectPrefab,
                    _lockedPositions[i] + _bombingConfig.BombEffectPosOffset,
                    Quaternion.Euler(_bombingConfig.BombEffectRotOffset)
                );
                _bombEffects.Add(bombEffect);
                if (!string.IsNullOrEmpty(_bombingConfig.MissileSoundPath))
                {
                    AudioMgr.Play(_bombingConfig.MissileSoundPath, bombEffect.transform);
                }
            }
        }

        /// <summary>
        /// 单段结算（各端受击方本地结算，§1.7）：每个锁定位置一次球形结算，
        /// 共享去重集（每段每目标一次，多圈重叠去重），复用基类共享管线。
        /// </summary>
        private void SettleBomb()
        {
            var self = _bombingConfig.SelfTransform;
            if (self == null) return;

            var hitTargets = new HashSet<EntityId>();

            for (int i = 0; i < _lockedPositions.Count; i++)
            {
                SettleSphereDamage(
                    _lockedPositions[i],
                    _bombingConfig.ExplosionRadius,
                    _bombingConfig.DamageLayer,
                    0f,
                    _bombingConfig.Damage,
                    _bombingConfig.HitForce,
                    hitTargets,
                    self.forward
                );
            }
        }

        #endregion

        #region Private Methods

        private void DestroyAllEffects()
        {
            for (int i = 0; i < _trackingEffects.Count; i++)
            {
                if (_trackingEffects[i] != null) UnityEngine.Object.Destroy(_trackingEffects[i]);
            }

            for (int i = 0; i < _lockEffects.Count; i++)
            {
                if (_lockEffects[i] != null) UnityEngine.Object.Destroy(_lockEffects[i]);
            }

            for (int i = 0; i < _bombEffects.Count; i++)
            {
                if (_bombEffects[i] != null) UnityEngine.Object.Destroy(_bombEffects[i]);
            }

            _trackingEffects.Clear();
            _lockEffects.Clear();
            _bombEffects.Clear();
        }

        private void ResetAll()
        {
            _targetIds.Clear();
            _lockedPositions.Clear();
            _locked = false;
            _nextBombSpawnIndex = 0;
            _nextBombSettleIndex = 0;
        }

        #endregion
    }
}
