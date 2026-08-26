using System;
using System.Collections.Generic;
using Framework;
using Framework.Core;
using Framework.Network;
using Fusion;
using Game.Components;
using Pathfinding;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Game.Entities
{
    /// <summary>
    /// 怪物区域生成系统（世界系统·非实体，MonoBehaviour 形态，GameWorld 挂载到宿主节点，§0.1.5 / §15）。
    /// 仅 MasterClient 端执行世界决策（共享世界决策类世界系统的特性，§15.1）；
    /// 出生点数据全部来自 TbMonsterspwan（SpwanPosition / Radius / MonsterGroup），
    /// 占用检测与归属决策以副本 Region 为单位（CfgMonsterspwan.Region →
    /// Consts.GetRegionCenter / GetRegionBounds，经 AOI Bounds 查询）——出生点只决定
    /// 怪物在哪出生，副本才决定何时出生；无场景物体依赖，兼容副本场景按端动态加载。
    ///
    /// 职责（决策，不执行）：
    /// - 副本无人 → 有玩家进入副本 Bounds：开副本全部出生点的生成批次（各出生点并行），
    ///   owner = 距副本中心最近的玩家；
    /// - 当前权威归属玩家离开副本：副本还有其他玩家 → 权威转移给距副本中心最近的玩家；
    ///   无人 → 销毁副本全部怪物；
    /// - 死亡回收：监听 MonsterDied（死亡流程完成广播）→ 清槽位 + DestroyMonster（经 MonsterSystem
    ///   受理后路由至怪物权威端执行 Despawn，本系统只发命令）+ 按配置 debris 数组掉落碎骸
    ///   （MC 端发 CreateMonsterDebris → MonsterDebrisSystem 受理）；
    /// - MC 重选：槽位图由 MonsterSpawned / MonsterDespawned 事件全端维护不丢；
    ///   owner 经占用轮询重建后对全部存活怪物重发归属（default→owner 两连发强制事件）。
    /// 执行全部经总线：MsgID.CreateMonster / DestroyMonster / AssignMonsterAuthority → MonsterSystem 路由；
    /// 其中 DestroyMonster 由 System 受理后经 MonsterCtrl.RequestDespawn 跨端路由到怪物权威端执行（统一销毁模式 §8）。
    ///
    /// 生命周期：Update 内 deltaTime 累积驱动轮询（tick 判据见 §15.1）；
    /// Awake 注册监听 / OnEnable·OnDisable 起停 tick / OnDestroy 清理。
    /// </summary>
    public class MonsterSpawnerSystem : MonoBehaviour
    {
        /// <summary> 循环间隔（秒），与单怪生成间隔一致 </summary>
        private const float LOOP_INTERVAL = 0.5f;

        /// <summary> 占用轮询间隔（秒） </summary>
        private const float POLL_INTERVAL = 1f;

        /// <summary> 状态切换需要的连续稳定轮数（防进出抖动） </summary>
        private const int STABLE_ROUNDS = 2;

        /// <summary> 单个怪物生成间隔（秒） </summary>
        private const float SPAWN_INTERVAL = 0.5f;

        /// <summary> 出生点采样最大重试次数 </summary>
        private const int MAX_SPAWN_ATTEMPTS = 10;

        /// <summary> 碎骸生成高度（死亡点上方，米） </summary>
        private const float DEBRIS_SPAWN_HEIGHT = 1f;

        /// <summary> 碎骸散布半径（死亡点周围，米） </summary>
        private const float DEBRIS_SPREAD_RADIUS = 1.2f;

        /// <summary> 副本占用检测的玩家 AOI 标签（MainPlayer = Player，Mech = Mech） </summary>
        private static readonly string[] PLAYER_AOI_TAGS = { "Player", "Mech" };

        private readonly MsgerGroup _bus = new();

        /// <summary> 全部副本状态（regionId → 副本，占用 / 归属决策单位） </summary>
        private readonly Dictionary<int, RegionState> _regions = new();

        /// <summary> 全部出生点索引（spawnId → 出生点，事件槽位维护用 O(1) 归属） </summary>
        private readonly Dictionary<int, ZoneState> _zones = new();

        /// <summary> AOI 查询结果缓存（查询后即用即弃，不跨帧持有） </summary>
        private readonly List<GameObject> _aoiBuffer = new();

        /// <summary> 上一 tick 是否已是 MasterClient（MC 变更检测） </summary>
        private bool _wasMasterClient;

        /// <summary> 决策轮询累积计时（deltaTime 驱动，tick 判据见 §15.1） </summary>
        private float _loopTimer;

        #region Lifecycle

        private void Awake()
        {
            // 区域表全端急切构建：槽位缓存必须从首个 Spawned 事件起在所有端完整维护，
            // MC 重选后新 MC 才能直接沿用（§15.4.1，同 ExpStoneSpawnerSystem.InitGroups）
            InitRegions();
            RegisterListeners();
        }

        private void OnEnable()
        {
            _loopTimer = 0f;
        }

        private void OnDisable()
        {
            // 复位 MC 检测标记：重新启用后首 tick 视作 becameMaster，统一重建决策中间态
            _wasMasterClient = false;
        }

        private void OnDestroy()
        {
            _bus?.Clear();
        }

        /// <summary>
        /// 决策循环（各端实例常驻，仅 MC 端执行决策）。
        /// </summary>
        private void Update()
        {
            _loopTimer += Time.deltaTime;
            while (_loopTimer >= LOOP_INTERVAL)
            {
                _loopTimer -= LOOP_INTERVAL;
                TickOnce();
            }
        }

        /// <summary>
        /// 单次决策 tick（原 UniTask 循环体的 Update 化，语义不变）。
        /// </summary>
        private void TickOnce()
        {
            var runner = NetworkMgr.PhotonRunner;
            if (runner == null || !runner.IsRunning) return;

            if (!NetworkMgr.IsMasterClient)
            {
                _wasMasterClient = false;
                return;
            }

            bool becameMaster = !_wasMasterClient;
            _wasMasterClient = true;

            if (becameMaster)
            {
                // MC 重选：决策中间态重启（槽位图由事件维护不丢；owner 重建后重发归属）
                ResetRegionRuntimeStates();
            }

            foreach (var region in _regions.Values)
            {
                if (region.IsSpawning)
                {
                    TickSpawning(region);
                    continue;
                }

                region.PollTimer -= LOOP_INTERVAL;
                if (region.PollTimer > 0f) continue;
                region.PollTimer = POLL_INTERVAL;

                TickOccupancy(region);
            }
        }

        #endregion

        #region Registers

        private void RegisterListeners()
        {
            if (_bus is null) return;

            _bus.AddListener(MsgID.MonsterSpawned, OnMonsterSpawned);
            _bus.AddListener(MsgID.MonsterDespawned, OnMonsterDespawned);
            _bus.AddListener(MsgID.MonsterDied, OnMonsterDied);
        }

        #endregion

        #region Listeners

        private void OnMonsterSpawned(MsgID id, object data)
        {
            if (data is object[] { Length: 2 } datas && datas[0] is EntityId entityId && datas[1] is int spawnId)
            {
                if (!_zones.TryGetValue(spawnId, out var zone)) return;

                zone.SlotMap.Add(entityId);

                // 生成完成 → 立即把权威归属指定给所属副本当前 owner（仅 MC 端 owner 有效）
                if (zone.Region.CurrentOwner.IsValid)
                {
                    Msger.Send(MsgID.AssignMonsterAuthority, entityId, zone.Region.CurrentOwner);
                }

                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnMonsterDespawned(MsgID id, object data)
        {
            if (data is EntityId entityId)
            {
                foreach (var zone in _zones.Values)
                {
                    zone.SlotMap.Remove(entityId);
                }
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnMonsterDied(MsgID id, object data)
        {
            if (data is object[] { Length: 4 } datas
                && datas[0] is EntityId entityId
                && datas[1] is int cfgId
                && datas[2] is Vector3 position
                && datas[3] is Quaternion rotation)
            {
                // 全端实例都维护槽位（MC 重选不丢）；仅 MC 实例发起销毁与碎骸掉落
                bool removed = false;
                foreach (var zone in _zones.Values)
                {
                    if (zone.SlotMap.Remove(entityId))
                    {
                        removed = true;
                        break;
                    }
                }

                if (removed && NetworkMgr.IsMasterClient)
                {
                    Logging.Info($"[MonsterSpawnerSystem] 死亡流程完成，销毁怪物并掉落碎骸 ({entityId})");
                    Msger.Send(MsgID.DestroyMonster, entityId);

                    SpawnDebris(cfgId, position, rotation);
                }

                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        /// <summary>
        /// 掉落碎骸（死亡结算的延续）：按怪物配置 debris 数组全量生成，配置驱动（debris 为空不掉）。
        /// 每块碎骸在死亡点上方 DEBRIS_SPAWN_HEIGHT 米、360°/N 均分角度、DEBRIS_SPREAD_RADIUS 半径散布，
        /// 无初速度自由落体；创建命令经总线 → MonsterDebrisSystem 受理（资源与网络 Spawn 收口在目标 System）。
        /// </summary>
        private void SpawnDebris(int cfgId, Vector3 deathPosition, Quaternion deathRotation)
        {
            var monsterCfg = ConfigMgr.Tables.TbMonsterstats.GetOrDefault(cfgId);
            if (monsterCfg?.Debris is not { Length: > 0 }) return;

            int count = monsterCfg.Debris.Length;
            for (int i = 0; i < count; i++)
            {
                var entry = monsterCfg.Debris[i];
                if (entry == null) continue;

                float angle = 360f / count * i * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * DEBRIS_SPREAD_RADIUS;
                var position = deathPosition + Vector3.up * DEBRIS_SPAWN_HEIGHT + offset;

                var template = new MonsterDebrisTemplate(cfgId, entry.Type);
                Msger.Send(MsgID.CreateMonsterDebris, template, position, deathRotation);
            }
        }

        #endregion

        #region Occupancy

        /// <summary>
        /// 轮询副本占用：检测玩家集合 → 连续 STABLE_ROUNDS 轮稳定才切换状态。
        /// </summary>
        private void TickOccupancy(RegionState region)
        {
            DetectOccupants(region);

            bool changed = !region.CurrentOccupants.SetEquals(region.LastOccupants);
            if (changed)
            {
                // 最新检测结果成为新的基准，等待下一轮再检测
                region.LastOccupants.Clear();
                region.LastOccupants.UnionWith(region.CurrentOccupants);
                region.CurrentOccupants.Clear();
                region.StableRounds = 0;
                return;
            }

            region.StableRounds++;
            if (region.StableRounds < STABLE_ROUNDS) return;

            ApplyOccupancyState(region);
        }

        /// <summary>
        /// AOI Bounds 查询副本内的玩家集合（身份解析即用即弃，只存 EntityId）
        /// </summary>
        private void DetectOccupants(RegionState region)
        {
            region.CurrentOccupants.Clear();

            if (region.Bounds.size.sqrMagnitude <= 0f) return;

            AOIMgr.FindTargetsInBounds(region.Bounds, _aoiBuffer, PLAYER_AOI_TAGS);

            for (int i = 0; i < _aoiBuffer.Count; i++)
            {
                var target = _aoiBuffer[i];
                if (target == null) continue;

                // 身份解析：即用即弃，只提取 EntityId（§1.6）
                var entityTag = target.GetComponentInParent<EntityTag>();
                if (entityTag == null) continue;
                region.CurrentOccupants.Add(entityTag.Id);
            }
        }

        /// <summary>
        /// 应用稳定后的占用状态：
        /// 空 → 有人（首个玩家进入）：生成副本全部出生点的怪物，owner = 距副本中心最近者；
        /// 有人 → owner 离开：副本还有人 → 权威转移给最近者；无人 → 销毁副本全部怪物。
        /// （MC 重选后 owner 重建走同一分支，并重发归属）
        /// </summary>
        private void ApplyOccupancyState(RegionState region)
        {
            if (region.LastOccupants.Count == 0)
            {
                // 副本无人 → 销毁全部存活怪物
                int aliveCount = 0;
                foreach (var zone in region.Zones)
                {
                    aliveCount += zone.SlotMap.Count;
                }

                if (aliveCount > 0)
                {
                    Logging.Info($"[MonsterSpawnerSystem] regionId:{region.RegionId} 副本无人，销毁全部怪物");
                    foreach (var zone in region.Zones)
                    {
                        foreach (var entityId in new List<EntityId>(zone.SlotMap))
                        {
                            Msger.Send(MsgID.DestroyMonster, entityId);
                        }
                    }
                }

                region.CurrentOwner = default;

                // 复位生成中间态：批次中途清场时 IsSpawning 必须同步复位，
                // 否则再次有人进入时 StartSpawning 早退 + 旧批次超时 → 副本永久无怪
                region.IsSpawning = false;
                foreach (var zone in region.Zones)
                {
                    zone.IsSpawning = false;
                    zone.NextSpawnTime = 0f;
                }
                return;
            }

            if (!region.CurrentOwner.IsValid)
            {
                // 首个玩家进入（或 MC 重选后 owner 重建）→ 生成全部怪物，owner = 距副本中心最近者
                EntityId firstOwner = FindNearestOccupant(region, region.LastOccupants);
                region.CurrentOwner = firstOwner;

                // 对全部存活怪物重发归属（default→owner 两连发强制事件：同值 no-op 不触发，
                // 先归零再指派让各端 MonsterAuthorityModule 重新反应；MC 重选场景关键）
                ReassignAuthorityToAll(region, firstOwner);

                Logging.Info($"[MonsterSpawnerSystem] regionId:{region.RegionId} 首个玩家进入 ({firstOwner})，开始生成怪物");
                StartSpawning(region);
                return;
            }

            if (region.LastOccupants.Contains(region.CurrentOwner)) return; // owner 仍在，维持现状

            // owner 离开但副本还有人 → 权威转移给最近者
            EntityId newOwner = FindNearestOccupant(region, region.LastOccupants);
            region.CurrentOwner = newOwner;

            Logging.Info($"[MonsterSpawnerSystem] regionId:{region.RegionId} 权威归属转移 → {newOwner}");
            ReassignAuthorityToAll(region, newOwner);
        }

        /// <summary>
        /// 取距副本中心最近的占用玩家
        /// </summary>
        private EntityId FindNearestOccupant(RegionState region, HashSet<EntityId> occupants)
        {
            EntityId nearest = default;
            float nearestDistSqr = float.MaxValue;

            foreach (var entityId in occupants)
            {
                var networkObject = NetworkMgr.FindObject(entityId.NetId);
                if (networkObject == null) continue;

                float distSqr = (networkObject.transform.position - region.Center).sqrMagnitude;
                if (distSqr < nearestDistSqr)
                {
                    nearestDistSqr = distSqr;
                    nearest = entityId;
                }
            }

            return nearest;
        }

        #endregion

        #region Spawning

        /// <summary>
        /// 副本有人进入：全部出生点并行开批次（各出生点保留自己的生成节奏与超时兜底）
        /// </summary>
        private void StartSpawning(RegionState region)
        {
            if (region.IsSpawning) return;
            region.IsSpawning = true;

            foreach (var zone in region.Zones)
            {
                zone.IsSpawning = true;
                zone.NextSpawnTime = 0f; // 立即生成第一只
                zone.SpawnBatchStartTime = Time.time;
            }
        }

        /// <summary>
        /// 逐出生点逐槽生成怪物（出生点内部按配置 monster_group 顺序，逐个间隔生成）
        /// </summary>
        private void TickSpawning(RegionState region)
        {
            bool anySpawning = false;
            foreach (var zone in region.Zones)
            {
                if (!zone.IsSpawning) continue;

                TickZoneSpawning(zone);
                anySpawning |= zone.IsSpawning;
            }

            region.IsSpawning = anySpawning;
        }

        private void TickZoneSpawning(ZoneState zone)
        {
            if (Time.time < zone.NextSpawnTime) return;

            // 找出下一个未生成的槽位（已存活怪物数即已生成数）
            int spawnedCount = zone.SlotMap.Count;
            int slotIndex = spawnedCount;
            if (slotIndex >= zone.MonsterGroup.Length)
            {
                zone.IsSpawning = false;
                return;
            }

            // 超时兜底：某只怪物生成失败（未收到 MonsterSpawned）时不永久阻塞轮询
            float batchTimeout = zone.MonsterGroup.Length * SPAWN_INTERVAL + 10f;
            if (Time.time - zone.SpawnBatchStartTime > batchTimeout)
            {
                Logging.Warning(
                    $"[MonsterSpawnerSystem] spawnId:{zone.SpawnId} 生成批次超时"
                    + $"（已生成 {spawnedCount}/{zone.MonsterGroup.Length}），强制结束，"
                    + "剩余槽位待下次副本清空再进入时补生成"
                );
                zone.IsSpawning = false;
                return;
            }

            int monsterCfgId = zone.MonsterGroup[slotIndex];
            Vector3 position = SampleSpawnPosition(zone);
            Quaternion rotation = Quaternion.Euler(0f, Random.value * 360f, 0f);

            Logging.Info($"[MonsterSpawnerSystem] spawnId:{zone.SpawnId} 生成怪物 slot:{slotIndex} cfgId:{monsterCfgId}");
            var template = new MonsterTemplate(monsterCfgId, zone.SpawnId);
            Msger.Send(MsgID.CreateMonster, template, position, rotation);

            // 下一只间隔
            zone.NextSpawnTime = Time.time + SPAWN_INTERVAL;
        }

        #endregion

        #region Spawn Position Sampling

        /// <summary>
        /// 在出生点范围内随机采样一个可行走位置（A* GetNearest 校验 + 轴并列连接过滤 + Y 偏差过滤）。
        /// </summary>
        private Vector3 SampleSpawnPosition(ZoneState zone, float maxYOffset = 3f)
        {
            Vector3 center = zone.Center;
            float spawnRadius = zone.Radius;

            if (AstarPath.active != null)
            {
                for (int attempt = 0; attempt < MAX_SPAWN_ATTEMPTS; attempt++)
                {
                    float radius = Mathf.Sqrt(Random.value) * spawnRadius;
                    float angle = Random.value * Mathf.PI * 2f;
                    var candidate = new Vector3(
                        center.x + radius * Mathf.Cos(angle),
                        center.y,
                        center.z + radius * Mathf.Sin(angle)
                    );

                    var nearest = AstarPath.active.GetNearest(candidate, NearestNodeConstraint.Walkable);
                    if (nearest.node == null || !nearest.node.Walkable) continue;

                    Vector3 walkablePos = (Vector3)nearest.position;
                    if (Mathf.Abs(walkablePos.y - candidate.y) > maxYOffset) continue;

                    // 过滤掉只有斜向连接、缺少轴并列连接的节点（PathTracer 异常来源）
                    if (!HasValidAxisAlignedConnections(nearest.node)) continue;

                    float dx = walkablePos.x - center.x;
                    float dz = walkablePos.z - center.z;
                    if (dx * dx + dz * dz <= spawnRadius * spawnRadius)
                    {
                        return walkablePos;
                    }
                }

                var fallback = AstarPath.active.GetNearest(center, NearestNodeConstraint.Walkable);
                if (fallback.node != null && fallback.node.Walkable)
                {
                    return (Vector3)fallback.position;
                }
            }

            // AstarPath 不可用 / 完全无合法节点：退化到配置中心
            return center;
        }

        /// <summary>
        /// 检查 GridNode 四个轴并列邻接方向是否都存在有效的可行走连接。
        /// （与 MonsterMoveModule 内同名逻辑一致；跨层共享待未来收敛到框架工具）
        /// </summary>
        private static bool HasValidAxisAlignedConnections(GraphNode node)
        {
            if (node is not GridNodeBase gridNode) return true; // 非 Grid 图无需检查

            for (int dir = 0; dir < 4; dir++)
            {
                var neighbor = gridNode.GetNeighbourAlongDirection(dir);
                if (neighbor == null || !neighbor.Walkable) return false;
            }
            return true;
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// 场景视图可视化（仅编辑器编译）：绘制各副本占用判定盒——
        /// 淡色填充（体积提示）+ 抗锯齿粗线框 + 区域标签，显眼且不遮挡场景视野。
        /// </summary>
#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;

            int index = 0;
            foreach (var region in _regions.Values)
            {
                Color color = index++ % 2 == 0 ? new Color(1f, 0.45f, 0f) : new Color(0.35f, 0.65f, 1f);
                Bounds bounds = region.Bounds;

                // 淡色填充：低透明度体积提示
                Gizmos.color = new Color(color.r, color.g, color.b, 0.05f);
                Gizmos.DrawCube(bounds.center, bounds.size);

                // 抗锯齿粗线框（比 Gizmos.DrawWireCube 的 1px 线清晰）
                UnityEditor.Handles.color = new Color(color.r, color.g, color.b, 0.7f);
                DrawWireCubeAA(bounds);

                UnityEditor.Handles.Label(
                    bounds.center + Vector3.up * (bounds.extents.y + 1f),
                    $"Region {region.RegionId}"
                );
            }
        }

        /// <summary>
        /// 用 Handles 抗锯齿线条绘制包围盒 12 条边（宽度 2px）
        /// </summary>
        private static void DrawWireCubeAA(Bounds bounds)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;

            var corners = new Vector3[8]
            {
                new(min.x, min.y, min.z),
                new(min.x, min.y, max.z),
                new(min.x, max.y, min.z),
                new(min.x, max.y, max.z),
                new(max.x, min.y, min.z),
                new(max.x, min.y, max.z),
                new(max.x, max.y, min.z),
                new(max.x, max.y, max.z),
            };

            int[,] edges =
            {
                { 0, 1 },
                { 0, 2 },
                { 0, 4 },
                { 1, 3 },
                { 1, 5 },
                { 2, 3 },
                { 2, 6 },
                { 3, 7 },
                { 4, 5 },
                { 4, 6 },
                { 5, 7 },
                { 6, 7 },
            };

            for (int i = 0; i < edges.GetLength(0); i++)
            {
                UnityEditor.Handles.DrawAAPolyLine(2f, corners[edges[i, 0]], corners[edges[i, 1]]);
            }
        }
#endif

        #endregion

        #region Region State

        /// <summary>
        /// 初始化副本区域（来自 TbMonsterspwan；Awake 全端急切构建，槽位缓存不依赖 MC 身份）。
        /// Region 未定义的出生点行直接跳过（fail loud）。
        /// </summary>
        private void InitRegions()
        {
            var table = ConfigMgr.Tables.TbMonsterspwan;
            if (table == null) return;

            foreach (var spwanCfg in table.DataList)
            {
                if (spwanCfg.SpwanPosition == null || spwanCfg.SpwanPosition.Length < 3) continue;

                // Region 校验：未定义的副本区域返回零尺寸 Bounds，跳过该行
                Bounds regionBounds = Consts.GetRegionBounds(spwanCfg.Region);
                if (regionBounds.size.sqrMagnitude <= 0f)
                {
                    Logging.Error(
                        $"[MonsterSpawnerSystem] spawnId:{spwanCfg.SpwanId} 所属副本区域未定义 (region:{spwanCfg.Region})，跳过该出生点"
                    );
                    continue;
                }

                if (!_regions.TryGetValue(spwanCfg.Region, out var region))
                {
                    region = new RegionState
                    {
                        RegionId = spwanCfg.Region,
                        Center = Consts.GetRegionCenter(spwanCfg.Region),
                        Bounds = regionBounds,
                    };
                    _regions[spwanCfg.Region] = region;
                }

                var zone = new ZoneState
                {
                    SpawnId = spwanCfg.SpwanId,
                    Center = new Vector3(
                        spwanCfg.SpwanPosition[0],
                        spwanCfg.SpwanPosition[1],
                        spwanCfg.SpwanPosition[2]
                    ),
                    Radius = Mathf.Max(0.1f, spwanCfg.Radius),
                    MonsterGroup = spwanCfg.MonsterGroup ?? Array.Empty<int>(),
                    Region = region,
                };
                region.Zones.Add(zone);
                _zones[spwanCfg.SpwanId] = zone;
            }

            Logging.Info($"[MonsterSpawnerSystem] 初始化副本区域 {_regions.Count} 个（出生点 {_zones.Count} 个）");
        }

        /// <summary>
        /// MC 重选时重置决策中间态（槽位图与区域配置保留）
        /// </summary>
        private void ResetRegionRuntimeStates()
        {
            foreach (var region in _regions.Values)
            {
                region.CurrentOwner = default;
                region.LastOccupants.Clear();
                region.CurrentOccupants.Clear();
                region.StableRounds = 0;
                region.PollTimer = 0f;
                region.IsSpawning = false;

                foreach (var zone in region.Zones)
                {
                    zone.IsSpawning = false;
                    zone.NextSpawnTime = 0f;
                }
            }
        }

        /// <summary>
        /// 对副本全部存活怪物重发归属（default→owner 两连发强制事件）
        /// </summary>
        private void ReassignAuthorityToAll(RegionState region, EntityId owner)
        {
            foreach (var zone in region.Zones)
            {
                foreach (var entityId in zone.SlotMap)
                {
                    // SetDesiredAuthorityOwner 同值 no-op：先归零再指派，强制各端 MonsterAuthorityModule 重新反应
                    Msger.Send(MsgID.AssignMonsterAuthority, entityId, default(EntityId));
                    Msger.Send(MsgID.AssignMonsterAuthority, entityId, owner);
                }
            }
        }

        /// <summary>
        /// 单个副本区域状态（占用 / 归属决策单位；仅 MC 端决策使用，槽位图由事件全端维护）
        /// </summary>
        private sealed class RegionState
        {
            public int RegionId;
            public Vector3 Center;
            public Bounds Bounds;

            /// <summary> 副本内全部出生点 </summary>
            public readonly List<ZoneState> Zones = new();

            /// <summary> 当前权威归属玩家 Id（副本级，覆盖副本内全部怪物） </summary>
            public EntityId CurrentOwner;

            /// <summary> 上一轮 / 当前轮的占用玩家集合（稳定过滤用）</summary>
            public readonly HashSet<EntityId> LastOccupants = new();
            public readonly HashSet<EntityId> CurrentOccupants = new();

            public int StableRounds;
            public float PollTimer;
            public bool IsSpawning;
        }

        /// <summary>
        /// 单个出生点状态（出生位置 / 怪物组 / 生成批次；占用与归属上移到所属副本）
        /// </summary>
        private sealed class ZoneState
        {
            public int SpawnId;
            public Vector3 Center;
            public float Radius;
            public int[] MonsterGroup;

            /// <summary> 所属副本（决策归属回查） </summary>
            public RegionState Region;

            /// <summary> 本出生点存活怪物缓存（事件维护，全端一致）</summary>
            public readonly HashSet<EntityId> SlotMap = new();

            public float NextSpawnTime;
            public bool IsSpawning;
            public float SpawnBatchStartTime;
        }

        #endregion
    }
}
