using System;
using System.Collections.Generic;
using Framework;
using Framework.Core;
using Fusion;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterPerceptionModuleConfig
    {
        [Header("References")]
        [Tooltip("怪物自身 Transform（位置来源与 AOI 查询排除自己）")]
        public Transform SelfTransform;

        [Header("Filter")]
        /// <summary>
        /// AOI 过滤标签。
        /// 只有包含这些 AOI Tag 的实体才会进入候选集。
        /// 列表顺序即仇恨优先级顺序，例如：["Mecha", "Player"]。
        /// 目标命中多个 Tag 时，取优先级最高的那一个。
        /// </summary>
        [Tooltip("需要收集的 AOI 目标标签，按仇恨优先顺序排列")]
        public List<string> TargetAoiTags;

        [Header("Range")]
        /// <summary>
        /// 第一层：搜索范围。
        /// 表示 AI 触发战斗、锁定目标的基础圈，以怪物当前位置为原点。
        /// 0 或负数时回退到配置表 tbmonsterstats.detection_range。
        /// </summary>
        [Tooltip("搜索范围，触发战斗、锁定目标的基础圈，以怪物当前位置为原点")]
        public float SearchRange = 30f;

        /// <summary>
        /// 第二层：追击范围。
        /// 表示 AI 战斗进行中，仍允许继续追击的范围，以怪物当前位置为原点。
        /// 0 或负数时回退到配置表 tbmonsterstats.alert_range。
        /// </summary>
        [Tooltip("追击范围，战斗进行中允许继续追击的范围，以怪物当前位置为原点")]
        public float ChaseRange = 40f;

        [Header("Tick")]
        [Tooltip("感知刷新间隔（秒）")]
        public float RefreshInterval = 0.5f;
    }

    /// <summary>
    /// 怪物感知模块：探知附近的其他 AOI Target。
    /// 权威端节流重算三层范围（搜索 / 追击 / 脱战）内的最佳目标，
    /// 把结果加工为 EntityId 事实快照写入 Model（权威端派生数据，不 [Networked]）。
    /// 权威易主后由 AI 决策驱动重算，无需跨端对齐。
    /// </summary>
    public class MonsterPerceptionModule : ModuleBase
    {
        private readonly MonsterModel _model;
        private readonly MonsterPerceptionModuleConfig _config;

        /// <summary> AOI 查询结果缓存（查询后即用即弃，不跨帧持有）</summary>
        private readonly List<GameObject> _aoiBuffer = new();

        /// <summary> 脱战圈候选（出生点圆心）</summary>
        private readonly List<TargetCandidate> _outOfCombatCandidates = new();
        /// <summary> 追击圈候选（当前位置圆心）</summary>
        private readonly List<TargetCandidate> _chaseCandidates = new();
        /// <summary> 搜索圈候选（当前位置圆心）</summary>
        private readonly List<TargetCandidate> _searchCandidates = new();

        private float _refreshTimer;

        #region Lifecycle

        public MonsterPerceptionModule(MonsterModel model, MonsterPerceptionModuleConfig config)
        {
            _model = model;
            _config = config;

            // 注册 Model / Components 的事件监听
            RegisterModelListeners();
            RegisterComponentListeners();
        }

        protected override void OnDispose()
        {
            // 清空 Model / Components 的事件监听
            ClearModelListeners();
            ClearComponentListeners();
        }

        protected override void OnStateAuthorityChanged() { }

        protected override void OnFixedUpdateNetwork(float deltaTime)
        {
            if (_model is null) return;
            if (_config is null) return;
            if (!HasStateAuthority) return;

            _refreshTimer -= deltaTime;
            if (_refreshTimer > 0f) return;
            _refreshTimer = Mathf.Max(0.05f, _config.RefreshInterval);

            RefreshPerception();
        }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;
        }

        private void RegisterComponentListeners()
        {
            if (_config is null) return;
        }

        private void ClearComponentListeners()
        {
            if (_config is null) return;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 重算感知快照并写入 Model
        /// </summary>
        private void RefreshPerception()
        {
            if (_config.SelfTransform == null)
            {
                _model.SetPerceptionSnapshot(default);
                return;
            }

            if (_config.TargetAoiTags is null || _config.TargetAoiTags.Count == 0)
            {
                _model.SetPerceptionSnapshot(default);
                return;
            }

            Vector3 currentPos = _config.SelfTransform.position;
            Vector3 spawnPos = _model.PatrolCenter;

            float searchRange = ResolveSearchRange();
            float chaseRange = ResolveChaseRange();
            float disengageRadius = Mathf.Max(chaseRange, ResolveDisengageRadius());

            // AOI 查询：以怪物当前位置为中心，半径需同时覆盖
            //   - 当前位置 + chaseRange（追击圈）
            //   - 出生位置 + disengageRadius（脱战圈；通过出生点半径 + 当前到出生点距离折算）
            float spawnToCurrentDist = HorizontalDistance(currentPos, spawnPos);
            float aoiMaxRange = Mathf.Max(chaseRange, spawnToCurrentDist + disengageRadius);

            _aoiBuffer.Clear();
            AOIMgr.FindTargetsInRange(
                currentPos,
                aoiMaxRange,
                _aoiBuffer,
                _config.TargetAoiTags,
                _config.SelfTransform.gameObject
            );

            CollectTargets(currentPos, spawnPos, searchRange, chaseRange, disengageRadius);

            _model.SetPerceptionSnapshot(BuildSnapshot());
        }

        private float ResolveSearchRange()
        {
            if (_config.SearchRange > 0f) return _config.SearchRange;

            var cfg = _model.Cfg;
            return cfg != null && cfg.DetectionRange > 0 ? cfg.DetectionRange : 0f;
        }

        private float ResolveChaseRange()
        {
            if (_config.ChaseRange > 0f) return _config.ChaseRange;

            var cfg = _model.Cfg;
            return cfg != null && cfg.AlertRange > 0 ? cfg.AlertRange : 0f;
        }

        private float ResolveDisengageRadius()
        {
            return _model.DisengageRadius > 0f ? _model.DisengageRadius : ResolveChaseRange();
        }

        /// <summary>
        /// 按三层范围把 AOI 结果拆分到不同缓存。
        /// - Search ⊆ Chase：以怪物当前位置为圆心。
        /// - OutOfCombat（脱战圈）：以出生位置为圆心，disengageRadius 为半径。
        /// 硬性脱战边界：脱战圈已无目标时，即便内圈还有目标也不能继续锁定（视作无目标）。
        /// </summary>
        private void CollectTargets(
            Vector3 currentPos,
            Vector3 spawnPos,
            float searchRange,
            float chaseRange,
            float disengageRadius)
        {
            _searchCandidates.Clear();
            _chaseCandidates.Clear();
            _outOfCombatCandidates.Clear();

            float searchRangeSqr = searchRange * searchRange;
            float chaseRangeSqr = chaseRange * chaseRange;
            float disengageRadiusSqr = disengageRadius * disengageRadius;

            for (int i = 0; i < _aoiBuffer.Count; i++)
            {
                GameObject target = _aoiBuffer[i];
                if (target == null) continue;

                // 身份解析：即用即弃，只提取 EntityId（§1.6）
                EntityId entityId = ResolveTargetId(target);
                if (!entityId.IsValid) continue;

                Vector3 targetPos = target.transform.position;

                // 离当前怪物的距离（XZ 平面）
                Vector3 toCurrent = targetPos - currentPos;
                toCurrent.y = 0f;
                float distFromCurrentSqr = toCurrent.sqrMagnitude;

                // 离出生点的距离（XZ 平面）
                Vector3 toSpawn = targetPos - spawnPos;
                toSpawn.y = 0f;
                float distFromSpawnSqr = toSpawn.sqrMagnitude;

                var candidate = new TargetCandidate(entityId, distFromCurrentSqr, GetHatredPriority(target));

                // 脱战圈：以出生位置为圆心
                if (distFromSpawnSqr <= disengageRadiusSqr)
                {
                    _outOfCombatCandidates.Add(candidate);
                }

                // 追击圈：以当前位置为圆心
                if (distFromCurrentSqr <= chaseRangeSqr)
                {
                    _chaseCandidates.Add(candidate);
                }

                // 搜索圈：以当前位置为圆心
                if (distFromCurrentSqr <= searchRangeSqr)
                {
                    _searchCandidates.Add(candidate);
                }
            }
        }

        /// <summary>
        /// 由三层候选集构建主决策快照。
        /// 主决策范围：脱战圈空 → 无目标；否则 Search 优先、Chase 兜底。
        /// </summary>
        private MonsterPerceptionSnapshot BuildSnapshot()
        {
            var snapshot = new MonsterPerceptionSnapshot();

            // 硬性脱战边界：脱战圈已空 → 无目标
            if (_outOfCombatCandidates.Count == 0)
            {
                return snapshot;
            }

            List<TargetCandidate> currentRange;
            if (_searchCandidates.Count > 0)
            {
                snapshot.RangeType = 1;
                currentRange = _searchCandidates;
            }
            else if (_chaseCandidates.Count > 0)
            {
                snapshot.RangeType = 2;
                currentRange = _chaseCandidates;
            }
            else
            {
                // 仅脱战圈有目标：内圈追不上，视作脱战中
                snapshot.RangeType = 3;
                currentRange = _outOfCombatCandidates;
            }

            currentRange.Sort(CompareCandidate);

            var best = currentRange[0];
            snapshot.HasAnyTarget = true;
            snapshot.TargetCount = currentRange.Count;
            snapshot.BestTargetId = best.Id;
            snapshot.BestTargetDistance = Mathf.Sqrt(best.DistanceSqr);

            return snapshot;
        }

        /// <summary>
        /// 从目标 GameObject 解析网络实体 Id（即用即弃）
        /// </summary>
        private static EntityId ResolveTargetId(GameObject target)
        {
            var networkObject = target.GetComponentInParent<NetworkObject>();
            return networkObject != null ? networkObject.Id : default;
        }

        /// <summary>
        /// 根据目标身上的 AOI Tag 计算仇恨优先级（值越小优先级越高）。
        /// 未命中任何优先级标签时返回 int.MaxValue。
        /// </summary>
        private int GetHatredPriority(GameObject target)
        {
            if (!target.TryGetComponent<AOITarget>(out var aoiTarget)
                || aoiTarget.AOITags == null
                || aoiTarget.AOITags.Length == 0)
            {
                return int.MaxValue;
            }

            int bestPriority = int.MaxValue;
            for (int i = 0; i < _config.TargetAoiTags.Count; i++)
            {
                string priorityTag = _config.TargetAoiTags[i];
                if (string.IsNullOrWhiteSpace(priorityTag)) continue;

                for (int j = 0; j < aoiTarget.AOITags.Length; j++)
                {
                    if (aoiTarget.AOITags[j] == priorityTag)
                    {
                        bestPriority = Mathf.Min(bestPriority, i);
                        break;
                    }
                }
            }

            return bestPriority;
        }

        /// <summary>
        /// 候选排序：仇恨优先级优先，距离次之，实例 Id 稳定兜底
        /// </summary>
        private static int CompareCandidate(TargetCandidate left, TargetCandidate right)
        {
            int priorityCompare = left.Priority.CompareTo(right.Priority);
            if (priorityCompare != 0) return priorityCompare;

            int distanceCompare = left.DistanceSqr.CompareTo(right.DistanceSqr);
            if (distanceCompare != 0) return distanceCompare;

            int netIdCompare = left.Id.NetId.Raw.CompareTo(right.Id.NetId.Raw);
            if (netIdCompare != 0) return netIdCompare;
            return left.Id.LocalId.CompareTo(right.Id.LocalId);
        }

        /// <summary>
        /// 水平距离（XZ 平面）
        /// </summary>
        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        #endregion

        #region Data Structs

        /// <summary>
        /// 单个候选目标的轻量数据（只保留 EntityId 身份，不持有对象引用）
        /// </summary>
        private readonly struct TargetCandidate
        {
            public readonly EntityId Id;
            public readonly float DistanceSqr;
            public readonly int Priority;

            public TargetCandidate(EntityId id, float distanceSqr, int priority)
            {
                Id = id;
                DistanceSqr = distanceSqr;
                Priority = priority;
            }
        }

        #endregion
    }
}
