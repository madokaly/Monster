using System;
using System.Collections.Generic;
using cfg;
using Framework;
using Framework.Core;
using Fusion;
using Game.DTOs;
using UnityEngine;
using Vector3 = UnityEngine.Vector3;

namespace Game.Entities
{
    /// <summary>
    /// Monster 行动态：实体此刻处于哪一层受限状态（优先级由低到高）。
    /// 由 Hp 与各 TickTimer <b>派生</b>——不存储、不同步、不可写，全端天然一致（依据字段本就 [Networked]）。
    /// 它把原先散在 AI 决策循环里的 if 守卫顺序，提升为一个具名的优先级阶梯。
    /// <para>
    /// 注意：AI 的主动意图（待机 / 巡逻 / 追击）<b>不在此轴</b>——那是 MonsterAIModule 的私有决策记忆，
    /// 其他端与其他角色都不需要看到，故不进 Model。
    /// </para>
    /// </summary>
    public enum MonsterActState : byte
    {
        /// <summary> 自由：无任何受限事实，AI 可按自己的意图决策 </summary>
        Free = 0,

        /// <summary> 脱战冷却中：原地无敌待机，等冷却到期 </summary>
        Disengaging = 1,

        /// <summary> 施法中：决策与 MoveCommand 所有权均让给技能链步骤 </summary>
        Casting = 2,

        /// <summary> 受控：受击 / 眩晕 / 破防动画期间，暂停决策并保持原地 </summary>
        Controlled = 3,

        /// <summary> 死亡：终态 </summary>
        Dead = 4,
    }

    /// <summary>
    /// Monster 配置模板：Spawn 前由权威端写入，全端可读。
    /// </summary>
    public readonly struct MonsterTemplate : IEquatable<MonsterTemplate>, INetworkStruct
    {
        public readonly int CfgId;
        public readonly int SpawnId;

        public MonsterTemplate(int cfgId, int spawnId)
        {
            CfgId = cfgId;
            SpawnId = spawnId;
        }

        public bool Equals(MonsterTemplate other) => CfgId == other.CfgId && SpawnId == other.SpawnId;

        public override bool Equals(object obj) => obj is MonsterTemplate other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(CfgId, SpawnId);

        public static bool operator ==(MonsterTemplate lhs, MonsterTemplate rhs) => lhs.Equals(rhs);

        public static bool operator !=(MonsterTemplate lhs, MonsterTemplate rhs) => !(lhs == rhs);
    }

    /// <summary>
    /// Monster Model 主文件：状态事实 + 直接 Setter。
    /// 战斗规则见 <see cref="MonsterModel"/>（MonsterModel.Combat.cs），技能规则见 MonsterModel.Skill.cs。
    /// </summary>
    public partial class MonsterModel : NetworkBehaviour, IAfterSpawned, IStateAuthorityChanged
    {
        private const int SKILL_COOLDOWN_CAPACITY = 16;

        #region Networked Datas

        public new EntityId Id => Object != null ? Object.Id : default;

        [Networked, OnChangedRender(nameof(OnTemplateChangedHandler))]
        public MonsterTemplate Template { get; private set; }

        [Networked, OnChangedRender(nameof(OnAnimIdChangedHandler))]
        [Tooltip("当前播放的动画 Id")]
        public int AnimId { get; private set; } = -1;

        [Networked, OnChangedRender(nameof(OnHpChangedHandler))]
        [Tooltip("当前血量")]
        public int Hp { get; private set; }

        [Networked, OnChangedRender(nameof(OnMaxHpChangedHandler))]
        [Tooltip("最大血量")]
        public int MaxHp { get; private set; }

        [Networked, OnChangedRender(nameof(OnAtkChangedHandler))]
        [Tooltip("攻击力")]
        public int Atk { get; private set; }

        [Networked, OnChangedRender(nameof(OnDefChangedHandler))]
        [Tooltip("防御力")]
        public int Def { get; private set; }

        [Networked, OnChangedRender(nameof(OnGuardChangedHandler))]
        [Tooltip("破防值")]
        public int Guard { get; private set; }

        [Networked, OnChangedRender(nameof(OnMaxGuardChangedHandler))]
        [Tooltip("最大破防值")]
        public int MaxGuard { get; private set; }

        [Networked, OnChangedRender(nameof(OnIsInvulnerableChangedHandler))]
        [Tooltip("是否无敌（脱战冷却期间为 true）")]
        public NetworkBool IsInvulnerable { get; private set; }

        [Networked, OnChangedRender(nameof(OnHasEverBeenInCombatChangedHandler))]
        [Tooltip("是否曾进入过战斗（回血资格，易主不丢）")]
        public NetworkBool HasEverBeenInCombat { get; private set; }

        // ===== 技能三件套 =====

        [Networked, OnChangedRender(nameof(OnCastingChainIndexChangedHandler))]
        [Tooltip("正在释放的技能链索引（-1 = 无；两端 prefab 一致，§16.3）")]
        public int CastingChainIndex { get; private set; } = -1;

        [Networked, OnChangedRender(nameof(OnCastCountChangedHandler))]
        [Tooltip("施放计数（每次实际施放 +1 的脉冲，驱动各端技能模块本地自治执行）")]
        public int CastCount { get; private set; }

        [Networked, OnChangedRender(nameof(OnCastingSkillTimerChangedHandler))]
        [Tooltip("施法计时器（TickTimer 全端同时到期）")]
        public TickTimer CastingSkillTimer { get; private set; }

        [Networked, OnChangedRender(nameof(OnCastTargetIdChangedHandler))]
        [Tooltip("本次施放的目标（单目标技能表现用）")]
        public EntityId CastTargetId { get; private set; }

        // ===== 权威归属 =====

        [Networked, OnChangedRender(nameof(OnDesiredAuthorityOwnerChangedHandler))]
        [Tooltip("期望的权威归属玩家实体身份（MonsterAuthorityModule 各端本地反应）")]
        public EntityId DesiredAuthorityOwner { get; private set; }

        // ===== 受控事实计时器（TickTimer 全端同步，易主无缝）=====

        [Networked, OnChangedRender(nameof(OnHitAnimTimerChangedHandler))]
        [Tooltip("受击动画计时器")]
        public TickTimer HitAnimTimer { get; private set; }

        [Networked, OnChangedRender(nameof(OnStunnedAnimTimerChangedHandler))]
        [Tooltip("眩晕动画计时器")]
        public TickTimer StunnedAnimTimer { get; private set; }

        [Networked, OnChangedRender(nameof(OnGuardBrokenAnimTimerChangedHandler))]
        [Tooltip("破防动画计时器")]
        public TickTimer GuardBrokenAnimTimer { get; private set; }

        [Networked, OnChangedRender(nameof(OnGuardBrokenRecoveryTimerChangedHandler))]
        [Tooltip("破防恢复计时器（到期回满破防值）")]
        public TickTimer GuardBrokenRecoveryTimer { get; private set; }

        [Networked, OnChangedRender(nameof(OnDiedAnimTimerChangedHandler))]
        [Tooltip("死亡动画计时器")]
        public TickTimer DiedAnimTimer { get; private set; }

        [Networked, OnChangedRender(nameof(OnCombatExitTimerChangedHandler))]
        [Tooltip("脱战冷却计时器")]
        public TickTimer CombatExitTimer { get; private set; }

        // ===== 技能冷却 =====

        [Networked]
        private TickTimer GlobalAttackCooldownTimer { get; set; }

        [Networked, Capacity(SKILL_COOLDOWN_CAPACITY)]
        public NetworkArray<MonsterSkillCooldownState> SkillCooldownStates => default;

        #endregion

        #region Local Datas

        public CfgMonsterstats Cfg => ConfigMgr.Tables.TbMonsterstats.GetOrDefault(Template.CfgId);

        public CfgMonsterspwan SpawnCfg => ConfigMgr.Tables.TbMonsterspwan.GetOrDefault(Template.SpawnId);

        /// <summary> 巡逻中心（出生点配置位置）</summary>
        public Vector3 PatrolCenter
        {
            get
            {
                var spwanCfg = SpawnCfg;
                if (spwanCfg == null || spwanCfg.SpwanPosition == null || spwanCfg.SpwanPosition.Length < 3)
                {
                    return Vector3.zero;
                }

                return new Vector3(spwanCfg.SpwanPosition[0], spwanCfg.SpwanPosition[1], spwanCfg.SpwanPosition[2]);
            }
        }

        /// <summary> 巡逻半径（出生点随机生成半径）</summary>
        public float PatrolRadius => SpawnCfg?.Radius ?? 0f;

        /// <summary> 脱战半径（tbmonsterstats.reset_range）</summary>
        public float DisengageRadius => Cfg?.ResetRange ?? 0f;

        /// <summary> 攻击冷却（全局攻击间隔）</summary>
        public float AttackCooldown => Cfg?.AttackCooldown ?? 0f;

        /// <summary> 待机动画 Id（表派生，全端可读）</summary>
        public int IdleAnim
        {
            get
            {
                var cfg = Cfg;
                return cfg?.Stand is { Length: > 0 } ? cfg.Stand[0]?.Animation ?? 0 : 0;
            }
        }

        /// <summary> 行走动画 Id（表派生，全端可读）</summary>
        public int WalkAnim
        {
            get
            {
                var cfg = Cfg;
                return cfg?.Walk is { Length: > 0 } ? cfg.Walk[0]?.Animation ?? 0 : 0;
            }
        }

        /// <summary> 奔跑动画 Id（表派生，全端可读）</summary>
        public int RunAnim
        {
            get
            {
                var cfg = Cfg;
                return cfg?.Run is { Length: > 0 } ? cfg.Run[0]?.Animation ?? 0 : 0;
            }
        }

        /// <summary> 破防动画 Id（表派生，全端可读）</summary>
        public int BreakAnim
        {
            get
            {
                var cfg = Cfg;
                return cfg?.Stagger is { Length: > 0 } ? cfg.Stagger[0]?.Animation ?? 0 : 0;
            }
        }

        /// <summary> 破防音效（表派生，全端可读）</summary>
        public string BreakSound
        {
            get
            {
                var cfg = Cfg;
                return cfg?.Stagger is { Length: > 0 } ? cfg.Stagger[0].Sound ?? string.Empty : string.Empty;
            }
        }

        /// <summary> 死亡动画 Id（表派生，全端可读）</summary>
        public int DeadAnim
        {
            get
            {
                var cfg = Cfg;
                return cfg?.Dead is { Length: > 0 } ? cfg.Dead[0]?.Animation ?? 0 : 0;
            }
        }

        /// <summary> 死亡音效（表派生，全端可读）</summary>
        public string DeadSound
        {
            get
            {
                var cfg = Cfg;
                return cfg?.Dead is { Length: > 0 } ? cfg.Dead[0].Sound ?? string.Empty : string.Empty;
            }
        }

        /// <summary> 派生的受控状态：受击 / 眩晕 / 破防动画期间为 true（AI 决策守卫用）</summary>
        public bool IsControlled =>
            HitAnimTimer.IsRunning || StunnedAnimTimer.IsRunning || GuardBrokenAnimTimer.IsRunning;

        /// <summary>
        /// 行动态（派生投影，非第二份状态）：把受限事实的优先级阶梯收敛为一个具名值。
        /// AI 决策守卫、动画兵底层让路、脱战回血资格判定，统一读它。
        /// </summary>
        public MonsterActState ActState =>
            IsDead() ? MonsterActState.Dead
            : IsControlled ? MonsterActState.Controlled
            : IsCastingSkill() ? MonsterActState.Casting
            : IsCombatExitCooldown ? MonsterActState.Disengaging
            : MonsterActState.Free;

        /// <summary> 死亡动画时长（表派生）</summary>
        public float DeadAnimLength => GetAnimLength(DeadAnim);

        #endregion

        #region StateAuthority Datas

        /// <summary> 感知结果快照（权威端派生数据）</summary>
        public MonsterPerceptionSnapshot PerceptionSnapshot { get; private set; }

        /// <summary> 当前移动指令（权威端派生数据；AI 的移动意图，每 tick 重申）</summary>
        public MonsterMoveCommand MoveCommand { get; private set; }

        /// <summary>
        /// 实际水平移动速度（权威端派生数据；MoveModule 每 tick 从 Follower 回流的<b>物理事实</b>）。
        /// locomotion 动画据此判定"在不在动"——不靠 MoveCommand 猜，
        /// 故到停距站定 / 被挤住 / 爬坡 / 绕路全部自动正确。
        /// </summary>
        public float MoveSpeedFact { get; private set; }

        /// <summary> 无目标累计时长（权威端本地累计；易主重置为 0，保守推迟回血）</summary>
        private float _outOfCombatElapsed;

        /// <summary> 已装配技能行为模块的技能 Id 集（Spawned 由 Ctrl 校验后写入；同 prefab 全端一致，易主不丢）</summary>
        /// <summary> 技能链列表（Ctrl Spawned 装配注入的派生快照；同 prefab 全端一致） </summary>
        private List<MonsterSkillChain> _skillChains = new();

        /// <summary> 已装配运行器的链索引集（AI 选技能守卫用） </summary>
        private HashSet<int> _availableChainIndices = new();

        /// <summary> 脱战回血计时器（权威端本地）</summary>
        private float _idleRegenTimer;

        /// <summary> 上次受击反应时刻（权威端本地时间，节流用）</summary>
        private float _lastHitReactionTime = float.NegativeInfinity;

        /// <summary> 最近一次受击反应选中的音效（权威端本地，BattleView 播放用）</summary>
        public string LastHitSound { get; private set; }

        #endregion

        #region Changed Events

        public event Action<MonsterTemplate> OnTemplateChanged;

        public event Action<MonsterPerceptionSnapshot> OnPerceptionSnapshotChanged;
        public event Action<MonsterMoveCommand> OnMoveCommandChanged;

        public event Action<int> OnAnimIdChanged;

        public event Action<int> OnHpChanged;
        public event Action<int> OnMaxHpChanged;
        public event Action<int> OnAtkChanged;
        public event Action<int> OnDefChanged;
        public event Action<int> OnGuardChanged;
        public event Action<int> OnMaxGuardChanged;

        public event Action<bool> OnIsInvulnerableChanged;
        public event Action<bool> OnHasEverBeenInCombatChanged;

        public event Action<int> OnCastingChainIndexChanged;
        public event Action<int> OnCastCountChanged;
        public event Action<EntityId> OnCastTargetIdChanged;

        public event Action<EntityId> OnDesiredAuthorityOwnerChanged;

        public event Action<bool> OnCastingSkillTimerChanged;
        public event Action<bool> OnHitAnimTimerChanged;
        public event Action<bool> OnStunnedAnimTimerChanged;
        public event Action<bool> OnGuardBrokenAnimTimerChanged;
        public event Action<bool> OnGuardBrokenRecoveryTimerChanged;
        public event Action<bool> OnDiedAnimTimerChanged;
        public event Action<bool> OnCombatExitTimerChanged;

        #endregion

        #region Pushed Events

        public event Action<Vector3> OnTeleportPositionPushed;
        public event Action<Quaternion> OnTeleportRotationPushed;

        /// <summary> 受击表现推送（一次性瞬变，权威端本地播受击 IK 用）</summary>
        public event Action<HitData> OnHitReceivedPushed;

        #endregion

        #region Lifecycle

        /// <summary>
        /// 预先初始化（Spawn 之前，仅权威端）
        /// </summary>
        public void PreInit(MonsterTemplate template)
        {
            // 1. 必须先写 Template（网络同步锚点）
            Template = template;

            var cfg = Cfg;
            if (cfg == null)
            {
                Logging.Error($"[MonsterModel] PreInit: tbmonsterstats 找不到配置 ({Template.CfgId})");
                return;
            }

            // 2. 由配置初始化战斗属性
            MaxHp = cfg.HealthBar;
            Hp = MaxHp;
            Atk = cfg.AttackPower;
            Def = 0;
            MaxGuard = cfg.StaggerBar;
            Guard = MaxGuard;

            IsInvulnerable = false;
            HasEverBeenInCombat = false;

            AnimId = -1;
            CastingChainIndex = -1;
            CastCount = 0;
            CastTargetId = default;
            DesiredAuthorityOwner = default;

            // 技能冷却槽：链索引 0 是合法链，空槽哨兵统一 -1
            for (int i = 0; i < SKILL_COOLDOWN_CAPACITY; i++)
            {
                SkillCooldownStates.Set(i, new MonsterSkillCooldownState { ChainIndex = -1, CooldownTimer = default });
            }
        }

        public void AfterSpawned()
        {
            // Spawned 之后必须进行一次重投影
            Resync();
        }

        public void StateAuthorityChanged()
        {
            // 权威易主之后必须进行一次重投影；
            // 权威端本地累计值保守重置（回血推迟，无漏洞）
            _outOfCombatElapsed = 0f;
            _idleRegenTimer = 0f;
            _lastHitReactionTime = float.NegativeInfinity;
            PerceptionSnapshot = default;
            MoveCommand = default;
            MoveSpeedFact = 0f;

            Resync();
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;

            // 领域计时器结算（Combat.cs / Skill.cs）
            UpdateCombatTimers();
            UpdateCastingTimer();
            UpdateOutOfCombat();

            // 动画裁决（Anim.cs）：必须紧接计时器结算之后——
            // 受控 / 施法计时器在上面清零的那一刻，本行立即接管兵底层动画。
            // 这正是原先散在各处的「到期 → SetAnimId(IdleAnim)」补丁的替代物。
            TickAnim();
        }

        #endregion

        #region Setter API

        /// <summary>
        /// 设置：动画 Id
        /// </summary>
        public void SetAnimId(int animId)
        {
            if (!HasStateAuthority) return;
            if (AnimId == animId) return;

            AnimId = animId;
            OnAnimIdChanged?.Invoke(AnimId);
        }

        /// <summary>
        /// 设置：血量（领域规则：钳制 [0, MaxHp]，死亡触发）
        /// </summary>
        public void SetHp(int hp)
        {
            if (!HasStateAuthority) return;
            if (Hp == hp) return;

            Hp = Mathf.Clamp(hp, 0, MaxHp);
            OnHpChanged?.Invoke(Hp);

            if (IsDead()) EnterDead();
        }

        /// <summary>
        /// 设置：破防值（领域规则：钳制 [0, MaxGuard]，归零触发破防）
        /// </summary>
        public void SetGuard(int guard)
        {
            if (!HasStateAuthority) return;
            if (Guard == guard) return;

            Guard = Mathf.Clamp(guard, 0, MaxGuard);
            OnGuardChanged?.Invoke(Guard);

            if (IsGuardBroken() && !IsDead() && !IsStunned())
            {
                EnterGuardBroken();
            }
        }

        /// <summary>
        /// 设置：无敌
        /// </summary>
        public void SetIsInvulnerable(bool invulnerable)
        {
            if (!HasStateAuthority) return;
            if (IsInvulnerable == invulnerable) return;

            IsInvulnerable = invulnerable;
            OnIsInvulnerableChanged?.Invoke(IsInvulnerable);
        }

        /// <summary>
        /// 设置：是否曾进入过战斗（回血资格，易主不丢）
        /// </summary>
        public void SetHasEverBeenInCombat(bool hasEverBeenInCombat)
        {
            if (!HasStateAuthority) return;
            if (HasEverBeenInCombat == hasEverBeenInCombat) return;

            HasEverBeenInCombat = hasEverBeenInCombat;
            OnHasEverBeenInCombatChanged?.Invoke(HasEverBeenInCombat);
        }

        /// <summary>
        /// 设置：期望的权威归属玩家（[Networked] 事实，各端 MonsterAuthorityModule 本地反应）
        /// </summary>
        public void SetDesiredAuthorityOwner(EntityId ownerId)
        {
            if (!HasStateAuthority) return;
            if (DesiredAuthorityOwner == ownerId) return;

            DesiredAuthorityOwner = ownerId;
            OnDesiredAuthorityOwnerChanged?.Invoke(DesiredAuthorityOwner);
        }

        /// <summary>
        /// 设置：感知快照（权威端派生数据，节流写入）
        /// </summary>
        public void SetPerceptionSnapshot(MonsterPerceptionSnapshot snapshot)
        {
            if (!HasStateAuthority) return;
            if (PerceptionSnapshot.Equals(snapshot)) return;

            PerceptionSnapshot = snapshot;
            OnPerceptionSnapshotChanged?.Invoke(PerceptionSnapshot);
        }

        /// <summary>
        /// 设置：移动指令（权威端派生数据）。
        /// AI 每 tick 无条件重申当前意图，靠此处的 no-op 早退去重——
        /// 内容未变则不抛事件、不触发寻路重规划，重申开销为零。
        /// </summary>
        public void SetMoveCommand(MonsterMoveCommand command)
        {
            if (!HasStateAuthority) return;
            if (MoveCommand.Equals(command)) return;

            MoveCommand = command;
            OnMoveCommandChanged?.Invoke(MoveCommand);
        }

        /// <summary>
        /// 设置：实际水平移动速度事实（逐帧值，免 no-op 早退；无事件，仅作动画裁决输入）
        /// </summary>
        public void SetMoveSpeedFact(float speed)
        {
            if (!HasStateAuthority) return;

            MoveSpeedFact = speed;
        }

        /// <summary>
        /// 设置：最近一次受击反应选中的音效（权威端本地，BattleView 播放用）
        /// </summary>
        public void SetLastHitSound(string sound)
        {
            if (!HasStateAuthority) return;
            if (LastHitSound == sound) return;

            LastHitSound = sound;
        }

        /// <summary>
        /// 设置：技能链列表 + 已装配链索引集（Spawned 由 Ctrl 校验装配后一次性注入；
        /// 同 prefab 全端一致，权威易主无需重写；§16.2 Ctrl 链列表的派生快照）
        /// </summary>
        public void SetSkillChains(List<MonsterSkillChain> chains, HashSet<int> availableChainIndices)
        {
            if (!HasStateAuthority) return;

            _skillChains = chains ?? new List<MonsterSkillChain>();
            _availableChainIndices = availableChainIndices ?? new HashSet<int>();
        }

        /// <summary>
        /// 设置：正在释放的技能链索引
        /// </summary>
        public void SetCastingChainIndex(int chainIndex)
        {
            if (!HasStateAuthority) return;
            if (CastingChainIndex == chainIndex) return;

            CastingChainIndex = chainIndex;
            OnCastingChainIndexChanged?.Invoke(CastingChainIndex);
        }

        /// <summary>
        /// 设置：施放计数（每次实际施放 +1 的脉冲）
        /// </summary>
        public void SetCastCount(int castCount)
        {
            if (!HasStateAuthority) return;
            if (CastCount == castCount) return;

            CastCount = castCount;
            OnCastCountChanged?.Invoke(CastCount);
        }

        /// <summary>
        /// 设置：本次施放的目标
        /// </summary>
        public void SetCastTargetId(EntityId targetId)
        {
            if (!HasStateAuthority) return;
            if (CastTargetId == targetId) return;

            CastTargetId = targetId;
            OnCastTargetIdChanged?.Invoke(CastTargetId);
        }

        // ===== 计时器（瞬变值，免 no-op 早退；运行态变化时才抛事件）=====

        /// <summary>
        /// 设置：受击动画计时器
        /// </summary>
        public void SetHitAnimTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            bool wasRunning = HitAnimTimer.IsRunning;
            HitAnimTimer = timer;
            if (wasRunning != timer.IsRunning) OnHitAnimTimerChanged?.Invoke(timer.IsRunning);
        }

        /// <summary>
        /// 设置：眩晕动画计时器
        /// </summary>
        public void SetStunnedAnimTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            bool wasRunning = StunnedAnimTimer.IsRunning;
            StunnedAnimTimer = timer;
            if (wasRunning != timer.IsRunning) OnStunnedAnimTimerChanged?.Invoke(timer.IsRunning);
        }

        /// <summary>
        /// 设置：破防动画计时器
        /// </summary>
        public void SetGuardBrokenAnimTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            bool wasRunning = GuardBrokenAnimTimer.IsRunning;
            GuardBrokenAnimTimer = timer;
            if (wasRunning != timer.IsRunning) OnGuardBrokenAnimTimerChanged?.Invoke(timer.IsRunning);
        }

        /// <summary>
        /// 设置：破防恢复计时器
        /// </summary>
        public void SetGuardBrokenRecoveryTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            bool wasRunning = GuardBrokenRecoveryTimer.IsRunning;
            GuardBrokenRecoveryTimer = timer;
            if (wasRunning != timer.IsRunning) OnGuardBrokenRecoveryTimerChanged?.Invoke(timer.IsRunning);
        }

        /// <summary>
        /// 设置：死亡动画计时器
        /// </summary>
        public void SetDiedAnimTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            bool wasRunning = DiedAnimTimer.IsRunning;
            DiedAnimTimer = timer;
            if (wasRunning != timer.IsRunning) OnDiedAnimTimerChanged?.Invoke(timer.IsRunning);
        }

        /// <summary>
        /// 设置：脱战冷却计时器
        /// </summary>
        public void SetCombatExitTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            bool wasRunning = CombatExitTimer.IsRunning;
            CombatExitTimer = timer;
            if (wasRunning != timer.IsRunning) OnCombatExitTimerChanged?.Invoke(timer.IsRunning);
        }

        /// <summary>
        /// 设置：施法计时器
        /// </summary>
        public void SetCastingSkillTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            bool wasRunning = CastingSkillTimer.IsRunning;
            CastingSkillTimer = timer;
            if (wasRunning != timer.IsRunning) OnCastingSkillTimerChanged?.Invoke(timer.IsRunning);
        }

        /// <summary>
        /// 设置：全局攻击冷却计时器（无事件，内部冷却判据）
        /// </summary>
        public void SetGlobalAttackCooldownTimer(TickTimer timer)
        {
            if (!HasStateAuthority) return;

            GlobalAttackCooldownTimer = timer;
        }

        #endregion

        #region Push API

        /// <summary>
        /// 推送：传送位置
        /// </summary>
        public void PushTeleportPosition(Vector3 position)
        {
            if (!HasStateAuthority) return;
            OnTeleportPositionPushed?.Invoke(position);
        }

        /// <summary>
        /// 推送：传送旋转
        /// </summary>
        public void PushTeleportRotation(Quaternion rotation)
        {
            if (!HasStateAuthority) return;
            OnTeleportRotationPushed?.Invoke(rotation);
        }

        /// <summary>
        /// 推送：受击表现（一次性瞬变，不落状态）
        /// </summary>
        public void PushHitReceived(HitData hitData)
        {
            if (!HasStateAuthority) return;
            OnHitReceivedPushed?.Invoke(hitData);
        }

        #endregion

        #region OnChangedRender Handlers

        private void OnTemplateChangedHandler()
        {
            if (HasStateAuthority) return;
            OnTemplateChanged?.Invoke(Template);
        }

        private void OnAnimIdChangedHandler()
        {
            if (HasStateAuthority) return;
            OnAnimIdChanged?.Invoke(AnimId);
        }

        private void OnHpChangedHandler()
        {
            if (HasStateAuthority) return;
            OnHpChanged?.Invoke(Hp);
        }

        private void OnMaxHpChangedHandler()
        {
            if (HasStateAuthority) return;
            OnMaxHpChanged?.Invoke(MaxHp);
        }

        private void OnAtkChangedHandler()
        {
            if (HasStateAuthority) return;
            OnAtkChanged?.Invoke(Atk);
        }

        private void OnDefChangedHandler()
        {
            if (HasStateAuthority) return;
            OnDefChanged?.Invoke(Def);
        }

        private void OnGuardChangedHandler()
        {
            if (HasStateAuthority) return;
            OnGuardChanged?.Invoke(Guard);
        }

        private void OnMaxGuardChangedHandler()
        {
            if (HasStateAuthority) return;
            OnMaxGuardChanged?.Invoke(MaxGuard);
        }

        private void OnIsInvulnerableChangedHandler()
        {
            if (HasStateAuthority) return;
            OnIsInvulnerableChanged?.Invoke(IsInvulnerable);
        }

        private void OnHasEverBeenInCombatChangedHandler()
        {
            if (HasStateAuthority) return;
            OnHasEverBeenInCombatChanged?.Invoke(HasEverBeenInCombat);
        }

        private void OnCastingChainIndexChangedHandler()
        {
            if (HasStateAuthority) return;
            OnCastingChainIndexChanged?.Invoke(CastingChainIndex);
        }

        private void OnCastCountChangedHandler()
        {
            if (HasStateAuthority) return;
            OnCastCountChanged?.Invoke(CastCount);
        }

        private void OnCastingSkillTimerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnCastingSkillTimerChanged?.Invoke(CastingSkillTimer.IsRunning);
        }

        private void OnCastTargetIdChangedHandler()
        {
            if (HasStateAuthority) return;
            OnCastTargetIdChanged?.Invoke(CastTargetId);
        }

        private void OnDesiredAuthorityOwnerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnDesiredAuthorityOwnerChanged?.Invoke(DesiredAuthorityOwner);
        }

        private void OnHitAnimTimerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnHitAnimTimerChanged?.Invoke(HitAnimTimer.IsRunning);
        }

        private void OnStunnedAnimTimerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnStunnedAnimTimerChanged?.Invoke(StunnedAnimTimer.IsRunning);
        }

        private void OnGuardBrokenAnimTimerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnGuardBrokenAnimTimerChanged?.Invoke(GuardBrokenAnimTimer.IsRunning);
        }

        private void OnGuardBrokenRecoveryTimerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnGuardBrokenRecoveryTimerChanged?.Invoke(GuardBrokenRecoveryTimer.IsRunning);
        }

        private void OnDiedAnimTimerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnDiedAnimTimerChanged?.Invoke(DiedAnimTimer.IsRunning);
        }

        private void OnCombatExitTimerChangedHandler()
        {
            if (HasStateAuthority) return;
            OnCombatExitTimerChanged?.Invoke(CombatExitTimer.IsRunning);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 重投影数据变化事件
        /// </summary>
        private void Resync()
        {
            OnTemplateChanged?.Invoke(Template);

            OnAnimIdChanged?.Invoke(AnimId);

            OnHpChanged?.Invoke(Hp);
            OnMaxHpChanged?.Invoke(MaxHp);
            OnAtkChanged?.Invoke(Atk);
            OnDefChanged?.Invoke(Def);
            OnGuardChanged?.Invoke(Guard);
            OnMaxGuardChanged?.Invoke(MaxGuard);

            OnIsInvulnerableChanged?.Invoke(IsInvulnerable);
            OnHasEverBeenInCombatChanged?.Invoke(HasEverBeenInCombat);

            OnCastingChainIndexChanged?.Invoke(CastingChainIndex);
            OnCastCountChanged?.Invoke(CastCount);
            OnCastingSkillTimerChanged?.Invoke(CastingSkillTimer.IsRunning);
            OnCastTargetIdChanged?.Invoke(CastTargetId);

            OnDesiredAuthorityOwnerChanged?.Invoke(DesiredAuthorityOwner);

            OnHitAnimTimerChanged?.Invoke(HitAnimTimer.IsRunning);
            OnStunnedAnimTimerChanged?.Invoke(StunnedAnimTimer.IsRunning);
            OnGuardBrokenAnimTimerChanged?.Invoke(GuardBrokenAnimTimer.IsRunning);
            OnGuardBrokenRecoveryTimerChanged?.Invoke(GuardBrokenRecoveryTimer.IsRunning);
            OnDiedAnimTimerChanged?.Invoke(DiedAnimTimer.IsRunning);
            OnCombatExitTimerChanged?.Invoke(CombatExitTimer.IsRunning);
        }

        #endregion

        #region Helpers

        public bool IsAlive() => Hp > 0;
        public bool IsDead() => Hp <= 0;
        public bool IsHpFull() => Hp >= MaxHp;
        public bool IsGuardBroken() => Guard <= 0;
        public bool IsStunned() => StunnedAnimTimer.IsRunning;
        public bool IsCastingSkill() => CastingSkillTimer.IsRunning;

        /// <summary>
        /// 技能链是否已装配运行器（AI 选技能守卫：装配失败的链不选，防"幽灵施放"）
        /// </summary>
        public bool IsChainConfigured(int chainIndex) => _availableChainIndices.Contains(chainIndex);

        /// <summary>
        /// 技能链数量（AI 技能池遍历用）
        /// </summary>
        public int ChainCount => _skillChains.Count;

        /// <summary>
        /// 查询：链配置（只读；链数据唯一权威在 Ctrl 序列化列表，此处为派生快照）
        /// </summary>
        public MonsterSkillChain GetChain(int chainIndex)
        {
            if (chainIndex < 0 || chainIndex >= _skillChains.Count) return null;
            return _skillChains[chainIndex];
        }

        /// <summary>
        /// 查询动画时长（跨领域共享：战斗受击 / 破防 / 死亡与技能施法均使用）
        /// </summary>
        public float GetAnimLength(int animId)
        {
            if (animId <= 0) return 0f;

            var cfgAnim = ConfigMgr.Tables.TbAnimation.GetOrDefault(animId);
            if (cfgAnim is null)
            {
                Logging.Warning($"[MonsterModel] GetAnimLength: tbanimation 找不到配置 ({animId})");
                return 0f;
            }

            return cfgAnim.AnimationLength;
        }

        #endregion
    }
}
