using System;
using Framework;
using Framework.Core;
using Game.Components;
using Pathfinding;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterMoveModuleConfig
    {
        [Header("References")]
        [Tooltip("寻路移动代理（A* FollowerEntity，仅权威端模拟移动）")]
        public FollowerEntity Follower;

        [Tooltip("怪物本体（朝向旋转用）")]
        public Transform Body;

        [Header("Setup")]
        [Tooltip("强制修正 FollowerEntity.orientation 为 ZAxisForward（XZ 平面），修复 PathTracer 轴并列连接异常")]
        public bool FixOrientation = true;

        [Tooltip("生成时吸附 NavMesh 的最大距离（0 = 不吸附）")]
        public float SnapMaxDistance = 100f;

        [Tooltip("移动目标投影到 NavMesh 的最大水平距离（米）")]
        public float DestinationProjectionMaxHorizontalDistance = 3f;
        [Tooltip("移动目标投影到 NavMesh 的最大垂直距离（米）")]
        public float DestinationProjectionMaxVerticalDistance = 10f;
    }

    /// <summary>
    /// 怪物移动模块：监听 Model 移动指令（权威端派生数据）驱动 FollowerEntity。
    /// 全部 A* 兼容层（投影 / 轴并列过滤 / 朝向）内聚于此。
    /// 代理端不模拟移动，位置由 NetworkTransform 同步。
    /// </summary>
    public class MonsterMoveModule : ModuleBase
    {
        private readonly MonsterModel _model;
        private readonly MonsterMoveModuleConfig _config;

        private bool _snapped;

        /// <summary> 不可达移动目标告警间隔（秒） </summary>
        private const float INVALID_DESTINATION_WARNING_INTERVAL = 5f;

        /// <summary> 不可达移动目标告警计时 </summary>
        private float _invalidDestinationWarningTimer;

        #region Lifecycle

        public MonsterMoveModule(MonsterModel model, MonsterMoveModuleConfig config)
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
            if (!HasStateAuthority) return;
            if (_config is null) return;

            EnsureFollowerEntityOrientation();
            SnapToNavMesh();
            RefreshFrozenState();
        }

        protected override void OnFixedUpdateNetwork(float deltaTime)
        {
            if (_model is null) return;
            if (_config is null) return;
            if (!HasStateAuthority) return;

            if (_invalidDestinationWarningTimer > 0f)
            {
                _invalidDestinationWarningTimer -= deltaTime;
            }

            ReportMoveSpeedFact();
        }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;

            _model.OnTeleportPositionPushed += OnTeleportPositionPushedHandler;
            _model.OnTeleportRotationPushed += OnTeleportRotationPushedHandler;
            _model.OnBodyRotationPushed += OnBodyRotationPushedHandler;

            _model.OnMoveCommandChanged += OnMoveCommandChangedHandler;
            _model.OnSimulationOwnerChanged += OnSimulationOwnerChangedHandler;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;

            _model.OnTeleportPositionPushed -= OnTeleportPositionPushedHandler;
            _model.OnTeleportRotationPushed -= OnTeleportRotationPushedHandler;
            _model.OnBodyRotationPushed -= OnBodyRotationPushedHandler;

            _model.OnMoveCommandChanged -= OnMoveCommandChangedHandler;
            _model.OnSimulationOwnerChanged -= OnSimulationOwnerChangedHandler;
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

        private void OnTeleportPositionPushedHandler(Vector3 position)
        {
            if (_config is null) return;
            if (!HasStateAuthority) return;

            if (_config.Body == null)
            {
                Logging.Error($"[MonsterMoveModule] OnTeleportPositionPushedHandler: _config.Body 为 null");
                return;
            }

            _config.Body.transform.position = position;
            _config.Body.position = position;

            // 必须同步 FollowerEntity 的内部位置：它按 transform 位置创建实体后自持一份位置，
            // 且 syncPosition 每帧把内部位置写回 transform——只改 transform 会被它当场拽回，
            // 并让寻路从错误的起点出发（怪物出生正是靠本推送摆位的）。
            var follower = _config.Follower;
            if (follower != null) follower.Teleport(position);
        }

        private void OnTeleportRotationPushedHandler(Quaternion rotation)
        {
            if (_config is null) return;
            if (!HasStateAuthority) return;

            if (_config.Body == null)
            {
                Logging.Error($"[MonsterMoveModule] OnTeleportRotationPushedHandler: _config.Body 为 null");
                return;
            }

            _config.Body.transform.rotation = rotation;
            _config.Body.rotation = rotation;

            var follower = _config.Follower;
            if (follower != null) follower.rotation = rotation;
        }

        /// <summary>
        /// 本体旋转推送（权威端逐 tick，如 Beam 吐息转向跟踪）。
        /// 施放期移动虽停（IsStopped），ECS 代理仍持续朝既有 targetRotation
        /// （停止前的旧朝向，无路径分支）以 maxRotationSpeed 回拽——写实体内部旋转同样会被拽回；
        /// 必须关闭 updateRotation 斩断 ECS → transform 的旋转回写，transform 直写才生效，
        /// 移动指令恢复时由 EnsureRotationOwnedByFollower 归还所有权。
        /// </summary>
        private void OnBodyRotationPushedHandler(Quaternion rotation)
        {
            if (_config is null) return;
            if (!HasStateAuthority) return;

            if (_config.Body == null)
            {
                Logging.Error($"[MonsterMoveModule] OnBodyRotationPushedHandler: _config.Body 为 null");
                return;
            }

            var follower = _config.Follower;
            if (follower != null && follower.updateRotation)
            {
                follower.updateRotation = false;
            }

            _config.Body.rotation = rotation;

            // 内部旋转同步对齐（updateRotation 已关仅内部生效），恢复所有权时零跳变
            if (follower != null) follower.rotation = rotation;
        }

        /// <summary>
        /// 归还 FollowerEntity 的旋转所有权（移动指令恢复时调用）：
        /// 重新开启 updateRotation，并把代理内部旋转对齐当前 transform，避免回开瞬间回甩。
        /// </summary>
        private void EnsureRotationOwnedByFollower(FollowerEntity follower)
        {
            if (follower == null) return;
            if (follower.updateRotation) return;

            follower.updateRotation = true;
            if (_config.Body != null) follower.rotation = _config.Body.rotation;
        }

        /// <summary>
        /// 归属事实变化（全端）：仅权威端反应——冻结（owner 无效）关停移动模拟，
        /// 解冻恢复模拟开关（移动指令由 AI 决策随后的 tick 重新下达）。
        /// </summary>
        private void OnSimulationOwnerChangedHandler(EntityId ownerId)
        {
            if (_config is null) return;
            if (!HasStateAuthority) return;

            RefreshFrozenState();
        }

        private void OnMoveCommandChangedHandler(MonsterMoveCommand command)
        {
            if (_config is null) return;
            if (!HasStateAuthority) return;

            if (_config.Follower == null)
            {
                Logging.Error("[MonsterMoveModule] OnMoveCommandChanged: Follower 为 null，请检查 Inspector 引用。");
                return;
            }

            if (command.IsStopped)
            {
                StopMovementInternal();
                return;
            }

            // 目标必须能从当前位置到达；否则停止移动，不能把 NavMesh 外坐标交给寻路代理
            var follower = _config.Follower;
            if (!MonsterNavigationQuery.TryProjectToReachableNode(
                    follower.transform.position,
                    command.Destination,
                    _config.DestinationProjectionMaxHorizontalDistance,
                    _config.DestinationProjectionMaxVerticalDistance,
                    out var safeDestination))
            {
                StopMovementInternal();
                WarnInvalidDestination(follower, command.Destination);
                return;
            }

            PrepareMovementInternal(command.MoveSpeed, command.StopDistance);
            follower.destination = safeDestination;
        }

        #endregion

        #region API

        /// <summary>
        /// 查询: 位置
        /// </summary>
        public Vector3 QueryPosition()
        {
            if (_config == null) return default;
            if (_config.Body == null) return default;

            return _config.Body.transform.position;
        }

        /// <summary>
        /// 查询: 旋转
        /// </summary>
        public Quaternion QueryRotation()
        {
            if (_config == null) return default;
            if (_config.Body == null) return default;

            return _config.Body.transform.rotation;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// 回流实际水平移动速度事实（权威端）。
        /// 这是"怪物此刻在不在动"的<b>唯一可信来源</b>——移动指令只是意图，
        /// 到停距站定 / 被挤住 / 爬坡 / 绕路时意图与事实并不一致，
        /// locomotion 动画必须据事实投影（见 MonsterModel.Anim.cs）。
        /// </summary>
        private void ReportMoveSpeedFact()
        {
            var follower = _config.Follower;
            if (follower == null)
            {
                _model.SetMoveSpeedFact(0f);
                return;
            }

            var velocity = follower.velocity;
            velocity.y = 0f;

            _model.SetMoveSpeedFact(velocity.magnitude);
        }

        /// <summary>
        /// 准备移动代理（速度 / 停距 / 移动模拟开关；移动恢复即归还旋转所有权）
        /// </summary>
        private void PrepareMovementInternal(float moveSpeed, float stopDistance)
        {
            var follower = _config.Follower;

            EnsureRotationOwnedByFollower(follower);

            follower.maxSpeed = Mathf.Max(0.1f, moveSpeed);
            follower.stopDistance = Mathf.Max(0.1f, stopDistance);
            follower.isStopped = false;

            if (!follower.simulateMovement) follower.simulateMovement = true;
            if (!follower.updatePosition) follower.updatePosition = true;
        }

        /// <summary>
        /// 停止移动代理
        /// </summary>
        private void StopMovementInternal()
        {
            var follower = _config.Follower;

            follower.destination = follower.transform.position;
            follower.isStopped = true;
        }

        /// <summary>
        /// 模拟门控 = owner 事实指向本端玩家实体且本地环境就绪（票 18：启动 / 恢复
        /// 都验证本地物理与导航）：冻结（owner 无效）、owner 指向其他端（权威交接瞬态）或
        /// 本端覆盖 / 寻路图缺失 → 停 Follower + 关移动模拟（避免无图模拟）；
        /// 解冻 / 就绪恢复只重开模拟开关，具体移动由 AI 决策下一 tick 的移动指令驱动自愈。
        /// </summary>
        private void RefreshFrozenState()
        {
            var follower = _config.Follower;
            if (follower == null)
            {
                Logging.Error("[MonsterMoveModule] RefreshFrozenState: Follower 为 null，请检查 Inspector 引用。");
                return;
            }

            bool simulate = IsOwnerLocalPlayer() && IsEnvironmentReady();

            if (!simulate)
            {
                StopMovementInternal();
                follower.simulateMovement = false;
            }
            else
            {
                follower.simulateMovement = true;
            }
        }

        /// <summary> owner 事实是否指向本端玩家实体（MainPlayer 或本端玩家驾驶的 Mech） </summary>
        private bool IsOwnerLocalPlayer()
        {
            var owner = _model.SimulationOwner;
            if (!owner.IsValid) return false;
            if (owner == Svcer.Req<EntityId>(SvcID.QueryLocalPlayer)) return true;
            return owner == Svcer.Req<EntityId>(SvcID.QueryLocalMech);
        }

        /// <summary> 本地环境就绪：primary Chunk Ready（纯查询）+ 导航图在场 </summary>
        private bool IsEnvironmentReady()
        {
            if (_config.Body == null) return false;

            return LocalPhysicsCoverage.CanSimulateAt(_config.Body.position) && AstarPath.active != null;
        }

        /// <summary>
        /// 强制修正 FollowerEntity 运动平面为 XZ（3D 水平面）。
        /// 若 orientation = YAxisForward（XY 平面 / 2D 模式），
        /// PathTracer.RemoveGridPathDiagonals 会在 Y 轴方向查找连接而找不到，
        /// 从而抛出 "Axis-aligned connection not found"。
        /// </summary>
        private void EnsureFollowerEntityOrientation()
        {
            if (!_config.FixOrientation) return;

            var follower = _config.Follower;
            if (follower == null) return;
            if (follower.orientation == OrientationMode.ZAxisForward) return;

            Logging.Warning(
                $"[MonsterMoveModule] FollowerEntity.orientation={follower.orientation}，"
                + "已强制修正为 ZAxisForward (XZ 平面)，以修复 PathTracer 轴并列连接异常。"
                + "请在 Prefab Inspector 中将 FollowerEntity 的 Orientation 设为 ZAxisForward。"
            );
            follower.orientation = OrientationMode.ZAxisForward;
        }

        /// <summary>
        /// 出生兜底：吸附到最近可行走节点（Spawner 采样已保证合法，此处仅兜底）
        /// </summary>
        private void SnapToNavMesh()
        {
            if (_snapped) return;
            if (_config.SnapMaxDistance <= 0f) return;
            if (AstarPath.active == null) return;

            _snapped = true;

            var follower = _config.Follower;
            var nearest = AstarPath.active.GetNearest(follower.transform.position, NearestNodeConstraint.Walkable);
            if (nearest.node == null || !nearest.node.Walkable) return;

            Vector3 snapPos = (Vector3)nearest.position;
            float dist = Vector3.Distance(snapPos, follower.transform.position);
            if (dist < 0.05f) return;
            if (dist > _config.SnapMaxDistance)
            {
                Logging.Warning(
                    $"[MonsterMoveModule] SnapToNavMesh: 离 NavMesh 最近节点 {dist:F2}m 超过 {_config.SnapMaxDistance}m 阈值，"
                    + $"仍强制 snap (pos={follower.transform.position}, snap={snapPos})"
                );
            }

            follower.transform.position = snapPos;
            follower.Teleport(snapPos);
        }

        /// <summary>
        /// 记录不可达移动目标；节流告警，避免追踪目标持续变化时刷屏
        /// </summary>
        private void WarnInvalidDestination(FollowerEntity follower, Vector3 destination)
        {
            if (_invalidDestinationWarningTimer > 0f) return;

            _invalidDestinationWarningTimer = INVALID_DESTINATION_WARNING_INTERVAL;
            Logging.Warning(
                $"[MonsterMoveModule] 移动目标不可达，已停止移动 "
                + $"(self={follower.transform.position}, destination={destination})"
            );
        }

        #endregion
    }
}
