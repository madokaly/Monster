using Framework;
using Fusion;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// Monster Model 战斗领域规则：伤害 / 治疗 / 受控反应 / 破防 / 死亡 / 脱战。
    /// 公开 API 一律经主文件 Setter 落地（见 MonsterModel.cs）。
    /// </summary>
    public partial class MonsterModel
    {
        /// <summary> 受击反应节流（秒）——防止同一帧或极短时间内连续多次触发受击动画 </summary>
        private const float HIT_REACTION_COOLDOWN = 0.1f;

        /// <summary> 脱战冷却时长（秒）——战斗结束后原地无敌待机，超过才允许巡逻 </summary>
        private const float COMBAT_EXIT_COOLDOWN = 3f;

        /// <summary> 脱战回血：无目标累计时长门槛（秒）</summary>
        private const float OUT_OF_COMBAT_THRESHOLD = 3f;

        /// <summary> 脱战回血间隔（秒）</summary>
        private const float IDLE_REGEN_INTERVAL = 1f;

        /// <summary> 脱战回血比例（最大血量百分比）</summary>
        private const float IDLE_REGEN_PERCENT = 0.1f;

        #region API

        /// <summary>
        /// 受到伤害（领域规则内聚）：
        /// 无敌 / 死亡守卫 → 防御减免 → Hp / Guard 扣减 → 受击反应 → 死亡触发。
        /// </summary>
        public void TakeDamage(int damage, EntityId attacker)
        {
            if (!HasStateAuthority) return;
            if (IsDead()) return;
            if (IsInvulnerable) return;

            // 标记进入过战斗（覆盖远程打一下就跑的场景，回血资格永久成立）
            MarkInCombat();

            int finalDamage = Mathf.Max(damage - Def, 1);

            SetHp(Hp - finalDamage);
            SetGuard(Guard - finalDamage);

            if (IsDead()) return;

            // 受击反应（0.1s 节流 + 受控 / 施法守卫；Runner.Time 与 tick 对齐，权威易主后时间轴一致）
            if ((float)Runner.SimulationTime - _lastHitReactionTime >= HIT_REACTION_COOLDOWN
                && ActState is not (MonsterActState.Controlled or MonsterActState.Casting))
            {
                _lastHitReactionTime = (float)Runner.SimulationTime;

                var (hitAnimId, hitSound) = GetRandomHitRes();
                SetLastHitSound(hitSound);
                if (hitAnimId > 0)
                {
                    // 先 Timer 后 AnimId：Timer 是本层对动画的保护，先立起来再写（MonsterModel.Anim.cs 纪律）
                    SetHitAnimTimer(TickTimer.CreateFromSeconds(Runner, GetAnimLength(hitAnimId)));
                    SetAnimId(hitAnimId);
                }
            }
        }

        /// <summary>
        /// 眩晕（由技能模块经此请求，领域规则守卫：死亡中不可眩晕）
        /// </summary>
        public void TriggerStunned(int stunAnimId)
        {
            if (!HasStateAuthority) return;
            if (IsDead()) return;
            if (IsStunned()) return;

            InterruptCasting();
            SetHitAnimTimer(default);

            // 先 Timer 后 AnimId（MonsterModel.Anim.cs 纪律）
            SetStunnedAnimTimer(TickTimer.CreateFromSeconds(Runner, GetAnimLength(stunAnimId)));
            SetAnimId(stunAnimId);
        }

        /// <summary>
        /// 治疗（钳制到 MaxHp）
        /// </summary>
        public void Heal(int amount)
        {
            if (!HasStateAuthority) return;
            if (IsDead()) return;

            SetHp(Hp + amount);
        }

        /// <summary>
        /// 按最大血量百分比回血
        /// </summary>
        public void RegenHpByPercent(float percent = IDLE_REGEN_PERCENT)
        {
            if (!HasStateAuthority) return;
            if (IsDead() || IsHpFull()) return;

            int amount = Mathf.Max(1, Mathf.RoundToInt(MaxHp * percent));
            Heal(amount);
        }

        /// <summary>
        /// 标记进入过战斗
        /// </summary>
        public void MarkInCombat()
        {
            if (!HasStateAuthority) return;

            SetHasEverBeenInCombat(true);
        }

        /// <summary>
        /// 开始脱战冷却：无敌 + 原地待机，COMBAT_EXIT_COOLDOWN 秒后才允许巡逻
        /// </summary>
        public void StartCombatExitCooldown()
        {
            if (!HasStateAuthority) return;
            if (CombatExitTimer.IsRunning) return;

            SetCombatExitTimer(TickTimer.CreateFromSeconds(Runner, COMBAT_EXIT_COOLDOWN));
            SetIsInvulnerable(true);
        }

        /// <summary>
        /// 立即取消脱战冷却（新目标出现时）
        /// </summary>
        public void ClearCombatExitCooldown()
        {
            if (!HasStateAuthority) return;

            SetCombatExitTimer(default);
            SetIsInvulnerable(false);
        }

        /// <summary> 是否处于脱战冷却中 </summary>
        public bool IsCombatExitCooldown => CombatExitTimer.IsRunning;

        #endregion

        #region Private Methods

        /// <summary>
        /// 进入死亡（领域规则：终态，打断一切受控与施法）
        /// </summary>
        private void EnterDead()
        {
            InterruptCasting();
            SetHitAnimTimer(default);
            SetGuardBrokenAnimTimer(default);
            SetGuardBrokenRecoveryTimer(default);
            SetStunnedAnimTimer(default);

            // 死亡层的动画保护是 Hp 归零本身（ActState.Dead 为终态），DiedAnimTimer 只驱动爆炸时机
            SetDiedAnimTimer(TickTimer.CreateFromSeconds(Runner, DeadAnimLength));
            SetAnimId(DeadAnim);
        }

        /// <summary>
        /// 进入破防（领域规则：打断施法 → 破防动画 + 恢复计时）
        /// </summary>
        private void EnterGuardBroken()
        {
            InterruptCasting();
            SetHitAnimTimer(default);

            const float BREAK_RECOVERY_TIME = 15f;

            // 先 Timer 后 AnimId（MonsterModel.Anim.cs 纪律）
            SetGuardBrokenAnimTimer(TickTimer.CreateFromSeconds(Runner, GetAnimLength(BreakAnim)));
            SetGuardBrokenRecoveryTimer(TickTimer.CreateFromSeconds(Runner, BREAK_RECOVERY_TIME));
            SetAnimId(BreakAnim);
        }

        /// <summary>
        /// 随机取一个受击表现资源（动画 + 音效）
        /// </summary>
        private (int animId, string sound) GetRandomHitRes()
        {
            var cfg = Cfg;
            if (cfg is null || cfg.Hit is null || cfg.Hit.Length == 0)
            {
                return (-1, string.Empty);
            }

            var hit = cfg.Hit[UnityEngine.Random.Range(0, cfg.Hit.Length)];
            return (hit.Animation, hit.Sound ?? string.Empty);
        }

        /// <summary>
        /// 战斗域计时器结算（FUN，权威端）。
        /// 到期 → 清计时器 → 结算后续规则（破防恢复 / 脱战冷却结束）。
        /// <para>
        /// 受控层计时器到期后<b>不需要</b>补写动画：清零使 ActState 落回 Free，
        /// 同一 tick 内紧随其后的 TickAnim 会自动接管兵底层（见 MonsterModel.Anim.cs）。
        /// </para>
        /// </summary>
        private void UpdateCombatTimers()
        {
            if (HitAnimTimer.IsRunning && HitAnimTimer.Expired(Runner))
            {
                SetHitAnimTimer(default);
            }

            if (StunnedAnimTimer.IsRunning && StunnedAnimTimer.Expired(Runner))
            {
                SetStunnedAnimTimer(default);
            }

            if (GuardBrokenAnimTimer.IsRunning && GuardBrokenAnimTimer.Expired(Runner))
            {
                SetGuardBrokenAnimTimer(default);
            }

            if (GuardBrokenRecoveryTimer.IsRunning && GuardBrokenRecoveryTimer.Expired(Runner))
            {
                SetGuardBrokenRecoveryTimer(default);
                SetGuard(MaxGuard);
            }

            if (DiedAnimTimer.IsRunning && DiedAnimTimer.Expired(Runner))
            {
                // 死亡动画到期：清计时器触发真实下降沿（各端 BattleView 播爆炸；权威端广播 Monster_Died）
                SetDiedAnimTimer(default);
            }

            if (CombatExitTimer.IsRunning && CombatExitTimer.Expired(Runner))
            {
                ClearCombatExitCooldown();
            }
        }

        /// <summary>
        /// 脱战累计 + 脱战回血（权威端本地规则）。
        /// 守卫：进过战斗、未锁目标（感知快照）、非施法、非无敌、脱战时长达标。
        /// <para>
        /// 原先的「AiState 是 Chase」判据由 <see cref="PerceptionSnapshot"/>.HasAnyTarget 覆盖
        /// （追击态必然有目标）；「AiState 是 Idle / Patrol」判据由 ActState.Free 表达
        /// （脱战冷却期原本靠 IsInvulnerable 挡住，语义等价）。
        /// </para>
        /// </summary>
        private void UpdateOutOfCombat()
        {
            if (!HasEverBeenInCombat) return;
            if (IsDead()) return;

            if (PerceptionSnapshot.HasAnyTarget || ActState == MonsterActState.Casting)
            {
                // 被打断时重置累计（"无目标累计时长"语义：有目标 / 战斗态期间不累积）
                _outOfCombatElapsed = 0f;
                _idleRegenTimer = 0f;
                return;
            }

            _outOfCombatElapsed += Runner.DeltaTime;

            // 回血：仅自由态，非无敌，脱战时长达标
            if (ActState == MonsterActState.Free
                && !IsInvulnerable
                && _outOfCombatElapsed >= OUT_OF_COMBAT_THRESHOLD)
            {
                if (IsHpFull())
                {
                    _idleRegenTimer = 0f;
                    return;
                }

                _idleRegenTimer += Runner.DeltaTime;
                if (_idleRegenTimer >= IDLE_REGEN_INTERVAL)
                {
                    _idleRegenTimer = 0f;
                    RegenHpByPercent(IDLE_REGEN_PERCENT);
                }
            }
        }

        #endregion

        #region Helpers

        #endregion
    }
}
