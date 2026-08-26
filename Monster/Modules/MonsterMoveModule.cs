using System;
using Framework;
using Framework.Core;
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
        }

        protected override void OnFixedUpdateNetwork(float deltaTime)
        {
            if (_model is null) return;
            if (_config is null) return;
            if (!HasStateAuthority) return;

            ReportMoveSpeedFact();
        }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;

            _model.OnTeleportPositionPushed += OnTeleportPositionPushedHandler;
            _model.OnTeleportRotationPushed += OnTeleportRotationPushedHandler;

            _model.OnMoveCommandChanged += OnMoveCommandChangedHandler;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;

            _model.OnTeleportPositionPushed -= OnTeleportPositionPushedHandler;
            _model.OnTeleportRotationPushed -= OnTeleportRotationPushedHandler;

            _model.OnMoveCommandChanged -= OnMoveCommandChangedHandler;
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

            // 目标点投影到合法节点（防 GridGraph 边界 / 斜向连接异常）
            var destination = command.Destination;
            if (TryProjectToValidPoint(destination, null, out var safeDestination))
            {
                destination = safeDestination;
            }

            PrepareMovementInternal(command.MoveSpeed, command.StopDistance);
            _config.Follower.destination = destination;
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
        /// 准备移动代理（速度 / 停距 / 移动模拟开关）
        /// </summary>
        private void PrepareMovementInternal(float moveSpeed, float stopDistance)
        {
            var follower = _config.Follower;

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
        /// 将世界坐标投影到最近的合法可行走节点，同时过滤缺失轴并列连接的边缘节点。
        /// </summary>
        private static bool TryProjectToValidPoint(
            Vector3 worldPosition,
            System.Collections.Generic.IReadOnlyList<int> allowedGraphTags,
            out Vector3 result)
        {
            result = worldPosition;

            if (AstarPath.active == null) return false;

            var constraint = NearestNodeConstraint.Walkable;
            constraint.tags = BuildAllowedGraphTagsMask(allowedGraphTags);
            constraint.distanceMetric = DistanceMetric.ClosestAsSeenFromAbove();

            var nearestInfo = AstarPath.active.GetNearest(worldPosition, constraint);
            if (nearestInfo.node == null) return false;

            if (!HasValidAxisAlignedConnections(nearestInfo.node)) return false;

            result = (Vector3)nearestInfo.position;
            return true;
        }

        /// <summary>
        /// 检查 GridNodeBase 四个轴并列方向（上下左右）是否都存在有效的可行走连接。
        /// 若某方向缺失，PathTracer.RemoveGridPathDiagonals 会抛出 "Axis-aligned connection not found"。
        /// </summary>
        private static bool HasValidAxisAlignedConnections(GraphNode node)
        {
            if (node is not GridNodeBase gridNode) return true; // 非 Grid 图无需检查
            for (int dir = 0; dir < 4; dir++)
            {
                var neighbor = gridNode.GetNeighbourAlongDirection(dir);
                if (neighbor == null || !neighbor.Walkable) return false;
            }
            return true;
        }

        /// <summary>
        /// 构建 A* Graph Tag 位掩码（未配置时默认允许全部标签）
        /// </summary>
        private static int BuildAllowedGraphTagsMask(System.Collections.Generic.IReadOnlyList<int> allowedGraphTags)
        {
            if (allowedGraphTags == null || allowedGraphTags.Count == 0) return -1;

            int mask = 0;
            for (int i = 0; i < allowedGraphTags.Count; i++)
            {
                int tag = allowedGraphTags[i];
                if (tag is < 0 or >= 32) continue;

                mask |= 1 << tag;
            }

            return mask == 0 ? -1 : mask;
        }

        #endregion
    }
}
