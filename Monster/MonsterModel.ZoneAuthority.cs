using Framework;
using Fusion;
using Game.Components;

namespace Game.Entities
{
    /// <summary>
    /// Monster 权威域规则 partial（§16.3-§16.5）：区域选举级联（ApplyZoneAuthority 唯一写入入口）。
    /// 怪物不可搬运、无接收豁免——级联无交互钳制分支。
    /// 全部方法仅在 SA 端有效——权威事实单写者【硬约束】。
    /// </summary>
    public partial class MonsterModel
    {
        /// <summary>
        /// 领域写入唯一入口（SA 端采样级联）：换区写 HomeZoneId → owner 变化写 owner →
        /// 冻结计时起 / 清（§1.4 级联内聚；怪物无交互钳制分支）。
        /// </summary>
        public void ApplyZoneAuthority(int homeZoneId, EntityId owner)
        {
            if (!HasStateAuthority) return;

            if (HomeZoneId != homeZoneId)
            {
                HomeZoneId = homeZoneId;
                OnHomeZoneIdChanged?.Invoke(HomeZoneId);
            }

            WriteOwner(owner);
        }

        /// <summary>
        /// owner 写入 + 冻结计时级联（SA 端内部）：维护不变式
        /// 「ShouldFrozenTimerRun(owner) == FrozenSinceTick.IsRunning」
        /// （单一事实双语义：owner 无效 = 冻结 = AI 关停 + TTL 起表，§16.5）。
        /// </summary>
        private void WriteOwner(EntityId owner)
        {
            if (DesiredAuthorityOwner != owner)
            {
                DesiredAuthorityOwner = owner;
                OnDesiredAuthorityOwnerChanged?.Invoke(DesiredAuthorityOwner);
            }

            bool shouldRun = ZoneAuthorityRules.ShouldFrozenTimerRun(owner, receptionValid: false);
            if (shouldRun == FrozenSinceTick.IsRunning) return;

            FrozenSinceTick = shouldRun ? TickTimer.CreateFromSeconds(Runner, FrozenDespawnDelay) : default;
            OnFrozenSinceTickChanged?.Invoke(FrozenSinceTick.IsRunning);
        }
    }
}
