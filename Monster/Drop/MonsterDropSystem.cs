using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Network;
using MechaServer.Assets;
using MechaServer.Sandbox;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Game.Entities
{
    /// <summary>
    /// 怪物掉落编排系统（世界系统·纯逻辑类，§0.1.5 / §15）：击杀上报与掉落生成决策。
    /// 由 GameWorld（世界根）创建与销毁。
    ///
    /// 职责（决策，不执行）：
    /// - 击杀上报（仅 MC）：监听 MonsterDied → 攻击者 EntityId → userId（Svcer）→ 100 米拆分
    ///   inside / outside → KillMonsterAsync（"世界事件上报类"接口，§5.4 直调）；
    /// - 死亡方位记录：全端维护（spawnId → 死亡位置），供响应到达后关联掉落生成点；
    /// - 响应处理：Server_SandboxKillMonster 各端生成共享掉落（LocalDrop），MC 生成非共享场景物品（经验石）。
    /// 生成执行一律经总线命令（CreateLocalDrop / CreateExpStone / CreateBlindBox / CreateMechPart /
    /// CreateMechFigure），收口在目标实体 System / Factory（§15.2 执行收口）。
    /// </summary>
    public class MonsterDropSystem
    {
        /// <summary> 击杀掉落：范围内判定半径（米）</summary>
        private const float KILL_RANGE = 100f;

        private readonly MsgerGroup _bus = new();

        /// <summary> 死亡方位记录（spawnId → 死亡位置/朝向；覆盖写，响应消费时弹出） </summary>
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
            if (data is object[] { Length: 5 } datas
                && datas[0] is EntityId entityId
                && datas[1] is MonsterTemplate template
                && datas[2] is Vector3 position
                && datas[3] is Quaternion rotation
                && datas[4] is IReadOnlyList<EntityId> attackers)
            {
                int spawnId = template.SpawnId;
                int cfgId = template.CfgId;
                int monsterIndex = template.MonsterIndex;

                // 全端记录死亡方位（响应经流推送，需按 spawnId 关联回掉落生成点）
                _recentDeaths[spawnId] = (position, rotation);

                // 仅 MC 发起击杀上报（服务端据此结算掉落并推流给房间全体）
                if (!NetworkMgr.IsMasterClient) return;

                var monsterCfg = ConfigMgr.Tables.TbMonsterstats.GetOrDefault(cfgId);
                if (monsterCfg == null)
                {
                    Logging.Error($"[MonsterDropSystem] OnMonsterDied: tbmonsterstats 找不到配置 ({cfgId})");
                    return;
                }

                // 无掉落组配置的怪物不发起上报（掉落表是唯一触发判据，内容解读在服务端）
                if (monsterCfg.DropGroup == 0) return;

                SendKillMonsterAsync(cfgId, spawnId, monsterIndex, position, attackers).Forget();
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

            // 死亡方位关联（响应经流推送；按 spawnId 取最近死亡记录并弹出）
            if (!TryConsumeDeathRecord(rsp.DeadMonster.SpawnId, out var position, out var rotation))
            {
                Logging.Error(
                    $"[MonsterDropSystem] OnSandboxKillMonsterResponse: 找不到死亡方位记录 (spawnId: {rsp.DeadMonster.SpawnId})"
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
                    box.Saved
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
                    component.Saved
                );
                Msger.Send(MsgID.CreateLocalDrop, template, position, rotation);
            }

            // 非共享场景物品（经验石）：仅 MC 生成网络实体（ExpStoneSystem 带 MC 守卫，恰好一次）
            if (NetworkMgr.IsMasterClient)
            {
                // 区域归属进 Template（§15.5.3）：掉落由死亡点经 spawnId → 配置表解析
                int regionId = ConfigMgr.Tables.TbMonsterspwan.GetOrDefault(rsp.DeadMonster.SpawnId)?.Region ?? 0;
                if (regionId == 0)
                {
                    Logging.Warning(
                        $"[MonsterDropSystem] OnSandboxKillMonsterResponse: 无法解析怪物 spawnId 的副本区域 "
                        + $"({rsp.DeadMonster.SpawnId})，经验石将以无区域生成"
                    );
                }

                SpawnSceneItems(rsp.SceneItems, position, regionId);
            }

            // rsp.Item（货币 / 武器进化材料等可共享物品）：本期不消费——
            // 范围内玩家到账走 EventDropGrant → ProfService.SetItemCount 现有链路；
            // 材料（item_type=3 非共享）未来需服务端扩展 SceneItemType 载体，TODO
        }

        /// <summary>
        /// 生成非共享场景物品：经验石（网络实体，先到先得）。死亡点附近 XZ ±2m 随机散开（与废弃版本一致）。
        /// regionId：死亡怪物所属副本区域（§15.5.3 区域归属进 Template）。
        /// </summary>
        private void SpawnSceneItems(IReadOnlyList<SceneItemInfo> sceneItems, Vector3 position, int regionId)
        {
            if (sceneItems == null) return;

            foreach (var item in sceneItems)
            {
                if (item == null) continue;

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
                    var template = new ExpStoneTemplate(item.Id, regionId);
                    Msger.Send(MsgID.CreateExpStone, template, position + offset, Quaternion.identity);
                }
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 击杀上报：攻击者 EntityId → userId（Svcer）→ 100 米拆分 inside / outside → KillMonsterAsync。
        /// 攻击者已销毁（离线 / 非机甲实体）→ 跳过；全部无效则不上报。
        /// </summary>
        private async UniTask SendKillMonsterAsync(
            int cfgId,
            int spawnId,
            int monsterIndex,
            Vector3 deathPosition,
            IReadOnlyList<EntityId> attackers)
        {
            var request = new KillMonsterRequest
            {
                // MonsterIndex：服务端语义为"出生点的第几只怪物"；现行出生槽位（HashSet<EntityId>）
                // 无索引语义，暂固定 0——若服务端需要精确索引，后续从 MonsterSpawnerSystem 槽位引入序号
                DeadMonster = new SceneMonster { SpawnId = spawnId, MonsterIndex = monsterIndex },
            };

            foreach (var attackerId in attackers)
            {
                if (!attackerId.IsValid) continue;

                string serverUserId = Svcer.Req<string>(SvcID.QueryMechOwnerServerUserId, attackerId);
                if (string.IsNullOrEmpty(serverUserId)) continue;

                bool inside = false;
                if (Svcer.TryReq(SvcID.QueryMechPosition, out Vector3 attackerPosition, attackerId))
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
                $"[MonsterDropSystem] SendKillMonsterAsync: cfgId={cfgId}, spawnId={spawnId}, "
                + $"inside={request.InsidePlayers.Count}, outside={request.OutsidePlayers.Count}"
            );

            // 响应不在此返回（服务端经 SubscribeServer 流推送，见 NetworkMgr.Server.Stream.OnSandboxKillMonster）
            await NetworkMgr.Instance.KillMonsterAsync(request);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// 取出并消费死亡方位记录（响应关联用；不存在返回 false）
        /// </summary>
        public bool TryConsumeDeathRecord(int spawnId, out Vector3 position, out Quaternion rotation)
        {
            if (_recentDeaths.TryGetValue(spawnId, out var record))
            {
                _recentDeaths.Remove(spawnId);
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
