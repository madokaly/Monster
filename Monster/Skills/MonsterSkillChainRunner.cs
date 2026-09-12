using Framework;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能链运行器（ModuleBase；每链一个，Ctrl Spawned 装配）。
    /// 监听 Model 施放脉冲按链身份过滤；双端统一以本地时钟在 Update 每帧推进时间线
    /// （不在仿真回调里；NetworkTransform 仍按仿真节奏采样快照，不增网络量）：
    /// 步骤 Enter / Tick / Exit；链级进入动画由 Model.TryCastChain 内部级联写入（硬约束），
    /// 步骤进入动画（StepAnimIds 随机）由权威端在时间线推进内经 Model 写 AnimId
    /// （[Networked] 状态事实，全端播放）；链 / 步骤特效与音效各端本地随机播。
    /// 权威端身份只用于状态事实写入（AnimId / MoveCommand / PushBodyRotation）；
    /// 行为逻辑（移动 / 选点 / 状态写入）在步骤内部按 HasStateAuthority 分流，
    /// 伤害判定与结算恒为受击方本地结算；链身份 = 链索引（Model.CastingChainIndex）。
    /// </summary>
    public class MonsterSkillChainRunner : ModuleBase
    {
        private readonly MonsterModel _model;
        private readonly MonsterSkillChain _chain;
        private readonly int _chainIndex;
        private readonly MonsterSkillStep[] _steps;

        /// <summary> 是否处于"本次施放进行中"（本端本地） </summary>
        private bool _casting;

        /// <summary> 本次施放的本端起点（本地时钟 Time.time） </summary>
        private float _castStartTime;

        /// <summary> 当前活跃步骤索引（本端时钟推进；-1 = 无） </summary>
        private int _activeStepIndex = -1;

        public MonsterSkillChainRunner(MonsterModel model, int chainIndex, MonsterSkillChain chain)
        {
            _model = model;
            _chainIndex = chainIndex;
            _chain = chain;
            _steps = new MonsterSkillStep[chain != null ? chain.Steps.Count : 0];

            if (chain != null)
            {
                for (int i = 0; i < chain.Steps.Count; i++)
                {
                    _steps[i] = MonsterSkillStepFactory.Create(model, chain.Steps[i]);
                }
            }

            RegisterModelListeners();
        }

        #region Lifecycle

        protected override void OnDispose()
        {
            ClearModelListeners();

            for (int i = 0; i < _steps.Length; i++)
            {
                _steps[i]?.Dispose();
            }
        }

        protected override void OnStateAuthorityChanged()
        {
            // 权威易主：保守重置中间态（等下次施放脉冲再启动）
            ResetCasting();

            for (int i = 0; i < _steps.Length; i++)
            {
                _steps[i]?.AuthorityChanged(HasStateAuthority);
            }
        }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;

            _model.OnCastCountChanged += OnCastCountChangedHandler;
            _model.OnCastingChainIndexChanged += OnCastingChainIndexChangedHandler;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;

            _model.OnCastCountChanged -= OnCastCountChangedHandler;
            _model.OnCastingChainIndexChanged -= OnCastingChainIndexChangedHandler;
        }

        #endregion

        #region Model Handlers

        private void OnCastCountChangedHandler(int castCount)
        {
            if (_chain is null) return;

            // 施放脉冲：轮到自己（链身份）才执行
            if (_model.CastingChainIndex != _chainIndex) return;

            _casting = true;
            _castStartTime = Time.time;
            _activeStepIndex = -1;

            // 链级进入表现：动画由 Model 在 TryCastChain 内完成（数据驱动级联内聚于 Model，硬约束），
            // 本 handler 内不反写 Model；特效 / 音效各端本地随机播
            MonsterSkillPresentation.PlayRandomEffect(_chain.EnterEffects);
            MonsterSkillPresentation.PlayRandomSound(_chain.EnterSounds, _chain.SoundAttachPoint);
        }

        private void OnCastingChainIndexChangedHandler(int castingChainIndex)
        {
            if (!_casting) return;

            // 施法结束（自然到期或打断都会归 -1）
            if (castingChainIndex != -1) return;

            ResetCasting();
        }

        #endregion

        #region Ticks

        protected override void OnUpdate(float deltaTime)
        {
            // 逻辑通道：双端统一每帧推进（Update 时刻，时序先于动画阶段）
            AdvanceTimeline(CastElapsed);
        }

        protected override void OnLateUpdate(float deltaTime)
        {
            // 表现通道：每帧视觉更新（Ctrl LateUpdate 转发，动画 / IK 后采样）：仅活跃步骤
            if (!_casting || _activeStepIndex < 0) return;

            var active = _steps[_activeStepIndex];
            if (active != null && active.IsActive)
            {
                active.VisualUpdate(CastElapsed);
            }
        }

        #endregion

        #region Timeline

        /// <summary>
        /// 本端时钟推进时间线：退出窗口已过的活跃步骤、驱动活跃步骤逐 tick、进入起点已到的后续步骤（同 tick 可跨多步）。
        /// 游标指向最近一个执行过的步骤，点式步骤执行后游标保留（进入循环从 +1 起），不会重复进入。
        /// </summary>
        private void AdvanceTimeline(float elapsed)
        {
            if (!_casting) return;
            if (_steps is not { Length: > 0 }) return;
            if (elapsed <= 0f) return;

            // 退出：活跃步骤窗口已结束（内容 + 收尾延迟到期，或已被后续步骤打断）
            if (_activeStepIndex >= 0)
            {
                var active = _steps[_activeStepIndex];
                if (active != null && active.IsActive)
                {
                    var activeConfig = _chain.Steps[_activeStepIndex];
                    if (elapsed >= activeConfig.StartOffset + activeConfig.TotalDuration)
                    {
                        active.Exit();
                    }
                }
            }

            // Tick：活跃步骤窗口内逐 tick 驱动（Enter → Tick → Exit 契约）
            if (_activeStepIndex >= 0)
            {
                var active = _steps[_activeStepIndex];
                if (active != null && active.IsActive)
                {
                    active.Tick(elapsed);
                }
            }

            // 进入：起点已到的后续步骤
            while (_activeStepIndex + 1 < _steps.Length)
            {
                int nextIndex = _activeStepIndex + 1;
                var nextConfig = _chain.Steps[nextIndex];
                var nextStep = _steps[nextIndex];

                // 未实现的步骤（工厂返回 null）：跳过并继续（装配期已告警）
                if (nextStep == null || nextConfig == null)
                {
                    _activeStepIndex = nextIndex;
                    continue;
                }

                if (elapsed < nextConfig.StartOffset) break;

                // 窗口重叠守卫：前一步骤窗口尚未结束即进入下一步骤 → 先收尾前一步骤（时间线顺序优先）
                if (_activeStepIndex >= 0)
                {
                    var previous = _steps[_activeStepIndex];
                    if (previous != null && previous.IsActive)
                    {
                        previous.Exit();
                    }
                }

                EnterStep(nextIndex, elapsed);
                _activeStepIndex = nextIndex;

                // 零窗口步骤（点式且无收尾延迟）：进入即一次性结算，立即退出
                if (nextConfig.TotalDuration <= 0f)
                {
                    nextStep.Exit();
                }
            }
        }

        /// <summary>
        /// 步骤起始：权威端写步骤动画（AnimId > 0 时；[Networked] 状态事实全端播放），全端 Enter。
        /// </summary>
        private void EnterStep(int index, float elapsed)
        {
            var config = _chain.Steps[index];
            var step = _steps[index];

            // 步骤进入动画：权威端随机写（替换当前动画；数组空 = 不写）
            if (HasStateAuthority)
            {
                WriteStepAnimId(config.StepAnimIds);
            }

            step.Enter(elapsed);
        }

        /// <summary>
        /// 施放相对时间（双端统一的本地时钟；未施放恒为 0）。
        /// </summary>
        private float CastElapsed
        {
            get
            {
                if (!_casting) return 0f;
                return Time.time - _castStartTime;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 权威端随机取一个步骤动画 Id 经 Model 写入（[Networked] 状态事实全端播；空数组 / 非正 Id 跳过）。
        /// 仅用于步骤进入（时间线推进内的命令式写入，非 Model 事件 handler 反写，硬约束允许）。
        /// 链级进入动画已收归 Model.TryCastChain 内部级联。
        /// </summary>
        private void WriteStepAnimId(int[] animIds)
        {
            if (animIds is not { Length: > 0 }) return;

            int animId = animIds[UnityEngine.Random.Range(0, animIds.Length)];
            if (animId > 0)
            {
                _model.SetAnimId(animId);
            }
        }

        private void ResetCasting()
        {
            _casting = false;
            _castStartTime = 0f;

            if (_activeStepIndex >= 0 && _steps is { Length: > 0 })
            {
                var active = _steps[_activeStepIndex];
                if (active != null && active.IsActive)
                {
                    active.Exit();
                }
            }

            // 施法级收口要遍历全部步骤：Projectile 等本地生成物可跨重叠步骤窗口继续存在，
            // 不能只清理当前活跃步骤；已自然结束的生成物由各步骤按自身状态幂等跳过。
            for (int i = 0; i < _steps.Length; i++)
            {
                _steps[i]?.AbortCast();
            }

            _activeStepIndex = -1;
        }

        #endregion
    }
}
