using System;
using System.Collections.Generic;
using cfg;
using Framework;
using Framework.Core;
using Framework.Network;
using Game.DTOs;
using UnityEngine;
using Random = UnityEngine.Random;
using Vector3 = UnityEngine.Vector3;

namespace Game.Entities
{
    /// <summary>
    /// 怪物生成系统（世界系统·非实体，MonoBehaviour 形态，GameWorld 挂载到宿主节点）。
    /// 票 20 重写为 EncounterSession 驱动的遭遇组生成协调者，仅 MasterClient 端执行生成决策
    /// （共享世界决策类世界系统的特性）；槽位事实全端事件维护（MC 重选不丢）。
    ///
    /// - 遭遇组生成：会话生成 / 代际边沿（Phase → Engaged 且 Generation 前进）→ 开批次补满怪物组
    ///   （槽位 = MonsterIndex）；仍有存活不补空槽——死亡槽本代消耗，全灭进 Cooldown 后下一代恢复完整组；
    /// - MC 远端生成：每次在 Center + SpawnRadius 水平圆盘内随机 + 全局玩家摘要做出生点安全距离
    ///   检查（3m；Explicit 行豁免——生成点 = 设计意图位置，「生成在被困玩家面前」正是产品语义，票 28），
    ///   不查 NavMesh / AOI / 场景对象——MC 不加载远端 chunk（票 20 验收）；
    /// - 出生点被占只短延后（下轮重试换点），不重置任何倒计时；
    /// - 生成确认超时重试（仅未确认槽位回到 Free 重发，已确认不重发——不重复超量）；
    ///   (encounterId, monsterIndex) 槽位重复到达按孤儿销毁（防御断言）；
    /// - 存活对账：槽位事实变化 → MC 发 SetEncounterSessionAliveCount（System 路由会话 Ctrl）；
    ///   全灭边沿（批次结算且存活 0）→ MC 发 MarkEncounterSessionGroupWiped → Engaged 转 Cooldown；
    /// - 宽限到期清场：Phase → ReadyToRespawn 边沿 → MC 销毁残余怪物（逐只 DestroyMonster）；
    /// - 死亡回收：MonsterDied → MC 发 DestroyMonster（尸体销毁，统一）。
    ///
    /// MC 更换语义：槽位 Alive / Dead 由事件全端维护；Free / Pending 是旧 MC 决策中间态，
    /// 新 MC 对 Free 槽照常补发（批次续传）、Pending 槽随超时自愈回 Free，代内死亡槽（Dead）不复活。
    /// 执行全部经总线：CreateMonster / DestroyMonster → MonsterSystem 路由（统一销毁模式）；
    /// 会话事实写入经 SetEncounterSessionAliveCount / MarkEncounterSessionGroupWiped →
    /// EncounterSessionSystem 路由 → 会话 Ctrl 公开 API（执行收口）。
    ///
    /// 事件失明自愈：遭遇状态纯事件维护的盲区（事件先于本系统就绪 / 跨端时序错位）由周期事实
    /// 对账兜底（全端 5s）：按会话快照 + 存活怪物事实重建缺失 / 换代 / 陈旧的组状态，MC 端补报
    /// 存活计数、全灭边沿与盲窗尸体回收——错过事件不再导致全灭检测永久丢失。
    ///
    /// 生命周期：Update 内 deltaTime 累积驱动轮询（tick 判据）；
    /// Awake 注册监听 / OnEnable·OnDisable 起停 tick / OnDestroy 清理。
    /// </summary>
    public class MonsterSpawnerSystem : MonoBehaviour
    {
        /// <summary> 决策循环间隔（秒），批次节奏与超时检查共用 </summary>
        private const float LOOP_INTERVAL = 0.5f;

        /// <summary> 批次内单只生成间隔（秒） </summary>
        private const float SPAWN_INTERVAL = 0.5f;

        /// <summary> 生成确认超时（秒）：发出 CreateMonster 后迟迟未收到 MonsterSpawned 视为失败，槽位回 Free 重试 </summary>
        private const float SPAWN_CONFIRM_TIMEOUT = 5f;

        /// <summary> 出生点安全距离（米）：玩家摘要 3D 距内有玩家视为占用，换点 / 延后 </summary>
        private const float SPAWN_POINT_SAFETY_RADIUS = 3f;

        /// <summary> 单轮随机选点次数上限；未找到空闲点则短延后，避免无限循环 </summary>
        private const int SPAWN_POINT_ATTEMPTS = 16;

        /// <summary> 槽位补齐等待窗口（秒）：晚加入 / MC 迁移时会话事件先于怪物事件到达时，
        /// 抑制 MC 批次 / 上报决策，等 MonsterSpawned 事件补齐槽位（超时兜底放行） </summary>
        private const float SLOT_RESYNC_WINDOW = 2f;

        /// <summary> 出生点全被占用的重试间隔（秒）：短延后（玩家让开即恢复），不重置任何倒计时 </summary>
        private const float SPAWN_POINT_RETRY_INTERVAL = 2f;

        /// <summary> 事实对账间隔（秒）：全端周期重建缺失 / 陈旧的遭遇组状态（错过事件自愈） </summary>
        private const float RECONCILE_INTERVAL = 5f;

        private readonly MsgerGroup _bus = new();

        /// <summary> 活跃遭遇组状态（encounterId → 组；Spawned / Despawned 事件维护，全端一致） </summary>
        private readonly Dictionary<int, EncounterGroupState> _encounters = new();

        /// <summary>
        /// 未归属遭遇怪缓冲（encounterId → (Id, 槽位) 列表）：晚加入 / 事件乱序时怪物事件先于会话事件到达，
        /// 会话状态建立时重放入槽（否则槽位不完整 → MC 补发 → 超量）；怪物死亡 / 销毁时清除对应项。
        /// </summary>
        private readonly Dictionary<int, List<(EntityId Id, int MonsterIndex)>> _unattributedMonsters = new();

        /// <summary> 摘要快照缓存（查询后即用即失，不跨帧持有） </summary>
        private readonly List<PlayerPresenceSnapshot> _snapshots = new();

        /// <summary> 上一 tick 是否已是 MasterClient（MC 变更检测） </summary>
        private bool _wasMasterClient;

        /// <summary> 决策轮询累积计时（deltaTime 驱动，tick 判据） </summary>
        private float _loopTimer;

        /// <summary> 事实对账累积计时（全端执行，与 MC 决策节拍独立） </summary>
        private float _reconcileTimer;

        /// <summary>
        /// 槽位状态（全端维护 Alive / Dead；Free / Pending 仅 MC 决策使用——见类注释 MC 更换语义）。
        /// </summary>
        private enum SlotState : byte
        {
            /// <summary> 本代未生成（代际边沿重置 / 确认超时回退） </summary>
            Free = 0,

            /// <summary> 已发创建命令待确认（MC 决策中间态） </summary>
            Pending = 1,

            /// <summary> 在世（MonsterSpawned 确认） </summary>
            Alive = 2,

            /// <summary> 本代已消耗（死亡 / 销毁——不补空槽） </summary>
            Dead = 3,
        }

        #region Lifecycle

        private void Awake()
        {
            RegisterListeners();
        }

        private void OnEnable()
        {
            _loopTimer = 0f;
            _reconcileTimer = 0f;
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
        /// 决策循环（各端实例常驻，仅 MC 端执行生成决策；周期事实对账全端执行）。
        /// </summary>
        private void Update()
        {
            _loopTimer += Time.deltaTime;
            while (_loopTimer >= LOOP_INTERVAL)
            {
                _loopTimer -= LOOP_INTERVAL;
                TickOnce();
            }

            _reconcileTimer += Time.deltaTime;
            if (_reconcileTimer < RECONCILE_INTERVAL) return;
            _reconcileTimer = 0f;
            ReconcileFromFacts();
        }

        /// <summary>
        /// 单次决策 tick：MC 变更检测 → 逐遭遇组（超时自愈 + 批次补发 + 全灭检查）。
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

            foreach (var state in _encounters.Values)
            {
                TickGroup(state, becameMaster);
            }
        }

        #endregion

        #region Registers

        private void RegisterListeners()
        {
            if (_bus is null) return;

            // 遭遇组生命周期（全端维护相位 / 代际视图与槽位事实）
            _bus.AddListener(MsgID.EncounterSessionSpawned, OnEncounterSessionSpawned);
            _bus.AddListener(MsgID.EncounterSessionDespawned, OnEncounterSessionDespawned);
            _bus.AddListener(MsgID.EncounterSessionStateChanged, OnEncounterSessionStateChanged);

            // 怪物槽位事实（全端维护，MC 重选不丢）
            _bus.AddListener(MsgID.MonsterSpawned, OnMonsterSpawned);
            _bus.AddListener(MsgID.MonsterDespawned, OnMonsterDespawned);
            _bus.AddListener(MsgID.MonsterDied, OnMonsterDied);
        }

        #endregion

        #region Listeners (Session)

        private void OnEncounterSessionSpawned(MsgID id, object data)
        {
            if (data is EncounterSessionSnapshot snapshot)
            {
                // 防重：孤儿会话由 EncounterSessionSystem 销毁，本端只认首个实例
                if (_encounters.TryGetValue(snapshot.EncounterId, out var existing))
                {
                    if (existing.SessionId != snapshot.SessionId)
                    {
                        Logging.Warning(
                            $"[MonsterSpawnerSystem] 遭遇 {snapshot.EncounterId} 已有会话，忽略后到实例 ({snapshot.SessionId})");
                    }

                    return;
                }

                var state = CreateGroupState(snapshot.EncounterId, snapshot.SessionId);
                if (state == null) return;

                // 首次激活 = Engaged / Generation 1：开首批（MC 端批次循环消费 Free 槽）
                state.LastPhase = snapshot.Phase;
                state.LastGeneration = snapshot.Generation;

                // 乱序到达的怪物事件重放入槽（本端此前无状态时缓存的部分）
                ReplayUnattributedMonsters(state);

                // 已有存活的会话（晚加入 / MC 迁移）：等待槽位补齐后才恢复 MC 决策，防止超量补发
                if (snapshot.AliveCount > 0)
                {
                    state.AwaitingResync = true;
                    state.ResyncSince = Time.time;
                    state.ResyncTargetAlive = snapshot.AliveCount;
                }

                Logging.Info(
                    $"[MonsterSpawnerSystem] 遭遇 {snapshot.EncounterId} 会话激活（{snapshot.Phase} gen{snapshot.Generation}），" +
                    $"怪物组 {state.Cfg.MonsterGroup.Length} 只 / 出生半径 {state.Cfg.SpawnRadius}m" +
                    (state.AwaitingResync ? $"（槽位补齐中：快照存活 {snapshot.AliveCount}）" : string.Empty));
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnEncounterSessionDespawned(MsgID id, object data)
        {
            if (data is EncounterSessionSnapshot snapshot)
            {
                if (_encounters.TryGetValue(snapshot.EncounterId, out var state)
                    && state.SessionId == snapshot.SessionId)
                {
                    _encounters.Remove(snapshot.EncounterId);
                }

                _unattributedMonsters.Remove(snapshot.EncounterId);
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnEncounterSessionStateChanged(MsgID id, object data)
        {
            if (data is EncounterSessionSnapshot snapshot)
            {
                if (!_encounters.TryGetValue(snapshot.EncounterId, out var state)) return;
                if (state.SessionId != snapshot.SessionId) return;

                bool generationAdvanced = snapshot.Generation != state.LastGeneration;
                bool enteredEngaged = snapshot.Phase == EncounterPhase.Engaged && state.LastPhase != EncounterPhase.Engaged;
                bool enteredReadyToRespawn =
                    snapshot.Phase == EncounterPhase.ReadyToRespawn && state.LastPhase != EncounterPhase.ReadyToRespawn;

                if (generationAdvanced)
                {
                    // 代际边沿（BeginNextGeneration → Engaged）：槽位全部重置开新批次
                    ResetGroupGeneration(state);
                    Logging.Info(
                        $"[MonsterSpawnerSystem] 遭遇 {snapshot.EncounterId} 开始第 {snapshot.Generation} 代，补满怪物组");
                }
                else if (state.AwaitingResync && snapshot.Generation == 1)
                {
                    // 补齐窗口内的同代事实刷新：以最新快照为对账目标
                    state.ResyncTargetAlive = Mathf.Max(state.ResyncTargetAlive, snapshot.AliveCount);
                }

                if (enteredReadyToRespawn && NetworkMgr.IsMasterClient)
                {
                    // 宽限到期全复位：销毁残余怪物（Grace → ReadyToRespawn 边沿；
                    // Cooldown → ReadyToRespawn 正常路径存活为 0，清理为空操作）
                    DestroyAliveMonsters(state, "宽限到期清场");
                }

                state.LastPhase = snapshot.Phase;
                state.LastGeneration = snapshot.Generation;

                // 宽限内返回恢复 Engaged 且已无存活（死在宽限期的收敛）→ 补一次全灭检查
                if (enteredEngaged && NetworkMgr.IsMasterClient)
                {
                    TryReportGroupWiped(state);
                }

                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        #endregion

        #region Listeners (Monster Slots)

        private void OnMonsterSpawned(MsgID id, object data)
        {
            if (data is object[] { Length: 2 } datas
                && datas[0] is EntityId entityId
                && datas[1] is MonsterTemplate template)
            {
                if (template.EncounterId != 0)
                {
                    OnEncounterMonsterSpawned(entityId, template);
                }

                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnEncounterMonsterSpawned(EntityId entityId, MonsterTemplate template)
        {
            if (!_encounters.TryGetValue(template.EncounterId, out var state))
            {
                // 会话事件尚未到达（晚加入 / 事件乱序）：缓存待归属，会话状态建立时重放
                if (!_unattributedMonsters.TryGetValue(template.EncounterId, out var buffered))
                {
                    buffered = new List<(EntityId, int)>();
                    _unattributedMonsters[template.EncounterId] = buffered;
                }

                buffered.Add((entityId, template.MonsterIndex));
                return;
            }

            if (template.MonsterIndex < 0 || template.MonsterIndex >= state.Slots.Length)
            {
                Logging.Error(
                    $"[MonsterSpawnerSystem] 遭遇 {template.EncounterId} 槽位越界 (index {template.MonsterIndex})，忽略 ({entityId})");
                return;
            }

            // 槽位重复到达（旧 MC 在途请求迟到等）：后到按孤儿销毁，不重复超量
            if (state.Slots[template.MonsterIndex] == SlotState.Alive
                && state.SlotIds[template.MonsterIndex].IsValid
                && state.SlotIds[template.MonsterIndex] != entityId)
            {
                Logging.Warning(
                    $"[MonsterSpawnerSystem] 遭遇 {template.EncounterId} 槽位 {template.MonsterIndex} 重复到达，孤儿销毁 ({entityId})");
                if (NetworkMgr.IsMasterClient)
                {
                    Msger.Send(MsgID.DestroyMonster, entityId);
                }

                return;
            }

            state.Slots[template.MonsterIndex] = SlotState.Alive;
            state.SlotIds[template.MonsterIndex] = entityId;
            state.RequestTimes[template.MonsterIndex] = 0f;
            ReportAliveCount(state);
        }

        private void OnMonsterDespawned(MsgID id, object data)
        {
            if (data is EntityId entityId)
            {
                foreach (var state in _encounters.Values)
                {
                    MarkEncounterSlotConsumed(state, entityId);
                }

                PurgeUnattributedMonster(entityId);
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnMonsterDied(MsgID id, object data)
        {
            if (data is object[] { Length: 6 } datas
                && datas[0] is EntityId entityId
                && datas[1] is MonsterTemplate template
                && datas[2] is Vector3
                && datas[3] is Quaternion
                && datas[4] is IReadOnlyList<DamageAttribution>
                && datas[5] is EntityId)
            {
                // 全端实例都维护槽位（MC 重选不丢）；仅 MC 实例发起销毁
                bool removed = false;

                if (template.EncounterId != 0 && _encounters.TryGetValue(template.EncounterId, out var state))
                {
                    removed |= MarkEncounterSlotConsumed(state, entityId);
                }

                PurgeUnattributedMonster(entityId);

                if (removed && NetworkMgr.IsMasterClient)
                {
                    Logging.Info($"[MonsterSpawnerSystem] 死亡流程完成，销毁怪物 ({entityId})");
                    Msger.Send(MsgID.DestroyMonster, entityId);
                }

                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        /// <summary> 槽位标记本代消耗（死亡 / 销毁），并对账存活计数（MC 端上报） </summary>
        private bool MarkEncounterSlotConsumed(EncounterGroupState state, EntityId entityId)
        {
            for (int i = 0; i < state.Slots.Length; i++)
            {
                if (state.SlotIds[i] != entityId || !entityId.IsValid) continue;

                state.Slots[i] = SlotState.Dead;
                state.SlotIds[i] = default;
                ReportAliveCount(state);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 重放未归属怪物入槽（会话状态刚建立时）：乱序先到的 MonsterSpawned 按槽位落 Alive。
        /// </summary>
        private void ReplayUnattributedMonsters(EncounterGroupState state)
        {
            if (!_unattributedMonsters.TryGetValue(state.EncounterId, out var buffered)) return;

            foreach (var (id, index) in buffered)
            {
                if (index < 0 || index >= state.Slots.Length) continue;
                if (!id.IsValid) continue;

                state.Slots[index] = SlotState.Alive;
                state.SlotIds[index] = id;
            }

            _unattributedMonsters.Remove(state.EncounterId);
        }

        /// <summary> 清除未归属缓冲中已死亡 / 销毁的怪物（防止重放过期事实） </summary>
        private void PurgeUnattributedMonster(EntityId entityId)
        {
            List<int> emptyKeys = null;

            foreach (var pair in _unattributedMonsters)
            {
                for (int i = pair.Value.Count - 1; i >= 0; i--)
                {
                    if (pair.Value[i].Id == entityId) pair.Value.RemoveAt(i);
                }

                if (pair.Value.Count == 0)
                {
                    emptyKeys ??= new List<int>();
                    emptyKeys.Add(pair.Key);
                }
            }

            if (emptyKeys == null) return;

            foreach (int encounterId in emptyKeys)
            {
                _unattributedMonsters.Remove(encounterId);
            }
        }

        #endregion

        #region Encounter Spawning (MC)

        /// <summary>
        /// 单遭遇组决策（仅 MC 端）：槽位补齐等待 → 超时自愈 → 批次补发（Engaged 才生成）→ 全灭检查。
        /// </summary>
        private void TickGroup(EncounterGroupState state, bool becameMaster)
        {
            // 槽位补齐窗口（晚加入 / MC 迁移）：等待乱序怪物事件追平快照存活数，期间不做任何决策
            if (state.AwaitingResync)
            {
                bool caughtUp = CountSlots(state, SlotState.Alive) >= state.ResyncTargetAlive;
                bool expired = Time.time - state.ResyncSince > SLOT_RESYNC_WINDOW;
                if (!caughtUp && !expired) return;

                state.AwaitingResync = false;
                if (!caughtUp)
                {
                    Logging.Warning(
                        $"[MonsterSpawnerSystem] 遭遇 {state.EncounterId} 槽位补齐超时（快照存活 {state.ResyncTargetAlive}，"
                        + $"本端槽位 {CountSlots(state, SlotState.Alive)}），按本端事实继续");
                }
            }

            // 确认超时自愈：在途请求未落地（生成失败 / 旧 MC 中间态）回 Free 重试
            for (int i = 0; i < state.Slots.Length; i++)
            {
                if (state.Slots[i] != SlotState.Pending) continue;

                if (Time.time - state.RequestTimes[i] > SPAWN_CONFIRM_TIMEOUT)
                {
                    Logging.Warning(
                        $"[MonsterSpawnerSystem] 遭遇 {state.EncounterId} 槽位 {i} 生成确认超时，回退重试");
                    state.Slots[i] = SlotState.Free;
                    state.RequestTimes[i] = 0f;
                }
            }

            if (becameMaster)
            {
                // 新 MC：强制重报存活计数；全灭收敛检查（死在 MC 交接窗的补算）
                state.LastReportedAlive = -1;
                ReportAliveCount(state);
                TryReportGroupWiped(state);
            }

            // 批次补发只在 Engaged（Cooldown / Grace / ReadyToRespawn 不生成）
            if (state.LastPhase == EncounterPhase.Engaged)
            {
                TickGroupSpawn(state);
            }

            TryReportGroupWiped(state);
        }

        /// <summary>
        /// 批次补发：到节奏后取一个 Free 槽位 → 随机选空闲出生位置 → 发创建命令。
        /// 出生点全被占用时本轮跳过（下轮重试，不重置倒计时）；无 Free 槽位即批次结算。
        /// </summary>
        private void TickGroupSpawn(EncounterGroupState state)
        {
            if (Time.time < state.NextSpawnTime) return;

            int slotIndex = FindFreeSlot(state);
            if (slotIndex < 0) return; // 批次结算（全部 Alive / Dead / Pending 在途）

            if (!TryPickFreeSpawnPoint(state, out Vector3 position))
            {
                // 出生点全被玩家占用：短延后重试（玩家让开即恢复），不重置任何倒计时
                state.NextSpawnTime = Time.time + SPAWN_POINT_RETRY_INTERVAL;
                return;
            }

            int monsterCfgId = state.Cfg.MonsterGroup[slotIndex];
            var rotation = Quaternion.Euler(0f, Random.value * 360f, 0f);

            Logging.Info(
                $"[MonsterSpawnerSystem] 遭遇 {state.EncounterId} 生成怪物 slot:{slotIndex} cfgId:{monsterCfgId} pos:{position}");
            // Template 携带遭遇归属 + 本次随机出生落位（巡逻 / 脱战锚）
            var template = new MonsterTemplate(monsterCfgId, slotIndex, state.EncounterId, position);

            // 同步总线可能在 Send 返回前完成生成并回调 MonsterSpawned。
            // 先登记在途状态，确认事件才能将其推进为 Alive；返回后不得覆盖确认或死亡事实。
            state.Slots[slotIndex] = SlotState.Pending;
            state.RequestTimes[slotIndex] = Time.time;
            state.NextSpawnTime = Time.time + SPAWN_INTERVAL;
            Msger.Send(MsgID.CreateMonster, template, position, rotation);
        }

        /// <summary> 全灭边沿上报：批次结算（无 Free / Pending）且存活 0 且仍在 Engaged </summary>
        private void TryReportGroupWiped(EncounterGroupState state)
        {
            if (state.WipeReported) return;
            if (state.LastPhase != EncounterPhase.Engaged) return;
            if (CountSlots(state, SlotState.Free) > 0 || CountSlots(state, SlotState.Pending) > 0) return;
            if (CountSlots(state, SlotState.Alive) > 0) return;

            // 空怪物组配置不触发（校验器已拦，防御）
            if (state.Slots.Length == 0) return;

            state.WipeReported = true;
            Logging.Info($"[MonsterSpawnerSystem] 遭遇 {state.EncounterId} 怪物组全灭，上报重刷倒计时");
            Msger.Send(MsgID.MarkEncounterSessionGroupWiped, state.EncounterId);
        }

        /// <summary> 存活计数对账上报（仅 MC 端；值未变不重发） </summary>
        private void ReportAliveCount(EncounterGroupState state)
        {
            if (!NetworkMgr.IsMasterClient) return;

            int alive = CountSlots(state, SlotState.Alive);
            if (alive == state.LastReportedAlive) return;

            state.LastReportedAlive = alive;
            Msger.Send(MsgID.SetEncounterSessionAliveCount, state.EncounterId, alive);
        }

        /// <summary> 销毁残余存活怪物（宽限到期清场；逐只通用命令，按 Id 路由） </summary>
        private void DestroyAliveMonsters(EncounterGroupState state, string reason)
        {
            for (int i = 0; i < state.Slots.Length; i++)
            {
                if (state.Slots[i] != SlotState.Alive) continue;
                if (!state.SlotIds[i].IsValid) continue;

                Logging.Info(
                    $"[MonsterSpawnerSystem] 遭遇 {state.EncounterId} {reason}，销毁残余怪物 ({state.SlotIds[i]})");
                Msger.Send(MsgID.DestroyMonster, state.SlotIds[i]);

                // 销毁命令已发出：本代消耗（Despawned 事件到达前防止重复发）
                state.Slots[i] = SlotState.Dead;
                state.SlotIds[i] = default;
            }

            ReportAliveCount(state);
        }

        /// <summary>
        /// 每次在 Center 的水平圆盘内均匀随机，Y 保持 Center.y；半径 0 为定点。
        /// 仅检查玩家安全距离，不采样地面或导航；有限重试失败后短延后。
        /// Explicit 行豁免安全距离检查（票 28）：生成点 = 设计意图位置，
        /// 「生成在被困玩家面前」正是产品语义——被困玩家必然 &lt; 3m，检查会永久延后生成。
        /// </summary>
        private bool TryPickFreeSpawnPoint(EncounterGroupState state, out UnityEngine.Vector3 position)
        {
            position = default;

            bool exemptSafety = state.Cfg.TriggerType == EncounterTriggerType.EXPLICIT;
            if (!exemptSafety && !TryLoadSnapshots()) return false; // 摘要不可得：本轮跳过（下轮重试）

            float sqrSafety = SPAWN_POINT_SAFETY_RADIUS * SPAWN_POINT_SAFETY_RADIUS;
            Vector3 center = TbMonsterspawn.DeriveEncounterCenter(state.Cfg);
            int attempts = state.Cfg.SpawnRadius > 0f ? SPAWN_POINT_ATTEMPTS : 1;
            for (int i = 0; i < attempts; i++)
            {
                UnityEngine.Vector2 offset = Random.insideUnitCircle * state.Cfg.SpawnRadius;
                Vector3 candidate = center + new Vector3(offset.x, 0f, offset.y);
                if (!exemptSafety && IsPointOccupied(candidate, sqrSafety)) continue;
                position = candidate;
                return true;
            }

            return false;
        }

        /// <summary> 出生点占用判定：任一在线玩家摘要 3D 距在安全半径内（MC 零场景依赖） </summary>
        private bool IsPointOccupied(UnityEngine.Vector3 point, float sqrSafety)
        {
            for (int i = 0; i < _snapshots.Count; i++)
            {
                var snapshot = _snapshots[i];
                if (!snapshot.IsOnline) continue;

                if ((snapshot.Position - point).sqrMagnitude <= sqrSafety) return true;
            }

            return false;
        }

        /// <summary> 拉取全局玩家摘要快照（即用即失；不可得返回 false 本轮跳过） </summary>
        private bool TryLoadSnapshots()
        {
            var snapshots = Svcer.Req<List<PlayerPresenceSnapshot>>(SvcID.QueryPlayerPresenceSnapshots);
            if (snapshots == null) return false;

            _snapshots.Clear();
            _snapshots.AddRange(snapshots);
            return true;
        }

        private static int FindFreeSlot(EncounterGroupState state)
        {
            for (int i = 0; i < state.Slots.Length; i++)
            {
                if (state.Slots[i] == SlotState.Free) return i;
            }

            return -1;
        }

        private static int CountSlots(EncounterGroupState state, SlotState slotState)
        {
            int count = 0;
            for (int i = 0; i < state.Slots.Length; i++)
            {
                if (state.Slots[i] == slotState) count++;
            }

            return count;
        }

        /// <summary> 代际重置：槽位全部回 Free 开新批次（全灭 / 清场后槽位本就空，防御性全重置） </summary>
        private static void ResetGroupGeneration(EncounterGroupState state)
        {
            for (int i = 0; i < state.Slots.Length; i++)
            {
                state.Slots[i] = SlotState.Free;
                state.SlotIds[i] = default;
                state.RequestTimes[i] = 0f;
            }

            state.WipeReported = false;
            state.AwaitingResync = false;
            state.NextSpawnTime = 0f; // 立即生成第一只
        }

        /// <summary> 建组（表配置缺失 / 出生点非法返回 null 并告警） </summary>
        private EncounterGroupState CreateGroupState(int encounterId, EntityId sessionId)
        {
            var cfg = ConfigMgr.Tables != null ? ConfigMgr.Tables.TbMonsterspawn.GetOrDefault(encounterId) : null;
            if (cfg == null)
            {
                Logging.Error($"[MonsterSpawnerSystem] 遭遇配置不存在 ({encounterId})，该组不生成");
                return null;
            }

            if (cfg.Center is not { Length: 3 }
                || Array.Exists(cfg.Center, value => float.IsNaN(value) || float.IsInfinity(value))
                || float.IsNaN(cfg.SpawnRadius) || float.IsInfinity(cfg.SpawnRadius) || cfg.SpawnRadius < 0f)
            {
                Logging.Error(
                    $"[MonsterSpawnerSystem] 遭遇 {encounterId} Center 或 SpawnRadius 非法，该组不生成");
                return null;
            }

            if (cfg.MonsterGroup is not { Length: > 0 })
            {
                Logging.Error($"[MonsterSpawnerSystem] 遭遇 {encounterId} 怪物组为空，该组不生成");
                return null;
            }

            var state = new EncounterGroupState
            {
                EncounterId = encounterId,
                SessionId = sessionId,
                Cfg = cfg,
                Slots = new SlotState[cfg.MonsterGroup.Length],
                SlotIds = new EntityId[cfg.MonsterGroup.Length],
                RequestTimes = new float[cfg.MonsterGroup.Length],
                LastReportedAlive = -1,
            };

            ResetGroupGeneration(state);
            _encounters[encounterId] = state;
            return state;
        }

        #endregion

        #region Fact Reconcile (Self-Heal)

        /// <summary>
        /// 周期事实对账（全端，独立于 MC 决策节拍）：遭遇状态由 Spawned / StateChanged / Despawned
        /// 事件维护，事件先于本系统就绪或跨端时序错位时即被错过——按会话快照与存活怪物事实重建：
        /// - 会话在世而本端无状态（错过 Spawned）→ 重建组状态 + 按在世怪物恢复槽位；
        /// - 会话换代而本端持有旧会话（错过 Despawned + Spawned）→ 丢弃旧状态按新会话重建；
        /// - 会话已销毁而本端仍持有（错过 Despawned）→ 移除陈旧状态。
        /// 重建后 MC 端立即补报存活计数与全灭边沿（死在盲窗的全灭在此补算）。
        /// </summary>
        private void ReconcileFromFacts()
        {
            var runner = NetworkMgr.PhotonRunner;
            if (runner == null || !runner.IsRunning) return;

            var snapshots = Svcer.Req<List<EncounterSessionSnapshot>>(SvcID.QueryEncounterSessionSnapshots);
            if (snapshots == null) return;

            var facts = Svcer.Req<List<EncounterMonsterFact>>(SvcID.QueryEncounterMonsterFacts);
            if (facts == null) return;

            var liveEncounterIds = new HashSet<int>();
            foreach (var snapshot in snapshots)
            {
                liveEncounterIds.Add(snapshot.EncounterId);

                if (!_encounters.TryGetValue(snapshot.EncounterId, out var state))
                {
                    HealEncounter(snapshot, facts);
                    continue;
                }

                if (state.SessionId != snapshot.SessionId)
                {
                    _encounters.Remove(snapshot.EncounterId);
                    _unattributedMonsters.Remove(snapshot.EncounterId);
                    Logging.Warning(
                        $"[MonsterSpawnerSystem] 遭遇 {snapshot.EncounterId} 会话换代未及事件（旧 {state.SessionId} → 新 {snapshot.SessionId}），按新会话重建");
                    HealEncounter(snapshot, facts);
                }
            }

            // 会话已销毁而本端仍持有的陈旧项（错过 Despawned 事件）
            List<int> staleIds = null;
            foreach (var encounterId in _encounters.Keys)
            {
                if (liveEncounterIds.Contains(encounterId)) continue;

                staleIds ??= new List<int>();
                staleIds.Add(encounterId);
            }

            if (staleIds == null) return;

            foreach (int staleId in staleIds)
            {
                _encounters.Remove(staleId);
                _unattributedMonsters.Remove(staleId);
                Logging.Info($"[MonsterSpawnerSystem] 遭遇 {staleId} 会话已销毁，对账移除陈旧状态");
            }
        }

        /// <summary>
        /// 按快照与存活事实重建单个遭遇组状态：在世怪物落 Alive 槽、尸体落 Dead 槽（MC 端补发销毁
        /// 回收）、未观测槽位保守记本代消耗（避免补发超量；在途生成由 MonsterSpawned 到达时覆写回
        /// Alive）。快照存活 &gt; 在世落槽时进入槽位补齐窗口等待事件追平。
        /// </summary>
        private void HealEncounter(EncounterSessionSnapshot snapshot, List<EncounterMonsterFact> facts)
        {
            var state = CreateGroupState(snapshot.EncounterId, snapshot.SessionId);
            if (state == null) return;

            state.LastPhase = snapshot.Phase;
            state.LastGeneration = snapshot.Generation;

            ReplayUnattributedMonsters(state);

            int aliveSeen = 0;
            foreach (var fact in facts)
            {
                if (fact.Template.EncounterId != snapshot.EncounterId) continue;
                if (fact.Template.MonsterIndex < 0 || fact.Template.MonsterIndex >= state.Slots.Length) continue;

                if (fact.IsDead)
                {
                    state.Slots[fact.Template.MonsterIndex] = SlotState.Dead;
                    continue;
                }

                state.Slots[fact.Template.MonsterIndex] = SlotState.Alive;
                state.SlotIds[fact.Template.MonsterIndex] = fact.Id;
                aliveSeen++;
            }

            // 未观测到的槽位保守记本代消耗：MC 端批次补发只在 Free 槽进行，宁缺不多
            for (int i = 0; i < state.Slots.Length; i++)
            {
                if (state.Slots[i] == SlotState.Free) state.Slots[i] = SlotState.Dead;
            }

            if (snapshot.AliveCount > aliveSeen)
            {
                state.AwaitingResync = true;
                state.ResyncSince = Time.time;
                state.ResyncTargetAlive = snapshot.AliveCount;
            }

            Logging.Info(
                $"[MonsterSpawnerSystem] 遭遇 {snapshot.EncounterId} 状态对账重建（{snapshot.Phase} gen{snapshot.Generation}，在世落槽 {aliveSeen} / 快照存活 {snapshot.AliveCount}）");

            if (!NetworkMgr.IsMasterClient) return;

            // 盲窗内死亡且未被回收的尸体：补发销毁（死亡流程的销毁环节在槽位失明时被跳过）
            foreach (var fact in facts)
            {
                if (fact.Template.EncounterId != snapshot.EncounterId || !fact.IsDead) continue;
                Logging.Info($"[MonsterSpawnerSystem] 遭遇 {snapshot.EncounterId} 对账回收盲窗尸体 ({fact.Id})");
                Msger.Send(MsgID.DestroyMonster, fact.Id);
            }

            state.LastReportedAlive = -1;
            ReportAliveCount(state);
            TryReportGroupWiped(state);
        }

        #endregion

        #region State

        /// <summary> 遭遇组状态（槽位事实全端维护；批次决策仅 MC 端执行） </summary>
        private sealed class EncounterGroupState
        {
            public int EncounterId;
            public EntityId SessionId;
            public CfgMonsterspawn Cfg;

            /// <summary> 槽位状态（下标 = MonsterIndex，长度 = 怪物组大小） </summary>
            public SlotState[] Slots;

            /// <summary> 槽位在世怪物 Id（Alive 槽有效） </summary>
            public EntityId[] SlotIds;

            /// <summary> Pending 槽的请求时刻（确认超时判定） </summary>
            public float[] RequestTimes;

            /// <summary> 会话相位 / 代际的最近视图（StateChanged / Spawned 事件维护） </summary>
            public EncounterPhase LastPhase;
            public int LastGeneration;

            /// <summary> 批次节奏（单只间隔） </summary>
            public float NextSpawnTime;

            /// <summary> 上次上报的存活计数（值未变不重发；MC 更换后强制重报） </summary>
            public int LastReportedAlive = -1;

            /// <summary> 全灭已上报标记（代际重置清零，防重复发送） </summary>
            public bool WipeReported;

            /// <summary> 槽位补齐等待中（会话创建时快照存活 > 0：等乱序怪物事件追平，期间抑制 MC 决策） </summary>
            public bool AwaitingResync;

            /// <summary> 补齐窗口起点（超时兜底） </summary>
            public float ResyncSince;

            /// <summary> 补齐目标存活数（会话快照 AliveCount） </summary>
            public int ResyncTargetAlive;
        }

        #endregion
    }
}
