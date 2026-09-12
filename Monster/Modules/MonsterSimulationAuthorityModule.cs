using System.Collections.Generic;
using Framework;
using Framework.Core;
using Game.Components;
using Game.DTOs;

namespace Game.Entities
{
    /// <summary>
    /// 遭遇怪物的模拟资格：距离从遭遇配置派生，候选和现任必须位于遭遇主 Chunk。
    /// 复用逐实体选举、Ready 自查与 Allocator 转权；不改变巡逻和脱战范围。
    /// </summary>
    public class MonsterSimulationAuthorityModule : SimulationAuthorityModule
    {
        private const float RETENTION_MARGIN = 30f;

        private readonly MonsterModel _model;

        public MonsterSimulationAuthorityModule(MonsterModel model, SimulationAuthorityModuleConfig config)
            : base(model, config)
        {
            _model = model;
        }

        protected override float AcquisitionRadius => _model?.EncounterCfg?.ActivationRange ?? 0f;

        protected override float RetentionRadius => AcquisitionRadius + RETENTION_MARGIN;

        protected override bool IsCandidateAllowed(in PlayerPresenceSnapshot snapshot)
        {
            var tables = ConfigMgr.Tables;
            return tables != null && _model != null
                && tables.TbMonsterspawn.IsInEncounterChunk(_model.EncounterCfg, snapshot.Position, tables.TbChunk);
        }

        protected override bool CanReceiveAuthority(EntityId owner)
        {
            var snapshots = Svcer.Req<List<PlayerPresenceSnapshot>>(SvcID.QueryPlayerPresenceSnapshots);
            if (snapshots == null) return false;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.MainPlayerId == owner)
                    return snapshot.IsOnline && IsCandidateAllowed(snapshot);
            }
            return false;
        }
    }
}
