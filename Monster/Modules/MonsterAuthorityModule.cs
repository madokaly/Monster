using System;
using Framework;
using Framework.Core;
using Fusion;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterAuthorityModuleConfig
    {
        [Tooltip("怪物根 NetworkObject（权威请求 / 释放用）")]
        public NetworkObject RootObject;
    }

    /// <summary>
    /// 怪物权威管理模块：监听 Model 的 [Networked] DesiredAuthorityOwner 事实，
    /// 各端本地自治反应——期望归属 == 本端玩家实体（MainPlayer 或本端玩家驾驶的 Mech）
    /// 且 无权威 → RequestStateAuthority
    /// （Shared 模式请求方直接夺取，当前持有者无需显式 release——
    /// 旧 release 分支在 MC 重选时 release 回弹到 MC 会形成 ping-pong，已移除）。
    /// 归属决策由 MonsterSpawnerSystem（Host 端）做出并写事实，本模块只执行反应。
    /// </summary>
    public class MonsterAuthorityModule : ModuleBase
    {
        private readonly MonsterModel _model;
        private readonly MonsterAuthorityModuleConfig _config;

        #region Lifecycle

        public MonsterAuthorityModule(MonsterModel model, MonsterAuthorityModuleConfig config)
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

        protected override void OnStateAuthorityChanged()
        {
            // 权威易主后重新评估归属事实（对齐当前期望归属）
            ReactToDesiredOwner(_model.DesiredAuthorityOwner);
        }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;

            _model.OnDesiredAuthorityOwnerChanged += OnDesiredAuthorityOwnerChangedHandler;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;

            _model.OnDesiredAuthorityOwnerChanged -= OnDesiredAuthorityOwnerChangedHandler;
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

        #region Model Handlers

        private void OnDesiredAuthorityOwnerChangedHandler(EntityId ownerId)
        {
            if (_config is null) return;
            if (_config.RootObject == null) return;

            // 全端反应类 handler：不加权威检查（这正是本模块的职责）
            ReactToDesiredOwner(ownerId);
        }

        #endregion

        #region Private Methods

        private void ReactToDesiredOwner(EntityId ownerId)
        {
            if (!ownerId.IsValid) return; // 未指派归属，保持现状

            var rootObject = _config.RootObject;
            if (rootObject == null || !rootObject.IsValid) return;

            // 归属认领判据：owner 是玩家实体身份（Spawner 可能指派 MainPlayer 或玩家驾驶的 Mech）
            var localPlayerId = Svcer.Req<EntityId>(SvcID.QueryLocalPlayer);
            var localMechId = Svcer.Req<EntityId>(SvcID.QueryLocalMech);

            if (ownerId != localPlayerId && ownerId != localMechId) return;

            // 期望归属是本端玩家实体：没有权威则请求
            // （Shared 模式 + prefab AllowStateAuthorityOverride 已勾选：请求直接夺取，持有者无需显式 release）
            if (!rootObject.HasStateAuthority)
            {
                Logging.Info(
                    $"[MonsterAuthorityModule] 期望归属为本端玩家实体 ({ownerId})，请求权威 ({_model.Id})"
                );
                rootObject.RequestStateAuthority();
            }
        }

        #endregion
    }
}
