using System;
using Framework;
using Framework.Core;
using Fusion;
using Game.DTOs;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 怪物弹道（网络子实体·简易形态身份主体）。
    /// 只公开只读身份字段（EntityId 等）与父实体回调（OnFlightEnded），无对外行为方法；
    /// 权威端本地自治飞行判定，命中 / 航程耗尽即结束，由父实体（技能模块）全权管理销毁。
    /// </summary>
    public class MonsterProjectile : NetworkBehaviour
    {
        private readonly RaycastHit[] _hitBuffer = new RaycastHit[8];

        // ===== 预初始化状态（onBeforeSpawned 由权威端写入，全端可读）=====

        [Networked]
        public Vector3 Direction { get; private set; }

        [Networked]
        public float Speed { get; private set; }

        [Networked]
        public float MaxDistance { get; private set; }

        [Networked]
        public int Damage { get; private set; }

        [Networked]
        public EntityId AttackerId { get; private set; }

        [Networked]
        public EntityId TargetId { get; private set; }

        [Networked]
        public int DamageLayerMask { get; private set; }

        // ===== 命中事实（全端感知，驱动命中特效）=====

        [Networked, OnChangedRender(nameof(OnIsHitChangedHandler))]
        public NetworkBool IsHit { get; private set; }

        [Networked]
        public Vector3 HitPoint { get; private set; }

        /// <summary> 飞行结束（命中 / 航程耗尽，仅权威端触发；父实体监听后销毁）</summary>
        public event Action<MonsterProjectile> OnFlightEnded;

        private Vector3 _spawnPos;
        private bool _flightEnded;

        #region Lifecycle

        public void PreInit(
            Vector3 direction,
            float speed,
            float maxDistance,
            int damage,
            EntityId attackerId,
            EntityId targetId,
            int damageLayerMask)
        {
            Direction = direction;
            Speed = speed;
            MaxDistance = maxDistance;
            Damage = damage;
            AttackerId = attackerId;
            TargetId = targetId;
            DamageLayerMask = damageLayerMask;
            IsHit = false;
            HitPoint = default;
        }

        public override void Spawned()
        {
            _spawnPos = transform.position;
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority) return;
            if (_flightEnded) return;

            float step = Speed * Runner.DeltaTime;
            Vector3 nextPos = transform.position + Direction * step;

            // 命中判定：沿飞行路径扫掠（本地表现时间轴为基准）
            float castDistance = step + 0.1f;
            bool hasHit = false;
            var hit = default(RaycastHit);

            if (DamageLayerMask != 0)
            {
                int hitCount = Physics.RaycastNonAlloc(
                    transform.position,
                    Direction,
                    _hitBuffer,
                    castDistance,
                    DamageLayerMask,
                    QueryTriggerInteraction.Ignore
                );

                float nearest = float.MaxValue;
                for (int i = 0; i < hitCount; i++)
                {
                    var candidate = _hitBuffer[i];
                    if (candidate.collider == null) continue;
                    if (candidate.distance >= nearest) continue;
                    nearest = candidate.distance;
                    hit = candidate;
                    hasHit = true;
                }
            }

            if (hasHit)
            {
                // 命中事实（全端感知，驱动命中特效）
                HitPoint = hit.point;
                IsHit = true;

                // 命中实体（身份解析即用即弃）：发 ApplyDamage 出伤害；命中环境则只停
                var tag = hit.collider.GetComponentInParent<EntityTag>();
                if (tag != null && tag.Id.IsValid)
                {
                    var damageData = new DamageData { Damage = Damage, AttackerId = AttackerId, };

                    var hitData = new HitData()
                    {
                        HitPoint = hit.point, HitDirection = Direction, Force = 0f,
                    };

                    Msger.Send(MsgID.ApplyDamage, tag.Id, damageData);
                    Msger.Send(MsgID.ApplyHit, tag.Id, hitData);
                }

                EndFlight();
                return;
            }

            transform.position = nextPos;

            // 航程耗尽
            if (Vector3.Distance(transform.position, _spawnPos) >= MaxDistance)
            {
                EndFlight();
            }
        }

        #endregion

        #region Private Methods

        private void EndFlight()
        {
            if (_flightEnded) return;
            _flightEnded = true;

            OnFlightEnded?.Invoke(this);
        }

        private void OnIsHitChangedHandler()
        {
            if (HasStateAuthority) return;
            // 代理端命中表现由视觉组件轮询本字段，无需事件
        }

        #endregion
    }
}
