using System.Collections.Generic;
using Framework;
using Framework.Network;
using Game.Components;
using Game.DTOs;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 怪物技能伤害结算共享管线（步骤与召唤物行为共用，单点维护）。
    /// 受击方本地结算（判定端 = 目标权威端）：只结算 SA 在本端的目标——
    /// 判定基准为本端真实位置 + 本端技能表现时间轴（所见即所得，消除代理延迟误伤），
    /// 伤害经端内 Msger 直接落到本端目标 Model（零 RPC）。网络目标查其 NetworkObject 权威；
    /// 本地实体只存在于本端，恒可结算。非本端目标撤销去重登记，目标权威端的轮询仍可命中。
    /// 物理查询 → 几何过滤 → 高度过滤 → 身份解析（即用即弃）→ 组装
    /// DamageData / HitData → ApplyDamage + ApplyHit；hitTargets 由调用方维护
    /// （窗口 / 波次 / 圈各维去重）；maxTargets > 0 时限制单次结算目标数（0 = 无限制）；
    /// maxAttackHeight <= 0 时不过滤高度。
    /// </summary>
    public static class MonsterSkillDamage
    {
        private static readonly Collider[] _overlapBuffer = new Collider[64];
        private static readonly RaycastHit[] _capsuleCastBuffer = new RaycastHit[64];

        /// <summary>
        /// 全圆结算（内半径 0）。
        /// </summary>
        public static void SettleSphere(
            EntityId attackerId,
            Vector3 center,
            float radius,
            LayerMask damageLayer,
            float maxAttackHeight,
            int damage,
            float hitForce,
            HashSet<EntityId> hitTargets,
            Vector3 fallbackDirection,
            int maxTargets = 0)
        {
            SettleAnnulus(
                attackerId,
                center,
                0f,
                radius,
                damageLayer,
                maxAttackHeight,
                damage,
                hitForce,
                hitTargets,
                fallbackDirection,
                maxTargets
            );
        }

        /// <summary>
        /// 环带结算（目标到圆心的平面距离落入 [innerRadius, outerRadius]；innerRadius 0 = 全圆）。
        /// </summary>
        public static void SettleAnnulus(
            EntityId attackerId,
            Vector3 center,
            float innerRadius,
            float outerRadius,
            LayerMask damageLayer,
            float maxAttackHeight,
            int damage,
            float hitForce,
            HashSet<EntityId> hitTargets,
            Vector3 fallbackDirection,
            int maxTargets = 0)
        {
            int count = Physics.OverlapSphereNonAlloc(
                center,
                outerRadius,
                _overlapBuffer,
                damageLayer,
                QueryTriggerInteraction.Ignore
            );

            int settled = 0;
            for (int i = 0; i < count; i++)
            {
                if (maxTargets > 0 && settled >= maxTargets) break;

                var col = _overlapBuffer[i];
                if (col == null) continue;

                // 环带几何（innerRadius > 0 时生效）
                if (innerRadius > 0f)
                {
                    Vector3 toTarget = col.transform.position - center;
                    toTarget.y = 0f;
                    if (toTarget.magnitude < innerRadius) continue;
                }

                if (!TrySettleHitFromOrigin(
                        col,
                        attackerId,
                        maxAttackHeight,
                        damage,
                        hitForce,
                        hitTargets,
                        center,
                        fallbackDirection
                    ))
                {
                    continue;
                }

                settled++;
            }
        }

        /// <summary>
        /// 竖直身体胶囊的水平扫掠结算。CapsuleCast 覆盖起点到当前扫掠路径；
        /// 起点重叠由 OverlapCapsule 补齐（Cast 不保证报告初始重叠）。
        /// </summary>
        public static void SettleSweptCapsule(
            EntityId attackerId,
            Vector3 startLowerPoint,
            Vector3 startUpperPoint,
            Vector3 sweepVector,
            float radius,
            LayerMask damageLayer,
            float maxAttackHeight,
            int damage,
            float hitForce,
            HashSet<EntityId> hitTargets,
            Vector3 fallbackDirection,
            int maxTargets = 0)
        {
            if (radius <= 0f) return;

            int overlapCount = Physics.OverlapCapsuleNonAlloc(
                startLowerPoint,
                startUpperPoint,
                radius,
                _overlapBuffer,
                damageLayer,
                QueryTriggerInteraction.Ignore
            );

            int settled = 0;
            for (int i = 0; i < overlapCount; i++)
            {
                if (maxTargets > 0 && settled >= maxTargets) break;

                var col = _overlapBuffer[i];
                if (col == null) continue;

                Vector3 closestPoint = ClosestPointOnSegment(
                    startLowerPoint,
                    startUpperPoint,
                    col.transform.position
                );
                if (!TrySettleHitFromOrigin(
                        col,
                        attackerId,
                        maxAttackHeight,
                        damage,
                        hitForce,
                        hitTargets,
                        closestPoint,
                        fallbackDirection
                    ))
                {
                    continue;
                }

                settled++;
            }

            Vector3 planarSweep = sweepVector;
            planarSweep.y = 0f;
            float sweepDistance = planarSweep.magnitude;
            if (sweepDistance <= Mathf.Epsilon) return;

            int castCount = Physics.CapsuleCastNonAlloc(
                startLowerPoint,
                startUpperPoint,
                radius,
                planarSweep / sweepDistance,
                _capsuleCastBuffer,
                sweepDistance,
                damageLayer,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < castCount; i++)
            {
                if (maxTargets > 0 && settled >= maxTargets) break;

                var hit = _capsuleCastBuffer[i];
                var col = hit.collider;
                if (col == null) continue;

                Vector3 hitDirection = fallbackDirection;
                if (!TrySettleHit(
                        col,
                        attackerId,
                        maxAttackHeight,
                        damage,
                        hitForce,
                        hitTargets,
                        hit.point,
                        hitDirection,
                        hit.point.y
                    ))
                {
                    continue;
                }

                settled++;
            }
        }

        private static bool TrySettleHitFromOrigin(
            Collider col,
            EntityId attackerId,
            float maxAttackHeight,
            int damage,
            float hitForce,
            HashSet<EntityId> hitTargets,
            Vector3 directionOrigin,
            Vector3 fallbackDirection)
        {
            Vector3 hitPoint = col.transform.position;
            Vector3 hitDirection = hitPoint - directionOrigin;
            hitDirection.y = 0f;
            hitDirection = hitDirection.sqrMagnitude > 0.001f
                ? hitDirection.normalized
                : fallbackDirection;

            return TrySettleHit(
                col,
                attackerId,
                maxAttackHeight,
                damage,
                hitForce,
                hitTargets,
                hitPoint,
                hitDirection,
                directionOrigin.y
            );
        }

        private static bool TrySettleHit(
            Collider col,
            EntityId attackerId,
            float maxAttackHeight,
            int damage,
            float hitForce,
            HashSet<EntityId> hitTargets,
            Vector3 hitPoint,
            Vector3 hitDirection,
            float heightOriginY)
        {
            // 高度过滤（对称竖向带，maxAttackHeight > 0 时生效）
            if (maxAttackHeight > 0f && Mathf.Abs(col.transform.position.y - heightOriginY) > maxAttackHeight)
            {
                return false;
            }

            // 身份解析：即用即弃，只提取 EntityId
            var tag = col.GetComponentInParent<EntityTag>();
            if (tag == null) return false;

            EntityId targetId = tag.Id;
            if (!targetId.IsValid) return false;

            // 受击方本地结算：非本端权威的目标交由目标权威端自行判定；
            // 撤销去重登记，目标权威端的轮询仍可命中该目标
            if (!IsTargetAuthoritativeHere(targetId))
            {
                hitTargets.Remove(targetId);
                return false;
            }

            if (!hitTargets.Add(targetId)) return false;

            var damageData = new DamageData { Damage = damage, AttackerId = attackerId, };

            var hitData = new HitData
            {
                HitPoint = hitPoint,
                HitDirection = hitDirection,
                Force = hitForce,
            };

            Msger.Send(MsgID.ApplyDamage, targetId, damageData);
            Msger.Send(MsgID.ApplyHit, targetId, hitData);
            return true;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 start, Vector3 end, Vector3 point)
        {
            Vector3 segment = end - start;
            float lengthSqr = segment.sqrMagnitude;
            if (lengthSqr <= Mathf.Epsilon) return start;

            float progress = Vector3.Dot(point - start, segment) / lengthSqr;
            return start + segment * Mathf.Clamp01(progress);
        }

        /// <summary>
        /// 受击方本地结算的目标过滤：目标 SA 在本端才由本端结算（网络目标查 NetworkObject 权威；
        /// 本地实体只存在于本端，恒为真）。供管线外的手写判定循环（Beam / Burrow 地下撞伤 / InstantShot）复用。
        /// </summary>
        public static bool IsTargetAuthoritativeHere(EntityId targetId)
        {
            if (!targetId.IsNetworked) return true;

            return NetworkMgr.TryFindObject(targetId.NetId, out var netObject)
                   && netObject != null
                   && netObject.HasStateAuthority;
        }
    }
}