using Framework;
using Game.Components;

namespace Game.Entities
{
    /// <summary>
    /// Monster 逐实体模拟权威域规则 partial（票 18）：归属写入唯一入口。
    /// 怪物不可搬运、无交互事实——级联无交互钳制分支（优先级对怪物不适用）。
    /// 规则裁决在 <see cref="SimulationAuthorityRules"/>（无状态），事实落地在这里；
    /// 全部方法仅在 SA 端有效——权威事实单写者【硬约束】。
    /// 无冻结销毁计时：SimulationOwner 无效 = 冻结停 AI / 移动，不是销毁依据——
    /// 销毁只由死亡或遭遇生命周期（EncounterSession 离场清场，票 20）驱动。
    /// </summary>
    public partial class MonsterModel
    {
        /// <summary>
        /// 归属写入唯一入口（SA 端采样级联）：离散值 no-op 早退 + 双轨事件。
        /// </summary>
        public void ApplySimulationOwner(EntityId owner)
        {
            if (!HasStateAuthority) return;

            WriteSimulationOwner(owner);
        }

        /// <summary> 归属事实写入（SA 端内部）：离散值 no-op 早退 + 双轨事件 </summary>
        private void WriteSimulationOwner(EntityId owner)
        {
            if (SimulationOwner == owner) return;

            SimulationOwner = owner;
            OnSimulationOwnerChanged?.Invoke(SimulationOwner);
        }
    }
}
