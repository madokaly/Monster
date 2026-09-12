using Pathfinding;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物 NavMesh 位置查询的共享入口。
    /// 查询只做节点投影与图连通性判断，不缓存任何节点或实体引用。
    /// </summary>
    internal static class MonsterNavigationQuery
    {
        /// <summary>
        /// 将世界坐标投影为满足水平 / 垂直容差的 Walkable 节点。
        /// </summary>
        public static bool TryProjectToWalkableNode(
            Vector3 position,
            float maxHorizontalDistance,
            float maxVerticalDistance,
            out GraphNode node,
            out Vector3 projectedPosition)
        {
            node = null;
            projectedPosition = position;
            if (AstarPath.active == null) return false;

            var constraint = NearestNodeConstraint.Walkable;
            constraint.distanceMetric = DistanceMetric.ClosestAsSeenFromAbove();

            node = AstarPath.active.GetNearest(position, constraint).node;
            if (node == null || !node.Walkable) return false;

            projectedPosition = (Vector3)node.position;
            return IsWithinProjectionDistance(
                position,
                projectedPosition,
                maxHorizontalDistance,
                maxVerticalDistance);
        }

        /// <summary>
        /// 判断目标投影点是否与指定起始节点处于同一可达区域。
        /// </summary>
        public static bool TryProjectToReachableNode(
            GraphNode startNode,
            Vector3 targetPosition,
            float maxHorizontalDistance,
            float maxVerticalDistance,
            out Vector3 projectedPosition)
        {
            projectedPosition = targetPosition;
            if (startNode == null) return false;
            if (!TryProjectToWalkableNode(
                    targetPosition,
                    maxHorizontalDistance,
                    maxVerticalDistance,
                    out var targetNode,
                    out projectedPosition))
            {
                return false;
            }

            return HasValidAxisAlignedConnections(targetNode)
                   && PathUtilities.IsPathPossible(startNode, targetNode);
        }

        /// <summary>
        /// 将目标投影为从当前位置可达的安全落点。
        /// </summary>
        public static bool TryProjectToReachableNode(
            Vector3 startPosition,
            Vector3 targetPosition,
            float maxHorizontalDistance,
            float maxVerticalDistance,
            out Vector3 projectedPosition)
        {
            projectedPosition = targetPosition;
            if (!TryProjectToWalkableNode(
                    startPosition,
                    maxHorizontalDistance,
                    maxVerticalDistance,
                    out var startNode,
                    out _))
            {
                return false;
            }

            return TryProjectToReachableNode(
                startNode,
                targetPosition,
                maxHorizontalDistance,
                maxVerticalDistance,
                out projectedPosition);
        }

        /// <summary>
        /// 检查 Grid 节点四向轴连接，避免把移动目标落在会造成路径追踪异常的边缘节点。
        /// </summary>
        public static bool HasValidAxisAlignedConnections(GraphNode node)
        {
            if (node is not GridNodeBase gridNode) return true;

            for (int direction = 0; direction < 4; direction++)
            {
                var neighbor = gridNode.GetNeighbourAlongDirection(direction);
                if (neighbor == null || !neighbor.Walkable) return false;
            }

            return true;
        }

        private static bool IsWithinProjectionDistance(
            Vector3 position,
            Vector3 projectedPosition,
            float maxHorizontalDistance,
            float maxVerticalDistance)
        {
            Vector3 horizontalDelta = position - projectedPosition;
            horizontalDelta.y = 0f;

            float horizontalLimit = Mathf.Max(0f, maxHorizontalDistance);
            float verticalLimit = Mathf.Max(0f, maxVerticalDistance);

            return horizontalDelta.sqrMagnitude <= horizontalLimit * horizontalLimit
                   && Mathf.Abs(position.y - projectedPosition.y) <= verticalLimit;
        }
    }
}