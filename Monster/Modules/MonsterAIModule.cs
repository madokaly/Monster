using System;
using Framework;
using Framework.Core;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Game.Entities
{
    /// <summary>
    /// AI 主动意图（本模块私有的决策记忆）。
    /// 它不进 Model、不同步——其他端与其他角色都不需要知道怪物"想干什么"，
    /// 需要被看见的是行动态（<see cref="MonsterActState"/>）与动画（AnimId）这些事实。
    /// </summary>
    public enum MonsterIntent : byte
    {
        Idle = 0,
        Patrol = 1,
        Chase = 2,
    }

    [Serializable]
    public class MonsterAIModuleConfig
    {
        [Header("References")]
        [Tooltip("怪物自身 Transform（权威端本地位置来源）")]
        public Transform SelfTransform;

        [Header("Patrol")]
        [Tooltip("巡逻移动速度（m/s）")]
        public float PatrolMoveSpeed = 3f;
        [Tooltip("巡逻点到达后原地等待时长（秒）")]
        public float PatrolWaitDuration = 2f;
        [Tooltip("巡逻点采样重试次数")]
        public int PatrolPointAttempts = 10;
        [Tooltip("巡逻停距")]
        public float PatrolStopDistance = 2f;

        [Header("Chase")]
        [Tooltip("追击移动速度（m/s）")]
        public float ChaseMoveSpeed = 4f;
        [Tooltip("追击移动指令刷新间隔（秒），节流寻路重规划")]
        public float ChaseCommandInterval = 0.5f;
        [Tooltip("追击停距")]
        public float ChaseStopDistance = 5f;
    }

    /// <summary>
    /// 怪物 AI 决策模块（权威端）。
    /// <para>
    /// 每个 FUN tick 读 Model 的行动态与感知快照，决策巡逻 / 追击 / 施法，产出两样东西：
    /// 移动意图（<see cref="MonsterMoveCommand"/>）与技能施放（Model.TryCastChain）。
    /// </para>
    /// <para>
    /// <b>本模块不写动画。</b>动画是状态的投影，由 Model 统一裁决（MonsterModel.Anim.cs）——
    /// AI 只需在移动意图里声明步态（走 / 跑），"在不在动"由实际速度事实决定。
    /// </para>
    /// <para>
    /// <b>移动意图是持续声明，不是一次性事件。</b>各决策分支只修改 <c>_moveIntent</c>，
    /// 由 <see cref="TickDecision"/> 末尾统一下达并每 tick 重申；
    /// 意图内容未变时 Model 的 no-op 早退生效，不触发事件、不重规划寻路。
    /// </para>
    /// </summary>
    public class MonsterAIModule : ModuleBase
    {
        private readonly MonsterModel _model;
        private readonly MonsterAIModuleConfig _config;

        // ===== 决策意图（权威端本地，易主后重算）=====

        /// <summary> AI 主动意图 </summary>
        private MonsterIntent _intent;

        /// <summary> 当前移动意图（每 tick 重申给 Model） </summary>
        private MonsterMoveCommand _moveIntent;

        // ===== 巡逻私有状态 =====

        /// <summary> 是否已有巡逻点 </summary>
        private bool _hasPatrolPoint;

        /// <summary> 当前巡逻点 </summary>
        private Vector3 _currentPatrolPoint;

        /// <summary> 巡逻原地等待计时 </summary>
        private float _patrolWaitTimer;

        // ===== 追击目标点（节流重算，逐 tick 重申）=====

        private bool _hasChaseDestination;
        private Vector3 _chaseDestination;
        private float _chaseCommandTimer;

        /// <summary> 上一 tick 是否处于施法态（用于识别施法结束，立即重算移动意图） </summary>
        private bool _wasCasting;

        #region Lifecycle

        public MonsterAIModule(MonsterModel model, MonsterAIModuleConfig config)
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
            // 权威易主：清空本地决策中间态，从 Model 状态事实重新决策
            _intent = MonsterIntent.Idle;
            _patrolWaitTimer = 0f;
            _wasCasting = false;
            ResetMoveDecisionCache();
            SetStopIntent();
        }

        protected override void OnFixedUpdateNetwork(float deltaTime)
        {
            if (_model is null) return;
            if (_config is null) return;
            if (!HasStateAuthority) return;

            TickDecision(deltaTime);
        }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;
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

        #region Decision Loop

        /// <summary>
        /// 决策主循环（每 FUN tick）。
        /// 受限状态的优先级阶梯已收敛到 <see cref="MonsterModel.ActState"/>，此处只需按态分派。
        /// </summary>
        private void TickDecision(float deltaTime)
        {
            // 施法中：决策与移动意图的所有权都让给技能链步骤
            // （步骤自行写 IsStopped 停 Follower 后手动位移，见 MonsterChaseStep / MonsterBurrowStep）
            if (_model.ActState == MonsterActState.Casting)
            {
                _wasCasting = true;
                return;
            }

            // 施法刚结束：丢弃施法前的陈旧目标点，本 tick 立即重算
            if (_wasCasting)
            {
                _wasCasting = false;
                ResetMoveDecisionCache();
            }

            var snapshot = _model.PerceptionSnapshot;

            // 脱战冷却中出现可追目标 → 立即取消冷却
            // （ActState 是派生值，清掉冷却计时器后下面的 switch 已重新求值为 Free）
            if (_model.ActState == MonsterActState.Disengaging && HasChasableTarget(snapshot))
            {
                _model.ClearCombatExitCooldown();
            }

            switch (_model.ActState)
            {
                case MonsterActState.Dead:
                case MonsterActState.Controlled:
                    // 死亡终态 / 受控期间：保持原地
                    SetStopIntent();
                    break;

                case MonsterActState.Disengaging:
                    // 脱战冷却中：原地待机，等冷却到期
                    _intent = MonsterIntent.Idle;
                    SetStopIntent();
                    break;

                case MonsterActState.Free:
                    TickFree(snapshot, deltaTime);
                    break;
            }

            // 唯一下达点：每 tick 无条件重申当前意图（Model 的 no-op 早退负责去重）
            _model.SetMoveCommand(_moveIntent);
        }

        /// <summary>
        /// 自由态决策：按 AI 意图分派。
        /// </summary>
        private void TickFree(MonsterPerceptionSnapshot snapshot, float deltaTime)
        {
            if (_intent == MonsterIntent.Chase)
            {
                TickChase(snapshot, deltaTime);
                return;
            }

            // 待机 / 巡逻：搜索圈或追击圈内有目标 → 转入追击
            if (HasChasableTarget(snapshot))
            {
                _intent = MonsterIntent.Chase;
                _hasPatrolPoint = false;
                _patrolWaitTimer = 0f;
                _hasChaseDestination = false; // 立即刷新追击目标点
                _chaseCommandTimer = 0f;

                TickChase(snapshot, deltaTime);
                return;
            }

            TickPatrol(deltaTime);
        }

        /// <summary>
        /// 追击行为：目标丢失 / 硬脱战 → 脱战；技能就绪且在射程 → 施法；否则追近。
        /// </summary>
        private void TickChase(MonsterPerceptionSnapshot snapshot, float deltaTime)
        {
            // 目标丢失，或仅脱战圈内有目标（追不上了）→ 进入脱战流程
            if (!snapshot.HasAnyTarget || snapshot.RangeType == 3)
            {
                EnterDisengage();
                return;
            }

            // 锁定目标 → 标记进战斗
            _model.MarkInCombat();

            // 尝试选择可释放技能（冷却就绪 + 目标在射程内）
            if (TryCastReadySkill(snapshot))
            {
                SetStopIntent();
                return;
            }

            // 节流：只控制目标点重算频率，不控制意图重申
            _chaseCommandTimer += deltaTime;
            if (!_hasChaseDestination || _chaseCommandTimer >= _config.ChaseCommandInterval)
            {
                _chaseCommandTimer = 0f;

                if (TryGetTargetPosition(snapshot.BestTargetId, out var targetPos))
                {
                    _chaseDestination = targetPos;
                    _hasChaseDestination = true;
                }
            }

            if (_hasChaseDestination)
            {
                BuildMoveIntent(
                    _chaseDestination, _config.ChaseMoveSpeed, MonsterGait.Run, _config.ChaseStopDistance);
            }
        }

        /// <summary>
        /// 巡逻：采样巡逻点 → 走过去 → 原地等待 → 再采样。
        /// </summary>
        private void TickPatrol(float deltaTime)
        {
            _intent = MonsterIntent.Patrol;

            // 正在原地等待
            if (_patrolWaitTimer > 0f)
            {
                _patrolWaitTimer -= deltaTime;
                if (_patrolWaitTimer > 0f)
                {
                    SetStopIntent();
                    return;
                }
            }

            // 已在巡逻点附近 → 进入原地等待
            if (_hasPatrolPoint
                && HorizontalDistance(SelfPosition, _currentPatrolPoint) <= _config.PatrolStopDistance)
            {
                _hasPatrolPoint = false;
                _patrolWaitTimer = Mathf.Max(0.1f, _config.PatrolWaitDuration);
                SetStopIntent();
                return;
            }

            // 还在走向巡逻点：每 tick 重建意图。
            // 不能只依赖"上次已下过指令"——受控 / 脱战期间意图会被改写为停止，
            // 旧实现在此处裸 return，导致意图再也不恢复，怪物永久卡死原地。
            if (_hasPatrolPoint)
            {
                BuildMoveIntent(
                    _currentPatrolPoint, _config.PatrolMoveSpeed, MonsterGait.Walk, _config.PatrolStopDistance);
                return;
            }

            // 采样新巡逻点（失败 → 走回巡逻中心）
            _currentPatrolPoint = TrySamplePatrolPoint(out var patrolPoint) ? patrolPoint : _model.PatrolCenter;
            _hasPatrolPoint = true;

            BuildMoveIntent(
                _currentPatrolPoint, _config.PatrolMoveSpeed, MonsterGait.Walk, _config.PatrolStopDistance);
        }

        /// <summary>
        /// 进入脱战：停移动 → 脱战冷却（Model 内：无敌 + 计时）
        /// </summary>
        private void EnterDisengage()
        {
            _intent = MonsterIntent.Idle;
            _patrolWaitTimer = 0f;
            ResetMoveDecisionCache();

            SetStopIntent();
            _model.StartCombatExitCooldown();
        }

        /// <summary>
        /// 选择并施放第一条冷却就绪且目标在射程内的技能链。
        /// 技能池 = Ctrl 序列化的链列表（§16.2），权重 0 不进池（权重排序筛选后续实现）。
        /// </summary>
        private bool TryCastReadySkill(MonsterPerceptionSnapshot snapshot)
        {
            if (_model.ChainCount == 0) return false;

            if (!TryGetTargetPosition(snapshot.BestTargetId, out var targetPos)) return false;

            Vector3 selfPos = SelfPosition;

            for (int i = 0; i < _model.ChainCount; i++)
            {
                var chain = _model.GetChain(i);
                if (chain is null) continue;

                // 权重 0 不进技能池
                if (chain.Weight <= 0) continue;

                // 未装配运行器的链不选（防"幽灵施放"；装配缺口已在 Spawned 校验报错）
                if (!_model.IsChainConfigured(i)) continue;

                if (!_model.IsChainReady(i)) continue;

                float attackDistance = chain.AttackDistance;
                if (HorizontalDistance(selfPos, targetPos) > Mathf.Max(0.1f, attackDistance)) continue;

                if (_model.TryCastChain(i, snapshot.BestTargetId))
                {
                    return true;
                }
            }

            return false;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 构建移动意图（下达由 <see cref="TickDecision"/> 末尾统一完成）
        /// </summary>
        private void BuildMoveIntent(Vector3 destination, float moveSpeed, MonsterGait gait, float stopDistance)
        {
            _moveIntent = new MonsterMoveCommand
            {
                IsStopped = false,
                Destination = destination,
                MoveSpeed = moveSpeed,
                Gait = gait,
                StopDistance = stopDistance,
            };
        }

        /// <summary>
        /// 把移动意图设为原地停止
        /// </summary>
        private void SetStopIntent()
        {
            _moveIntent = new MonsterMoveCommand { IsStopped = true, };
        }

        /// <summary>
        /// 清空移动决策缓存（追击目标点 / 巡逻点），迫使下次决策重算
        /// </summary>
        private void ResetMoveDecisionCache()
        {
            _hasChaseDestination = false;
            _chaseCommandTimer = 0f;
            _hasPatrolPoint = false;
        }

        /// <summary>
        /// 搜索圈 / 追击圈内是否有可追击目标（仅脱战圈内的目标视作追不上，不算）
        /// </summary>
        private static bool HasChasableTarget(MonsterPerceptionSnapshot snapshot)
        {
            return snapshot is { HasAnyTarget: true, RangeType: 1 or 2 };
        }

        /// <summary>
        /// 权威端本地解析目标位置（即用即弃：只取位置，不缓存引用）
        /// </summary>
        private bool TryGetTargetPosition(EntityId targetId, out Vector3 position)
        {
            position = default;
            if (!targetId.IsValid) return false;

            var targetObject = Framework.Network.NetworkMgr.FindObject(targetId.NetId);
            if (targetObject == null) return false;

            position = targetObject.transform.position;
            return true;
        }

        /// <summary>
        /// 在巡逻半径内采样随机巡逻点
        /// </summary>
        private bool TrySamplePatrolPoint(out Vector3 patrolPoint)
        {
            patrolPoint = _model.PatrolCenter;

            float radius = Mathf.Max(0.1f, _model.PatrolRadius);
            Vector3 center = _model.PatrolCenter;
            int attempts = Mathf.Max(1, _config.PatrolPointAttempts);

            for (int i = 0; i < attempts; i++)
            {
                Vector2 randomCircle = Random.insideUnitCircle * radius;
                var candidate = center + new Vector3(randomCircle.x, 0f, randomCircle.y);

                if (_hasPatrolPoint && HorizontalDistance(candidate, _currentPatrolPoint) < _config.PatrolStopDistance)
                {
                    continue;
                }

                patrolPoint = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 权威端本地怪物位置
        /// </summary>
        private Vector3 SelfPosition => _config.SelfTransform != null ? _config.SelfTransform.position : Vector3.zero;

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        #endregion
    }
}
