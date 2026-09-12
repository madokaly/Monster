using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Network;
using Game.DTOs;
using MechaServer.Assets;
using MechaServer.Sandbox;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Game.Entities
{
    /// <summary>
    /// 怪物掉落编排系统（世界系统·纯逻辑类）：击杀上报与掉落生成决策。
    /// 由 GameWorld（世界根）创建与销毁。
    ///
    /// 职责（决策，不执行）：
    /// - 击杀上报（仅 MC）：监听 MonsterDied → 归属玩家 EntityId → userId（Svcer）→ 100 米拆分
    ///   inside / outside → KillMonsterAsync（"世界事件上报类"接口，直调）；
    /// - 碎骸掉落（仅 MC）：monsterCfg.Debris 配置驱动散布生成 CreateMonsterDebris
    ///   （决策层收缩：MonsterDebrisSpawnerSystem 已废除，职责并入本系统）；
    /// - 死亡方位记录：全端维护（怪物组槽位索引 → 死亡位置），供响应到达后关联掉落生成点；
    /// - 响应处理：Server_SandboxKillMonster 各端生成共享掉落（LocalDrop），MC 生成非共享场景物品（经验石）。
    /// 生成执行一律经总线命令（CreateLocalDrop / CreateMonsterDebris / CreateExpStone / CreateBlindBox /
    /// CreateMechPart / CreateMechFigure），收口在目标实体 System / Factory（执行收口）。
    /// </summary>
    public class MonsterDropSystem
    {
        /// <summary> 击杀掉落：范围内判定半径（米）</summary>
        private const float KILL_RANGE = 100f;

        /// <summary> 碎骸生成高度（死亡点上方，米） </summary>
        private const float DEBRIS_SPAWN_HEIGHT = 1f;

        /// <summary> 碎骸散布半径（死亡点周围，米） </summary>
        private const float DEBRIS_SPREAD_RADIUS = 1.2f;

        private readonly MsgerGroup _bus = new();

        /// <summary> 死亡方位记录（怪物组槽位索引 → 死亡位置/朝向；覆盖写，响应消费时弹出，票 28 SpawnId 收口） </summary>
        private readonly Dictionary<int, (Vector3 position, Quaternion rotation)> _recentDeaths = new();

        #region Lifecycle

        public MonsterDropSystem()
        {
            RegisterListeners();
        }

        public void Dispose()
        {
            _bus?.Clear();
        }

        #endregion

        #region Registers

        private void RegisterListeners()
        {
            if (_bus is null) return;

            _bus.AddListener(MsgID.MonsterDied, OnMonsterDied);
            _bus.AddListener(MsgID.Server_SandboxKillMonster, OnSandboxKillMonsterResponse);
        }

        #endregion

        #region Listeners

        private void OnMonsterDied(MsgID id, object data)
        {
            if (data is object[] { Length: 6 } datas
                && datas[0] is EntityId entityId
                && datas[1] is MonsterTemplate template
                && datas[2] is Vector3 position
                && datas[3] is Quaternion rotation
                && datas[4] is IReadOnlyList<DamageAttribution> attackers
                && datas[5] is EntityId)
            {
                int cfgId = template.CfgId;
                int monsterIndex = template.MonsterIndex;

                // 全端记录死亡方位（响应经流推送，按组内索引关联回掉落生成点，票 28 SpawnId 收口）
                _recentDeaths[monsterIndex] = (position, rotation);

                // 仅 MC 发起击杀上报（服务端据此结算掉落并推流给房间全体）
                if (!NetworkMgr.IsMasterClient) return;

                var monsterCfg = ConfigMgr.Tables.TbMonsterstats.GetOrDefault(cfgId);
                if (monsterCfg == null)
                {
                    Logging.Error($"[MonsterDropSystem] OnMonsterDied: tbmonsterstats 找不到配置 ({cfgId})");
                    return;
                }

                // 碎骸掉落（MC 一次、全端可见）：配置驱动，与击杀上报相互独立
                SpawnDebris(monsterCfg, position, rotation);

                // 无掉落组配置的怪物不发起上报（掉落表是唯一触发判据，内容解读在服务端）
                if (monsterCfg.DropGroup == 0) return;

                SendKillMonsterAsync(cfgId, template.EncounterId, monsterIndex, position, attackers).Forget();
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        /// <summary>
        /// 击杀响应（每连接个性化，NetworkMgr 流 → 本地总线）：各端生成自己的共享掉落（LocalDrop），
        /// MC 生成非共享场景物品（经验石网络实体）。
        /// </summary>
        private void OnSandboxKillMonsterResponse(MsgID id, object data)
        {
            if (data is not KillMonsterResponse rsp)
            {
                Logging.Error($"MsgID.{id}: 数据类型不匹配");
                return;
            }

            // 死亡方位关联（响应经流推送；按组内索引取最近死亡记录并弹出）
            if (!TryConsumeDeathRecord(rsp.DeadMonster.MonsterIndex, out var position, out var rotation))
            {
                Logging.Error(
                    $"[MonsterDropSystem] OnSandboxKillMonsterResponse: 找不到死亡方位记录 (monsterIndex: {rsp.DeadMonster.MonsterIndex})"
                );
                return;
            }

            Logging.Debug(
                $"[MonsterDropSystem] OnSandboxKillMonsterResponse: 怪物被击杀，开始生成掉落物。SceneBoxes(Count={rsp.SceneBoxes.Count}), SceneComponents(Count={rsp.SceneComponents.Count}, SceneItems(Count={rsp.SceneItems.Count})"
            );

            // 共享掉落（本端检定结果）：范围内（saved=true）捡取纯溶解；范围外（saved=false）拾取走 PickSceneItem
            foreach (var box in rsp.SceneBoxes)
            {
                if (box?.Box == null) continue;

                var template = new LocalDropTemplate(
                    box.SceneId,
                    box.Box.Id,
                    box.Box.TemplateId,
                    0,
                    LocalDropKind.BlindBox,
                    box.Saved,
                    spawnAbandoned: true
                );
                Msger.Send(MsgID.CreateLocalDrop, template, position, rotation);
            }

            foreach (var component in rsp.SceneComponents)
            {
                if (component?.Component == null) continue;

                LocalDropKind kind;
                switch (component.Component.SlotType)
                {
                    case ComponentType.LeftHand:
                        kind = LocalDropKind.PartLeftArm;
                        break;
                    case ComponentType.RightHand:
                        kind = LocalDropKind.PartRightArm;
                        break;
                    case ComponentType.Body:
                        kind = LocalDropKind.FigureBody;
                        break;
                    default:
                        Logging.Warning(
                            $"[MonsterDropSystem] OnSandboxKillMonsterResponse: 不支持的部件槽位类型 "
                            + $"({component.Component.SlotType}, sceneId: {component.SceneId})"
                        );
                        continue;
                }

                var template = new LocalDropTemplate(
                    component.SceneId,
                    component.Component.Id,
                    component.Component.TemplateId,
                    component.Component.AvatarId,
                    kind,
                    component.Saved,
                    spawnAbandoned: true
                );
                Msger.Send(MsgID.CreateLocalDrop, template, position, rotation);
            }

            // 非共享场景物品（经验石）：仅 MC 生成网络实体（ExpStoneSystem 带 MC 守卫，恰好一次）
            if (NetworkMgr.IsMasterClient)
            {
                SpawnSceneItems(rsp.SceneItems, position);
            }

            // rsp.Item（货币 / 武器进化材料等可共享物品）：本期不消费——
            // 范围内玩家到账走 EventDropGrant → ProfService.SetItemCount 现有链路。
            // 未知类型（如 SCRAP 废料堆，待独立系统设计）由下方 SceneItemType 守卫跳过
        }

        /// <summary>
        /// 生成场景掉落物（两类分发模型不同，KillMonsterResponse 推送全房间但内容按端区分）：
        /// - 经验石：各端响应均含 → 各端都发 CreateExpStone，ExpStoneSystem 的 MC 守卫去重
        ///   （网络实体，全端可见，先到先得）；
        /// - 同步率升级芯片：共享掉落个人判定，芯片条目仅出现在得主端响应（各端收到的内容不同）
        ///   → 本地实体生成，仅本端可见，无先到先得竞争。
        /// 死亡点附近 XZ ±2m 随机散开（与废弃版本一致）。
        /// </summary>
        private void SpawnSceneItems(IReadOnlyList<SceneItemInfo> sceneItems, Vector3 position)
        {
            if (sceneItems == null) return;

            foreach (var item in sceneItems)
            {
                if (item == null) continue;

                // 同步率升级芯片：共享掉落个人判定，条目仅本端（得主端）响应中存在，
                // 本地实体仅本端可见（id = TbItem 610300001）
                if (item.Type == SceneItemType.SyncChip)
                {
                    int chipTotal = Mathf.Max(1, item.Count);
                    for (int i = 0; i < chipTotal; i++)
                    {
                        var chipOffset = new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
                        Msger.Send(
                            MsgID.CreateSyncChip,
                            new SyncChipTemplate(item.Id, spawnAbandoned: true),
                            position + chipOffset,
                            Quaternion.identity
                        );
                    }
                    continue;
                }

                if (item.Type != SceneItemType.Exp)
                {
                    Logging.Warning(
                        $"[MonsterDropSystem] SpawnSceneItems: SceneItemType 暂未支持 ({(int)item.Type}, id: {item.Id})"
                    );
                    continue;
                }

                int total = Mathf.Max(1, item.Count);
                for (int i = 0; i < total; i++)
                {
                    var offset = new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
                    var template = new ExpStoneTemplate(item.Id, spawnAbandoned: true);
                    Msger.Send(MsgID.CreateExpStone, template, position + offset, Quaternion.identity);
                }
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 掉落碎骸（死亡结算的延续）：按怪物配置 debris 数组全量生成，配置驱动（debris 为空不掉）。
        /// 每块碎骸在死亡点上方 DEBRIS_SPAWN_HEIGHT 米、360°/N 均分角度、DEBRIS_SPREAD_RADIUS 半径散布，
        /// 无初速度自由落体；创建命令经总线 → MonsterDebrisSystem 受理。
        /// 碎骸是击杀掉落物：来源标志恒 true，生成即入遗弃表（来源化入表，票 17）。
        /// </summary>
        private void SpawnDebris(cfg.CfgMonsterstats monsterCfg, Vector3 deathPosition, Quaternion deathRotation)
        {
            if (monsterCfg?.Debris is not { Length: > 0 }) return;

            int count = monsterCfg.Debris.Length;
            for (int i = 0; i < count; i++)
            {
                var entry = monsterCfg.Debris[i];
                if (entry == null) continue;

                float angle = 360f / count * i * Mathf.Deg2Rad;
                var offset = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * DEBRIS_SPREAD_RADIUS;
                var position = deathPosition + Vector3.up * DEBRIS_SPAWN_HEIGHT + offset;

                var template = new MonsterDebrisTemplate(monsterCfg.Id, entry.Type, spawnAbandoned: true);
                Msger.Send(MsgID.CreateMonsterDebris, template, position, deathRotation);
            }
        }

        /// <summary>
        /// 击杀上报：归属玩家 EntityId → userId（Svcer）→ 100 米拆分 inside / outside → KillMonsterAsync。
        /// 玩家已离线（或非玩家攻击）→ 跳过；全部无效则不上报。
        /// </summary>
        private async UniTask SendKillMonsterAsync(
            int cfgId,
            int spawnId,
            int monsterIndex,
            Vector3 deathPosition,
            IReadOnlyList<DamageAttribution> attackers)
        {
            var request = new KillMonsterRequest
            {
                // MonsterIndex = 遭遇怪物组槽位（防作弊键：同组同索引不可能连续击杀，票 28 语义澄清）；
                DeadMonster = new SceneMonster { SpawnId = spawnId, MonsterIndex = monsterIndex },
            };

            foreach (var attacker in attackers)
            {
                EntityId attackerId = attacker.AttackerId;
                if (!attackerId.IsValid) continue;

                string serverUserId = Svcer.Req<string>(SvcID.QueryPlayerServerUserId, attackerId);
                if (string.IsNullOrEmpty(serverUserId)) continue;

                bool inside = false;
                if (Svcer.TryReq(SvcID.QueryPlayerPosition, out Vector3 attackerPosition, attackerId))
                {
                    inside = Vector3.Distance(attackerPosition, deathPosition) <= KILL_RANGE;
                }

                if (inside)
                    request.InsidePlayers.Add(serverUserId);
                else
                    request.OutsidePlayers.Add(serverUserId);
            }

            if (request.InsidePlayers.Count == 0 && request.OutsidePlayers.Count == 0)
            {
                Logging.Info($"[MonsterDropSystem] SendKillMonsterAsync: 无有效参与玩家，跳过击杀上报 (cfgId: {cfgId})");
                return;
            }

            Logging.Info(
                $"[MonsterDropSystem] SendKillMonsterAsync: cfgId={cfgId}, monsterIndex={monsterIndex}, "
                + $"inside={request.InsidePlayers.Count}, outside={request.OutsidePlayers.Count}"
            );

            // 响应不在此返回（服务端经 SubscribeServer 流推送，见 NetworkMgr.Server.Stream.OnSandboxKillMonster）
            await NetworkMgr.Instance.KillMonsterAsync(request);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 取出并消费死亡方位记录（响应关联用；不存在返回 false）。键 = 遭遇怪物组槽位索引，票 28 SpawnId 收口。
        /// </summary>
        public bool TryConsumeDeathRecord(int monsterIndex, out Vector3 position, out Quaternion rotation)
        {
            if (_recentDeaths.TryGetValue(monsterIndex, out var record))
            {
                _recentDeaths.Remove(monsterIndex);
                position = record.position;
                rotation = record.rotation;
                return true;
            }

            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        #endregion
    }
}
