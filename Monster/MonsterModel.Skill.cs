using Framework;
using Framework.Core;
using Fusion;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 技能冷却槽状态（按链索引记）
    /// </summary>
    public struct MonsterSkillCooldownState : INetworkStruct
    {
        public int ChainIndex;
        public TickTimer CooldownTimer;
    }

    /// <summary>
    /// 落石步骤写入的本次落石快照（变体专属载荷，类型名携带归属）。
    /// 权威端选点后一次写入全端同步；消费方轮询 Seq 识别新一次落石；
    /// ExpireTick 为陨石驻场失效时刻（遁地撞石判定用，仅权威端消费）；
    /// BrokenMask 为已碎陨石位掩码（遁地撞石置位，陨石表现体轮询取用）。
    /// </summary>
    public struct MonsterRockfallState : INetworkStruct
    {
        /// <summary> 落石序号（每次施放 +1，0 = 从未落石；新施放掩码归零） </summary>
        public int Seq;

        /// <summary> 陨石落点（按石索引取用，未用槽位为 default） </summary>
        public Vector3 Anchor0;
        public Vector3 Anchor1;
        public Vector3 Anchor2;

        /// <summary> 陨石驻场失效时刻（Runner.Tick；近似取末石落地 + 驻场时长） </summary>
        public int ExpireTick;

        /// <summary> 已碎陨石位掩码（bit i = 第 i 石已碎；权威端撞石时置位，仅本 Seq 轮次内有效） </summary>
        public byte BrokenMask;
    }

    /// <summary>
    /// Monster Model 技能领域规则：链施放 / 中断 / 冷却（施放契约）。
    /// 公开 API 一律经主文件 Setter 落地（见 MonsterModel.cs）。
    /// </summary>
    public partial class MonsterModel
    {
        #region API

        /// <summary>
        /// 尝试施放技能链（AIModule 决策后调用）。
        /// 守卫：状态允许 / 链已装配 / 冷却就绪。
        /// 成功后写技能三件套（CastingChainIndex + CastCount 脉冲 + CastingSkillTimer）+ 施放目标，
        /// 各端链运行器监听 CastCount 变化后本地自治执行；步骤动画由运行器逐段写 AnimId。
        /// </summary>
        public bool TryCastChain(int chainIndex, EntityId targetId)
        {
            if (!HasStateAuthority) return false;
            if (IsDead()) return false;
            if (CastingSkillTimer.IsRunning) return false;
            if (!IsChainConfigured(chainIndex)) return false;
            if (!IsChainReady(chainIndex)) return false;

            var chain = GetChain(chainIndex);
            if (chain is null) return false;
            if (!CanCastChain(chainIndex, chain)) return false;

            SetCastingChainIndex(chainIndex);
            SetCastTargetId(targetId);

            // 施法时长 = max(链总时长派生, 动画收尾)（权威端以此为结算计时）。
            // 动画收尾 = 链级进入动画（施放起点起算）+ 各步骤进入动画（各自 StartOffset 起算）；
            // 数组随机取一 → 保守取最大动画时长（技能动画不截断）。
            float animCoverage = MaxAnimCoverage(chain.EnterAnimIds, 0f);
            for (int i = 0; i < chain.Steps.Count; i++)
            {
                var step = chain.Steps[i];
                if (step == null) continue;

                animCoverage = Mathf.Max(animCoverage, MaxAnimCoverage(step.StepAnimIds, step.StartOffset));
            }

            float castLength = Mathf.Max(0.01f, Mathf.Max(chain.CastDuration, animCoverage));

            // 先 Timer 后脉冲（MonsterModel.Anim.cs 纪律）：CastCount 会同步唤起链运行器，
            // 必须让 CastingSkillTimer 先立起来，施法层对动画的保护才在写入前生效。
            SetCastingSkillTimer(TickTimer.CreateFromSeconds(Runner, castLength));

            // 链级进入动画：施放起点随机取一
            WriteChainEnterAnim(chain);

            SetCastCount(CastCount + 1);

            return true;
        }

        /// <summary>
        /// 是否存在尚未消耗的生成后一次性链。
        /// </summary>
        public bool TryGetPendingSpawnOnceChain(out int chainIndex)
        {
            chainIndex = -1;
            if (CastCount != 0) return false;

            for (int i = 0; i < ChainCount; i++)
            {
                var chain = GetChain(i);
                if (chain is null || chain.CastTrigger != MonsterSkillCastTrigger.SpawnOnce) continue;
                if (!IsChainConfigured(i)) continue;

                chainIndex = i;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 登场链未消耗前，普通链不得抢放；登场链自身成功一次后也不得重放。
        /// </summary>
        private bool CanCastChain(int chainIndex, MonsterSkillChain chain)
        {
            if (TryGetPendingSpawnOnceChain(out int pendingSpawnOnceIndex))
            {
                return chainIndex == pendingSpawnOnceIndex;
            }

            return chain.CastTrigger != MonsterSkillCastTrigger.SpawnOnce || CastCount == 0;
        }

        /// <summary>
        /// 中断施法（受击死亡 / 破防 / 眩晕时调用）。
        /// 中断同样视为一次施放结束：记录链冷却与全局攻击冷却，避免同一技能被打断后立即重放。
        /// </summary>
        public void InterruptCasting()
        {
            if (!HasStateAuthority) return;
            if (!CastingSkillTimer.IsRunning) return;

            int interruptedChainIndex = CastingChainIndex;
            SetCastingSkillTimer(default);
            ResetCastInterruptPolicy();
            if (interruptedChainIndex >= 0)
            {
                StartChainCooldown(interruptedChainIndex);
            }
            SetCastingChainIndex(-1);
            SetCastTargetId(default);
        }

        /// <summary>
        /// 注册当前施法汇聚期的打断策略（权威端本地；抛射开始 / 施法结束 / 权威迁移时清理）。
        /// </summary>
        public void RegisterCastInterruptPolicy(int damageThreshold, int breakAnimId)
        {
            if (!HasStateAuthority) return;

            ResetCastInterruptPolicy();
            if (damageThreshold <= 0) return;

            _castInterruptDamageThreshold = damageThreshold;
            _castInterruptAnimId = breakAnimId;
            _castInterruptDamage = 0;
        }

        /// <summary>
        /// 清理当前施法汇聚期打断策略（权威端）。
        /// </summary>
        public void ClearCastInterruptPolicy()
        {
            if (!HasStateAuthority) return;

            ResetCastInterruptPolicy();
        }

        private void AccumulateCastInterruptDamage(int finalDamage)
        {
            int threshold = _castInterruptDamageThreshold;
            if (threshold <= 0 || finalDamage <= 0) return;

            bool reached = finalDamage >= threshold - _castInterruptDamage;
            _castInterruptDamage += finalDamage;
            if (!reached || IsDead() || IsStunned()) return;

            int breakAnimId = _castInterruptAnimId > 0 ? _castInterruptAnimId : BreakAnim;
            bool wasGuardBroken = IsGuardBroken();
            ResetCastInterruptPolicy();

            // Guard > 0 时由 SetGuard 归零触发破防；破防恢复期内 Guard 仍为 0，
            // SetGuard 是 no-op，需要显式重入破防流程以中断本次施法。
            SetGuard(0, breakAnimId);
            if (wasGuardBroken)
            {
                EnterGuardBroken(breakAnimId);
            }
        }

        private void ResetCastInterruptPolicy()
        {
            _castInterruptDamageThreshold = 0;
            _castInterruptAnimId = 0;
            _castInterruptDamage = 0;
        }

        /// <summary>
        /// 技能链冷却是否就绪（全局攻击冷却 + 链自身冷却）
        /// </summary>
        public bool IsChainReady(int chainIndex)
        {
            if (!HasStateAuthority) return false;

            if (GlobalAttackCooldownTimer.IsRunning && !GlobalAttackCooldownTimer.Expired(Runner)) return false;

            for (int i = 0; i < SKILL_COOLDOWN_CAPACITY; i++)
            {
                var state = SkillCooldownStates.Get(i);
                if (state.ChainIndex != chainIndex) continue;

                return !state.CooldownTimer.IsRunning || state.CooldownTimer.Expired(Runner);
            }

            return true;
        }

        /// <summary>
        /// 记录技能链冷却（施法结束时调用；冷却时长 = 链级 CooldownTime）
        /// </summary>
        public void StartChainCooldown(int chainIndex)
        {
            if (!HasStateAuthority) return;

            SetGlobalAttackCooldownTimer(TickTimer.CreateFromSeconds(Runner, Mathf.Max(0f, AttackCooldown)));

            var chain = GetChain(chainIndex);
            float chainCooldown = chain?.CooldownTime ?? 0f;

            int targetIndex = -1;
            for (int i = 0; i < SKILL_COOLDOWN_CAPACITY; i++)
            {
                var state = SkillCooldownStates.Get(i);
                if (state.ChainIndex == chainIndex)
                {
                    targetIndex = i;
                    break;
                }

                if (targetIndex < 0 && state.ChainIndex == -1)
                {
                    targetIndex = i;
                }
            }

            if (targetIndex < 0)
            {
                Logging.Warning($"[MonsterModel] StartChainCooldown: 技能冷却槽不足 (chainIndex: {chainIndex})");
                return;
            }

            SkillCooldownStates.Set(
                targetIndex,
                new MonsterSkillCooldownState
                {
                    ChainIndex = chainIndex,
                    CooldownTimer = TickTimer.CreateFromSeconds(Runner, Mathf.Max(0f, chainCooldown)),
                }
            );
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 写入链级进入动画：随机取一（空数组 / 非正 Id 跳过，保持当前动画）。
        /// 施放起点由权威端一次性决定并同步全端。
        /// </summary>
        private void WriteChainEnterAnim(MonsterSkillChain chain)
        {
            if (chain.EnterAnimIds is not { Length: > 0 }) return;

            int animId = chain.EnterAnimIds[UnityEngine.Random.Range(0, chain.EnterAnimIds.Length)];
            if (animId > 0)
            {
                SetAnimId(animId);
            }
        }

        /// <summary>
        /// 动画收尾覆盖：数组内最大动画时长（非正 Id 忽略）+ 起始偏移。
        /// </summary>
        private float MaxAnimCoverage(int[] animIds, float startOffset)
        {
            float max = 0f;
            if (animIds == null) return max;

            for (int i = 0; i < animIds.Length; i++)
            {
                int animId = animIds[i];
                if (animId <= 0) continue;

                max = Mathf.Max(max, startOffset + GetAnimLength(animId));
            }

            return max;
        }

        /// <summary>
        /// 施法计时器结算（FUN，权威端）。
        /// 到期 → 清计时器 → 记录链冷却 → 清施放身份。
        /// <para>
        /// 不需要补写动画：计时器清零使 ActState 落回 Free，
        /// 同一 tick 内紧随其后的 TickAnim 会自动接管兵底层（见 MonsterModel.Anim.cs）。
        /// MoveCommand 同理——AI 下一 tick 恢复重申自己的移动意图。
        /// </para>
        /// </summary>
        private void UpdateCastingTimer()
        {
            if (CastingSkillTimer.IsRunning && CastingSkillTimer.Expired(Runner))
            {
                SetCastingSkillTimer(default);

                // 施法收尾：冷却 + 清施放身份（并兜底清理汇聚打断策略）
                int finishedChainIndex = CastingChainIndex;
                StartChainCooldown(finishedChainIndex);

                ResetCastInterruptPolicy();
                SetCastingChainIndex(-1);
                SetCastTargetId(default);
            }
        }

        #endregion
    }
}
