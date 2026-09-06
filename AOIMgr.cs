using System;
using System.Collections.Generic;
using UnityEngine;

namespace Framework.Core
{
    /// <summary>
    /// 基于网格分桶的 AOI 管理器
    /// </summary>
    public class AOIMgr : MonoSingleton<AOIMgr>
    {
        [Header("Grid")]
        [SerializeField]
        [Min(0.01f)]
        [Tooltip("网格边长")]
        private float _gridSize = 10f;

        [SerializeField]
        [Tooltip("世界边界最小值，仅用于可视化")]
        private Vector3 _worldMin = new(-1000f, 0f, -1000f);

        [SerializeField]
        [Tooltip("世界边界最大值，仅用于可视化")]
        private Vector3 _worldMax = new(1000f, 0f, 1000f);

        [Header("Performance")]
        [SerializeField]
        [Min(0f)]
        [Tooltip("位置同步间隔，0 表示每帧更新")]
        private float _updateInterval = 0.1f;

        [Header("Debug")]
        [SerializeField]
        [Tooltip("是否绘制 AOI Gizmos")]
        private bool _drawGizmos = true;

        [SerializeField]
        [Min(1)]
        [Tooltip("编辑器模式下预览的半径网格数")]
        private int _editorPreviewRange = 20;

        private readonly Dictionary<GridKey, GridCell> _gridCellMap = new();    // 网格字典
        private readonly Dictionary<GameObject, TargetInfo> _targetMap = new(); // 目标字典
        private readonly List<TargetInfo> _targetUpdateBuffer = new();          // Update 用的缓存（Update 中遍历时可能移除目标）
        private readonly List<TargetInfo> _staleTargetBuffer = new();           // 旧目标缓存（查找过程中如果发现空的旧目标，随手清理）

        private float _lastUpdateTime;

        #region Lifecycle

        protected override void OnInit()
        {
            NormalizeConfig();
        }

        protected override void OnDispose()
        {
            _gridCellMap.Clear();
            _targetMap.Clear();
            _targetUpdateBuffer.Clear();
            _staleTargetBuffer.Clear();
        }

        private void Update()
        {
            // 没有目标 或 未到间隔时间，跳过
            if (_targetMap.Count == 0 || !ShouldUpdate()) return;

            SyncTargets();
            _lastUpdateTime = Time.time;
        }

        private void OnValidate()
        {
            NormalizeConfig();
        }

        #endregion

        #region Registration

        /// <summary>
        /// 注册目标
        /// </summary>
        public static void RegisterTarget(AOITarget target)
        {
            if (target == null)
            {
                Logging.Warning("[AOIMgr] 注册 AOI 目标失败，AOITarget 为空");
                return;
            }

            RegisterTarget(target.gameObject, target.AOITags);
        }

        /// <summary>
        /// 注册目标
        /// </summary>
        public static void RegisterTarget(GameObject target, IReadOnlyList<string> tags)
        {
            if (target == null)
            {
                Logging.Warning("[AOIMgr] 注册 AOI 目标失败，GameObject 为空");
                return;
            }

            Instance.RegisterTargetInternal(target, tags);
        }

        /// <summary>
        /// 刷新目标
        /// </summary>
        public static void RefreshTarget(AOITarget target)
        {
            if (target == null)
            {
                Logging.Warning("[AOIMgr] 刷新 AOI 目标失败，AOITarget 为空");
                return;
            }

            RefreshTarget(target.gameObject, target.AOITags);
        }

        /// <summary>
        /// 刷新目标
        /// </summary>
        public static void RefreshTarget(GameObject target, IReadOnlyList<string> tags)
        {
            if (target == null)
            {
                Logging.Warning("[AOIMgr] 刷新 AOI 目标失败，GameObject 为空");
                return;
            }

            Instance.RefreshTargetInternal(target, tags);
        }

        /// <summary>
        /// 注销目标
        /// </summary>
        public static void UnregisterTarget(GameObject target)
        {
            if (!HasInstance || target == null)
            {
                return;
            }

            Instance.UnregisterTargetInternal(target);
        }

        /// <summary>
        /// 目标是否已经注册
        /// </summary>
        public static bool IsTargetRegistered(GameObject target)
        {
            return target != null && HasInstance && Instance._targetMap.ContainsKey(target);
        }

        #endregion

        #region Query

        /// <summary>
        /// 找到范围内的所有目标
        /// </summary>
        /// <param name="center">中心点位置</param>
        /// <param name="range">范围</param>
        /// <param name="tags">AOI 标签</param>
        /// <param name="excludeSelf">排除项</param>
        /// <returns>找到的目标列表</returns>
        public static List<GameObject> FindTargetsInRange(
            Vector3 center,
            float range,
            IReadOnlyList<string> tags = null,
            GameObject excludeSelf = null)
        {
            var results = new List<GameObject>();
            FindTargetsInRange(center, range, results, tags, excludeSelf);
            return results;
        }

        /// <summary>
        /// 找到范围内的所有目标
        /// </summary>
        /// <param name="center">中心点位置</param>
        /// <param name="range">范围</param>
        /// <param name="results">找到的目标列表</param>
        /// <param name="tags">AOI 标签</param>
        /// <param name="excludeSelf">排除项</param>
        public static void FindTargetsInRange(
            Vector3 center,
            float range,
            List<GameObject> results,
            IReadOnlyList<string> tags = null,
            GameObject excludeSelf = null)
        {
            if (results == null)
            {
                Logging.Warning("[AOIMgr] 范围查询失败，结果列表为空");
                return;
            }

            results.Clear();
            if (!HasInstance) return;

            Instance.FindTargetsInRangeInternal(center, range, results, tags, excludeSelf);
        }

        /// <summary>
        /// 找到包围盒内的所有目标
        /// </summary>
        /// <param name="bounds">包围盒（3D 判定，Y 也参与过滤）</param>
        /// <param name="tags">AOI 标签</param>
        /// <param name="excludeSelf">排除项</param>
        /// <returns>找到的目标列表</returns>
        public static List<GameObject> FindTargetsInBounds(
            Bounds bounds,
            IReadOnlyList<string> tags = null,
            GameObject excludeSelf = null)
        {
            var results = new List<GameObject>();
            FindTargetsInBounds(bounds, results, tags, excludeSelf);
            return results;
        }

        /// <summary>
        /// 找到包围盒内的所有目标
        /// </summary>
        /// <param name="bounds">包围盒（3D 判定，Y 也参与过滤）</param>
        /// <param name="results">找到的目标列表</param>
        /// <param name="tags">AOI 标签</param>
        /// <param name="excludeSelf">排除项</param>
        public static void FindTargetsInBounds(
            Bounds bounds,
            List<GameObject> results,
            IReadOnlyList<string> tags = null,
            GameObject excludeSelf = null)
        {
            if (results == null)
            {
                Logging.Warning("[AOIMgr] 包围盒查询失败，结果列表为空");
                return;
            }

            results.Clear();
            if (!HasInstance) return;

            Instance.FindTargetsInBoundsInternal(bounds, results, tags, excludeSelf);
        }

        /// <summary>
        /// 找到范围内最近的目标
        /// </summary>
        /// <param name="center">中心点位置</param>
        /// <param name="maxRange">最大查找范围</param>
        /// <param name="tags">AOI 标签</param>
        /// <param name="excludeSelf">排除项</param>
        /// <returns>找到的目标</returns>
        public static GameObject FindNearestTarget(
            Vector3 center,
            float maxRange,
            IReadOnlyList<string> tags = null,
            GameObject excludeSelf = null)
        {
            if (!HasInstance)
            {
                return null;
            }

            return Instance.FindNearestTargetInternal(center, maxRange, tags, excludeSelf);
        }

        /// <summary>
        /// 获取当前网格内的所有目标
        /// </summary>
        /// <param name="position">网格位置</param>
        /// <param name="tags">AOI 标签</param>
        /// <returns>找到的目标列表</returns>
        public static List<GameObject> GetTargetsInCell(Vector3 position, IReadOnlyList<string> tags = null)
        {
            var results = new List<GameObject>();
            GetTargetsInCell(position, results, tags);
            return results;
        }

        /// <summary>
        /// 获取当前网格内的所有目标
        /// </summary>
        /// <param name="position">网格位置</param>
        /// <param name="results">找到的目标列表</param>
        /// <param name="tags">AOI 标签</param>
        public static void GetTargetsInCell(
            Vector3 position,
            List<GameObject> results,
            IReadOnlyList<string> tags = null)
        {
            if (results == null)
            {
                Logging.Warning("[AOIMgr] 网格查询失败，结果列表为空");
                return;
            }

            results.Clear();
            if (!HasInstance)
            {
                return;
            }

            Instance.GetTargetsInCellInternal(position, results, tags);
        }

        #endregion

        #region Internal

        /// <summary>
        /// 内部实现：注册目标
        /// </summary>
        private void RegisterTargetInternal(GameObject target, IReadOnlyList<string> tags)
        {
            if (_targetMap.TryGetValue(target, out var targetInfo))
            {
                UpdateTargetTags(targetInfo, tags);
                SyncTargetCell(targetInfo, target.transform.position);
                return;
            }

            targetInfo = new TargetInfo(target, GetGridKey(target.transform.position));
            UpdateTargetTags(targetInfo, tags);

            _targetMap.Add(target, targetInfo);
            AddToCell(targetInfo);
        }

        /// <summary>
        /// 内部实现：刷新目标
        /// </summary>
        private void RefreshTargetInternal(GameObject target, IReadOnlyList<string> tags)
        {
            if (_targetMap.TryGetValue(target, out var targetInfo))
            {
                UpdateTargetTags(targetInfo, tags);
                SyncTargetCell(targetInfo, target.transform.position);
                return;
            }

            RegisterTargetInternal(target, tags);
        }

        /// <summary>
        /// 内部实现：注销目标
        /// </summary>
        private void UnregisterTargetInternal(GameObject target)
        {
            if (!_targetMap.TryGetValue(target, out var entityInfo))
            {
                return;
            }

            RemoveFromCell(entityInfo);
            _targetMap.Remove(target);
        }

        /// <summary>
        /// 同步目标信息
        /// </summary>
        private void SyncTargets()
        {
            _targetUpdateBuffer.Clear();
            _targetUpdateBuffer.AddRange(_targetMap.Values);

            for (int i = 0; i < _targetUpdateBuffer.Count; i++)
            {
                var entityInfo = _targetUpdateBuffer[i];
                if (!IsTargetValid(entityInfo))
                {
                    RemoveTarget(entityInfo);
                    continue;
                }

                SyncTargetCell(entityInfo, entityInfo.Transform.position);
            }
        }

        /// <summary>
        /// 同步目标所属网格信息
        /// </summary>
        private void SyncTargetCell(TargetInfo targetInfo, Vector3 position)
        {
            var nextCell = GetGridKey(position);
            if (!targetInfo.CurrentCell.Equals(nextCell))
            {
                RemoveFromCell(targetInfo);
                targetInfo.CurrentCell = nextCell;
                AddToCell(targetInfo);
            }

            targetInfo.LastPosition = position;
        }

        /// <summary>
        /// 内部实现：查找范围内的目标
        /// </summary>
        private void FindTargetsInRangeInternal(
            Vector3 center,
            float range,
            List<GameObject> results,
            IReadOnlyList<string> tags,
            GameObject excludeSelf)
        {
            if (range < 0f)
            {
                Logging.Warning("[AOIMgr] 范围查询失败，range 不能小于 0");
                return;
            }

            int gridRange = Mathf.CeilToInt(range / _gridSize);
            float rangeSqr = range * range;
            var centerCell = GetGridKey(center);

            for (int x = -gridRange; x <= gridRange; x++)
            {
                for (int z = -gridRange; z <= gridRange; z++)
                {
                    var gridKey = new GridKey(centerCell.X + x, centerCell.Z + z);
                    if (!_gridCellMap.TryGetValue(gridKey, out var cell))
                    {
                        continue;
                    }

                    CollectTargetsFromCell(cell, gridKey, center, rangeSqr, results, tags, excludeSelf);
                }
            }
        }

        /// <summary>
        /// 内部实现：查找包围盒内的目标（遍历 XZ 矩形覆盖的网格，逐目标 Bounds.Contains 精确过滤）
        /// </summary>
        private void FindTargetsInBoundsInternal(
            Bounds bounds,
            List<GameObject> results,
            IReadOnlyList<string> tags,
            GameObject excludeSelf)
        {
            var minKey = GetGridKey(new Vector3(bounds.min.x, 0f, bounds.min.z));
            var maxKey = GetGridKey(new Vector3(bounds.max.x, 0f, bounds.max.z));

            for (int x = minKey.X; x <= maxKey.X; x++)
            {
                for (int z = minKey.Z; z <= maxKey.Z; z++)
                {
                    var gridKey = new GridKey(x, z);
                    if (!_gridCellMap.TryGetValue(gridKey, out var cell))
                    {
                        continue;
                    }

                    CollectTargetsFromCellInBounds(cell, gridKey, bounds, results, tags, excludeSelf);
                }
            }
        }

        /// <summary>
        /// 内部实现：查找最近的目标
        /// </summary>
        private GameObject FindNearestTargetInternal(
            Vector3 center,
            float maxRange,
            IReadOnlyList<string> tags,
            GameObject excludeSelf)
        {
            if (maxRange < 0f)
            {
                Logging.Warning("[AOIMgr] 最近目标查询失败，maxRange 不能小于 0");
                return null;
            }

            GameObject nearestTarget = null;
            float nearestDistanceSqr = maxRange * maxRange;

            int gridRange = Mathf.CeilToInt(maxRange / _gridSize);
            var centerCell = GetGridKey(center);

            for (int x = -gridRange; x <= gridRange; x++)
            {
                for (int z = -gridRange; z <= gridRange; z++)
                {
                    var gridKey = new GridKey(centerCell.X + x, centerCell.Z + z);
                    if (!_gridCellMap.TryGetValue(gridKey, out var cell))
                    {
                        continue;
                    }

                    _staleTargetBuffer.Clear();

                    foreach (var entityInfo in cell.Targets)
                    {
                        if (!IsTargetValid(entityInfo))
                        {
                            _staleTargetBuffer.Add(entityInfo);
                            continue;
                        }

                        if (entityInfo.GameObject == excludeSelf)
                        {
                            continue;
                        }

                        if (!MatchesTags(entityInfo.Tags, tags))
                        {
                            continue;
                        }

                        float distanceSqr = (entityInfo.LastPosition - center).sqrMagnitude;
                        if (distanceSqr > nearestDistanceSqr)
                        {
                            continue;
                        }

                        nearestDistanceSqr = distanceSqr;
                        nearestTarget = entityInfo.GameObject;
                    }

                    CleanupStaleTargets(gridKey);
                }
            }

            return nearestTarget;
        }

        /// <summary>
        /// 内部实现：获取网格内的目标
        /// </summary>
        private void GetTargetsInCellInternal(Vector3 position, List<GameObject> results, IReadOnlyList<string> tags)
        {
            var gridKey = GetGridKey(position);
            if (!_gridCellMap.TryGetValue(gridKey, out var cell))
            {
                return;
            }

            _staleTargetBuffer.Clear();

            foreach (var entityInfo in cell.Targets)
            {
                if (!IsTargetValid(entityInfo))
                {
                    _staleTargetBuffer.Add(entityInfo);
                    continue;
                }

                if (!MatchesTags(entityInfo.Tags, tags))
                {
                    continue;
                }

                results.Add(entityInfo.GameObject);
            }

            CleanupStaleTargets(gridKey);
        }

        /// <summary>
        /// 收集网格内的所有目标
        /// </summary>
        private void CollectTargetsFromCell(
            GridCell cell,
            GridKey gridKey,
            Vector3 center,
            float rangeSqr,
            List<GameObject> results,
            IReadOnlyList<string> tags,
            GameObject excludeSelf)
        {
            _staleTargetBuffer.Clear();

            foreach (var entityInfo in cell.Targets)
            {
                if (!IsTargetValid(entityInfo))
                {
                    _staleTargetBuffer.Add(entityInfo);
                    continue;
                }

                if (entityInfo.GameObject == excludeSelf)
                {
                    continue;
                }

                if (!MatchesTags(entityInfo.Tags, tags))
                {
                    continue;
                }

                float distanceSqr = (entityInfo.LastPosition - center).sqrMagnitude;
                if (distanceSqr <= rangeSqr)
                {
                    results.Add(entityInfo.GameObject);
                }
            }

            CleanupStaleTargets(gridKey);
        }

        /// <summary>
        /// 收集网格内位于包围盒中的目标
        /// </summary>
        private void CollectTargetsFromCellInBounds(
            GridCell cell,
            GridKey gridKey,
            Bounds bounds,
            List<GameObject> results,
            IReadOnlyList<string> tags,
            GameObject excludeSelf)
        {
            _staleTargetBuffer.Clear();

            foreach (var entityInfo in cell.Targets)
            {
                if (!IsTargetValid(entityInfo))
                {
                    _staleTargetBuffer.Add(entityInfo);
                    continue;
                }

                if (entityInfo.GameObject == excludeSelf)
                {
                    continue;
                }

                if (!MatchesTags(entityInfo.Tags, tags))
                {
                    continue;
                }

                if (bounds.Contains(entityInfo.LastPosition))
                {
                    results.Add(entityInfo.GameObject);
                }
            }

            CleanupStaleTargets(gridKey);
        }

        /// <summary>
        /// 清除旧目标
        /// </summary>
        private void CleanupStaleTargets(GridKey gridKey)
        {
            if (_staleTargetBuffer.Count == 0) return;

            for (int i = 0; i < _staleTargetBuffer.Count; i++)
            {
                RemoveTarget(_staleTargetBuffer[i]);
            }

            _staleTargetBuffer.Clear();
        }

        /// <summary>
        /// 移除目标
        /// </summary>
        private void RemoveTarget(TargetInfo targetInfo)
        {
            if (targetInfo == null) return;

            RemoveFromCell(targetInfo);

            if (targetInfo.GameObject != null)
            {
                _targetMap.Remove(targetInfo.GameObject);
            }
            else
            {
                RemoveNullKeyEntity(targetInfo);
            }
        }

        /// <summary>
        /// 空键值的目标移除方法
        /// </summary>
        private void RemoveNullKeyEntity(TargetInfo targetInfo)
        {
            GameObject targetKey = null;
            bool found = false;

            foreach (var pair in _targetMap)
            {
                if (ReferenceEquals(pair.Value, targetInfo))
                {
                    targetKey = pair.Key;
                    found = true;
                    break;
                }
            }

            if (found)
            {
                _targetMap.Remove(targetKey);
            }
        }

        /// <summary>
        /// 把目标添加到网格
        /// </summary>
        private void AddToCell(TargetInfo targetInfo)
        {
            if (!_gridCellMap.TryGetValue(targetInfo.CurrentCell, out var cell))
            {
                cell = new GridCell();
                _gridCellMap.Add(targetInfo.CurrentCell, cell);
            }

            cell.Targets.Add(targetInfo);
        }

        /// <summary>
        /// 从网格移除目标
        /// </summary>
        private void RemoveFromCell(TargetInfo targetInfo)
        {
            if (!_gridCellMap.TryGetValue(targetInfo.CurrentCell, out var cell))
            {
                return;
            }

            cell.Targets.Remove(targetInfo);
            if (cell.Targets.Count == 0)
            {
                _gridCellMap.Remove(targetInfo.CurrentCell);
            }
        }

        /// <summary>
        /// 根据位置获得该位置所在的网格
        /// </summary>
        /// <param name="position">位置</param>
        /// <returns>网格键值</returns>
        private GridKey GetGridKey(Vector3 position)
        {
            int x = Mathf.FloorToInt(position.x / _gridSize);
            int z = Mathf.FloorToInt(position.z / _gridSize);
            return new GridKey(x, z);
        }

        /// <summary>
        /// 目标是否有效
        /// </summary>
        private static bool IsTargetValid(TargetInfo targetInfo)
        {
            return targetInfo != null && targetInfo.GameObject != null && targetInfo.Transform != null;
        }

        /// <summary>
        /// 匹配标签
        /// </summary>
        /// <param name="targetTags">目标的标签</param>
        /// <param name="queryTags">请求的标签</param>
        /// <returns>是否匹配</returns>
        private static bool MatchesTags(HashSet<string> targetTags, IReadOnlyList<string> queryTags)
        {
            if (queryTags == null || queryTags.Count == 0)
            {
                return true;
            }

            for (int i = 0; i < queryTags.Count; i++)
            {
                string tag = queryTags[i];
                if (string.IsNullOrWhiteSpace(tag)) continue;

                if (targetTags.Contains(tag)) return true;
            }

            return false;
        }

        /// <summary>
        /// 更新目标 Tags
        /// </summary>
        private static void UpdateTargetTags(TargetInfo targetInfo, IReadOnlyList<string> tags)
        {
            targetInfo.Tags.Clear();

            if (tags == null || tags.Count == 0)
            {
                targetInfo.Tags.Add(AOITarget.DEFAULT_AOI_TAG);
                return;
            }

            for (int i = 0; i < tags.Count; i++)
            {
                string tag = tags[i];
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    targetInfo.Tags.Add(tag);
                }
            }

            if (targetInfo.Tags.Count == 0)
            {
                targetInfo.Tags.Add(AOITarget.DEFAULT_AOI_TAG);
            }
        }

        private bool ShouldUpdate()
        {
            if (_updateInterval <= 0f) return true;
            return Time.time - _lastUpdateTime >= _updateInterval;
        }

        /// <summary>
        /// 规整化配置，防止填入的配置超出边界值
        /// </summary>
        private void NormalizeConfig()
        {
            _gridSize = Mathf.Max(0.01f, _gridSize);
            _updateInterval = Mathf.Max(0f, _updateInterval);
            _editorPreviewRange = Mathf.Max(1, _editorPreviewRange);

            Vector3 rawMin = _worldMin;
            Vector3 rawMax = _worldMax;
            _worldMin = Vector3.Min(rawMin, rawMax);
            _worldMax = Vector3.Max(rawMin, rawMax);
        }

        #endregion

        #region Gizmos

        private void OnDrawGizmos()
        {
            if (!_drawGizmos)
            {
                return;
            }

            if (!Application.isPlaying)
            {
                DrawEditorGrid();
                return;
            }

            DrawRuntimeGrid();
            DrawRuntimeEntities();
        }

        private void DrawEditorGrid()
        {
            Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.25f);

            int minX = Mathf.FloorToInt(_worldMin.x / _gridSize);
            int maxX = Mathf.CeilToInt(_worldMax.x / _gridSize);
            int minZ = Mathf.FloorToInt(_worldMin.z / _gridSize);
            int maxZ = Mathf.CeilToInt(_worldMax.z / _gridSize);

            minX = Mathf.Max(minX, -_editorPreviewRange);
            maxX = Mathf.Min(maxX, _editorPreviewRange);
            minZ = Mathf.Max(minZ, -_editorPreviewRange);
            maxZ = Mathf.Min(maxZ, _editorPreviewRange);

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Vector3 center = GetCellCenter(new GridKey(x, z));
                    center.y = transform.position.y;
                    Gizmos.DrawWireCube(center, new Vector3(_gridSize, 0.1f, _gridSize));
                }
            }

            Gizmos.color = Color.red;
            Vector3 worldCenter = (_worldMin + _worldMax) * 0.5f;
            Vector3 worldSize = _worldMax - _worldMin;
            worldCenter.y = transform.position.y;
            worldSize.y = 0.1f;
            Gizmos.DrawWireCube(worldCenter, worldSize);

#if UNITY_EDITOR
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 2f,
                $"Grid Size: {_gridSize}\nUpdate Interval: {_updateInterval}s"
            );
#endif
        }

        private void DrawRuntimeGrid()
        {
            foreach (var pair in _gridCellMap)
            {
                int entityCount = pair.Value.Targets.Count;
                float intensity = Mathf.Clamp01(entityCount / 10f);
                Gizmos.color = Color.Lerp(Color.gray, Color.yellow, intensity);
                Gizmos.DrawWireCube(GetCellCenter(pair.Key), new Vector3(_gridSize, 0.1f, _gridSize));

#if UNITY_EDITOR
                if (entityCount > 0)
                {
                    UnityEditor.Handles.Label(GetCellCenter(pair.Key) + Vector3.up, entityCount.ToString());
                }
#endif
            }
        }

        private void DrawRuntimeEntities()
        {
            foreach (var entityInfo in _targetMap.Values)
            {
                if (!IsTargetValid(entityInfo))
                {
                    continue;
                }

                Gizmos.color = GetEntityColor(entityInfo.Tags);
                Gizmos.DrawSphere(entityInfo.LastPosition, 0.3f);
            }

#if UNITY_EDITOR
            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 3f,
                $"Targets: {_targetMap.Count}\nActive Grids: {_gridCellMap.Count}\nGrid Size: {_gridSize}"
            );
#endif
        }

        private Vector3 GetCellCenter(GridKey gridKey)
        {
            return new Vector3(gridKey.X * _gridSize + _gridSize * 0.5f, 0f, gridKey.Z * _gridSize + _gridSize * 0.5f);
        }

        private static Color GetEntityColor(HashSet<string> tags)
        {
            if (tags.Contains("Player"))
            {
                return Color.blue;
            }

            if (tags.Contains("Monster"))
            {
                return Color.red;
            }

            if (tags.Contains("Mecha"))
            {
                return Color.green;
            }

            return Color.white;
        }

        #endregion

        #region Data

        /// <summary>
        /// 网格键值，唯一代表网格
        /// </summary>
        private readonly struct GridKey : IEquatable<GridKey>
        {
            public readonly int X;
            public readonly int Z;

            public GridKey(int x, int z)
            {
                X = x;
                Z = z;
            }

            public bool Equals(GridKey other)
            {
                return X == other.X && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is GridKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (X * 397) ^ Z;
                }
            }
        }

        /// <summary>
        /// 网格
        /// </summary>
        private sealed class GridCell
        {
            /// <summary>
            /// 网格包含的所有目标
            /// </summary>
            public readonly HashSet<TargetInfo> Targets = new();
        }

        /// <summary>
        /// 目标信息
        /// </summary>
        private sealed class TargetInfo
        {
            public readonly GameObject GameObject;
            public readonly Transform Transform;
            public readonly HashSet<string> Tags = new(StringComparer.Ordinal);

            public Vector3 LastPosition;
            public GridKey CurrentCell;

            public TargetInfo(GameObject gameObject, GridKey currentCell)
            {
                GameObject = gameObject;
                Transform = gameObject.transform;
                LastPosition = Transform.position;
                CurrentCell = currentCell;
            }
        }

        #endregion
    }
}