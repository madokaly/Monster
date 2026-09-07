using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Framework.Network;
using Fusion;
using Game.DTOs;
using UnityEngine;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Game.Entities
{
    /// <summary>
    /// Monster 实体系统：生命周期入口、消息 / 服务路由、Factory 管理者（世界系统的一种，§0.1.4）。
    /// 由 GameWorld（世界根）创建与销毁。
    /// </summary>
    public class MonsterSystem
    {
        private readonly MsgerGroup _bus = new();
        private readonly MonsterFactory _factory = new();
        private readonly Dictionary<EntityId, MonsterCtrl> _monsterCtrls = new();

        #region Lifecycle

        public MonsterSystem()
        {
            RegisterListeners();
            RegisterProviders();
        }

        public void Dispose()
        {
            _bus?.Clear();
            _factory?.Dispose();
        }

        #endregion

        #region Registers

        private void RegisterListeners()
        {
            if (_bus is null) return;

            _bus.AddListener(MsgID.CreateMonster, OnCreateMonster);
            _bus.AddListener(MsgID.DestroyMonster, OnDestroyMonster);
            _bus.AddListener(MsgID.DestroyZoneEntity, OnDestroyZoneEntity);
            _bus.AddListener(MsgID.MonsterSpawned, OnMonsterSpawned);
            _bus.AddListener(MsgID.MonsterDespawned, OnMonsterDespawned);

            _bus.AddListener(MsgID.ApplyDamage, OnApplyDamage);
            _bus.AddListener(MsgID.ApplyHit, OnApplyHit);
        }

        private void RegisterProviders()
        {
            if (_bus is null) return;
        }

        #endregion

        #region Listeners

        private void OnCreateMonster(MsgID id, object data)
        {
            if (data is object[] { Length: 3 } datas
                && datas[0] is MonsterTemplate template
                && datas[1] is Vector3 position
                && datas[2] is Quaternion rotation)
            {
                CreateMonsterAtPositionAsync(template, position, rotation).Forget();
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnDestroyMonster(MsgID id, object data)
        {
            if (data is EntityId entityId)
            {
                DestroyMonster(entityId);
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnDestroyZoneEntity(MsgID id, object data)
        {
            if (data is EntityId expStoneId)
            {
                if (!_monsterCtrls.ContainsKey(expStoneId)) return;
                DestroyMonster(expStoneId);
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnMonsterSpawned(MsgID id, object data)
        {
            if (data is object[] { Length: 2 } datas && datas[0] is EntityId entityId && datas[1] is int spawnId)
            {
                if (!NetworkMgr.TryFindObjectOfType(entityId.NetId, out MonsterCtrl ctrl))
                {
                    Logging.Error($"[MonsterSystem] OnMonsterSpawned: Runner 找不到目标实体 ({entityId})");
                    return;
                }

                _monsterCtrls[entityId] = ctrl;
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnMonsterDespawned(MsgID id, object data)
        {
            if (data is EntityId entityId)
            {
                _monsterCtrls.Remove(entityId);
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnApplyDamage(MsgID id, object data)
        {
            if (data is object[] { Length: 2 } datas
                && datas[0] is EntityId targetId
                && datas[1] is DamageData damageData)
            {
                if (!_monsterCtrls.TryGetValue(targetId, out var ctrl)) return;

                ctrl.RequestApplyDamage(damageData);
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        private void OnApplyHit(MsgID id, object data)
        {
            if (data is object[] { Length: 2 } datas && datas[0] is EntityId targetId && datas[1] is HitData hitData)
            {
                if (!_monsterCtrls.TryGetValue(targetId, out var ctrl)) return;

                ctrl.RequestApplyHit(hitData);
                return;
            }

            Logging.Error($"MsgID.{id}: 数据类型不匹配");
        }

        #endregion

        #region Create API

        /// <summary>
        /// 创建怪物（公开 API）。
        /// 业务守卫：怪物是共享网络对象，仅 MasterClient 端允许创建，防止多端重复生成（但权威会在创建后转交给区域占领者）。
        /// </summary>
        public async UniTask<MonsterCtrl> CreateMonsterAsync(MonsterTemplate template)
        {
            if (!NetworkMgr.IsMasterClient)
            {
                Logging.Warning(
                    $"[MonsterSystem] CreateMonsterAsync: 仅 MasterClient 端允许创建怪物 (cfgId: {template.CfgId})"
                );
                return null;
            }

            return await _factory.CreateAsync(template);
        }

        /// <summary>
        /// 创建怪物（公开 API）。
        /// </summary>
        public async UniTask<MonsterCtrl> CreateMonsterAtPositionAsync(
            MonsterTemplate template,
            Vector3 position,
            Quaternion rotation)
        {
            var ctrl = await CreateMonsterAsync(template);
            if (ctrl == null) return null;

            ctrl.RequestTeleport(position, rotation);
            return ctrl;
        }

        /// <summary>
        /// 销毁怪物（公开 API）。
        /// 销毁来源含权威端自身的实体级计时（冻结 TTL，§16.5），不再限定 MC——
        /// 受理不做端守卫；实际 Despawn 经 MonsterCtrl.RequestDespawn 跨端路由到怪物权威端执行
        /// （怪物权威归属附近玩家，发起方可能不是权威端——统一销毁模式见规范 §8）。
        /// </summary>
        public void DestroyMonster(EntityId entityId)
        {
            if (!_monsterCtrls.TryGetValue(entityId, out var ctrl))
            {
                Logging.Error($"[MonsterSystem] DestroyMonster: 找不到实体缓存 ({entityId})，无法销毁");
                return;
            }

            ctrl.RequestDespawn();
        }

        #endregion
    }

    #region Factory

    /// <summary>
    /// Monster Factory：实体实例化与资源生命周期管理（仅由 MonsterSystem 持有与调用）。
    /// </summary>
    public class MonsterFactory
    {
        private readonly Dictionary<int, AsyncOperationHandle<GameObject>> _cachedHandles = new();

        public async UniTask<MonsterCtrl> CreateAsync(MonsterTemplate template)
        {
            int cfgId = template.CfgId;

            // Monster Cfg
            var cfg = ConfigMgr.Tables.TbMonsterstats.GetOrDefault(cfgId);
            if (cfg is null)
            {
                Logging.Error($"[MonsterFactory] CreateAsync: 找不到配置 (tbmonsterstats: {cfgId})");
                return null;
            }

            NetworkObject netObj = null;
            AsyncOperationHandle<GameObject> handle = default;

            try
            {
                // 解析路径（按 cfg 配置的 prefab 路径）
                string path = cfg.Path;
                if (string.IsNullOrEmpty(path))
                {
                    Logging.Error($"[MonsterFactory] CreateAsync: 配置缺少 prefab 路径 (cfgId: {cfgId})");
                    HandleFailure();
                    return null;
                }

                // 加载预制体（按 cfgId 缓存）
                if (!_cachedHandles.TryGetValue(cfgId, out handle) || !handle.IsValid() || handle.Result == null)
                {
                    handle = await ResourceMgr.LoadAssetAsync<GameObject>(path);
                    _cachedHandles[cfgId] = handle;
                }

                if (!handle.IsValid() || handle.Result == null)
                {
                    Logging.Error($"[MonsterFactory] CreateAsync: 加载预制体失败 ({cfgId}), Path: {path}");
                    HandleFailure();
                    return null;
                }

                // 生成网络对象
                netObj = await NetworkMgr.SpawnAsync(handle.Result, onBeforeSpawned: OnBeforeSpawned);
                if (netObj == null)
                {
                    Logging.Error($"[MonsterFactory] CreateAsync: 生成网络对象失败 ({cfgId}), Path: {path}");
                    HandleFailure();
                    return null;
                }

                // 获取 Ctrl
                if (!netObj.TryGetComponent<MonsterCtrl>(out var ctrl))
                {
                    Logging.Error($"[MonsterFactory] CreateAsync: 缺少 MonsterCtrl ({cfgId}, Path: {path})");
                    HandleFailure();
                    return null;
                }

                return ctrl;
            }
            catch (Exception ex)
            {
                Logging.Error($"[MonsterFactory] CreateAsync: {ex}");
                HandleFailure();
                return null;
            }

            void OnBeforeSpawned(NetworkRunner runner, NetworkObject no)
            {
                if (no.TryGetComponent(out MonsterCtrl ctrl))
                {
                    // 配置模板由 System 传入（Spawn 前仅权威端写入 [Networked] 首帧状态）
                    ctrl.PreInit(template);
                }
            }

            void HandleFailure()
            {
                if (netObj != null) NetworkMgr.Despawn(netObj);
                if (handle.IsValid()) handle.Release();
                _cachedHandles.Remove(cfgId);
            }
        }

        // 销毁不经过 Factory（统一销毁模式，§8）：System 受理销毁请求后调 ctrl.RequestDespawn()，
        // 由怪物权威端执行 Despawn；Factory 仅保留生成与生成失败清理（HandleFailure）。

        public void Dispose()
        {
            foreach (var handle in _cachedHandles.Values)
            {
                if (handle.IsValid()) handle.Release();
            }
            _cachedHandles.Clear();
        }
    }

    #endregion
}
