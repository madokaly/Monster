using Framework;
using Game.DTOs;
using Game.Entities;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 怪物弹道（本地简易子实体的行为主体）。
    /// 各端本地自治飞行与命中表现；命中目标时仅目标 StateAuthority 端发送伤害命令，
    /// 其余端只保留本端命中反馈。飞行结束后按延迟自毁，父步骤销毁时统一兜底回收。
    /// </summary>
    public class MonsterProjectile : MonoBehaviour
    {
        private readonly RaycastHit[] _hitBuffer = new RaycastHit[8];

        private Vector3 _direction;
        private float _speed;
        private float _maxDistance;
        private int _damage;
        private EntityId _attackerId;
        private int _damageLayerMask;
        private float _hitRadius;
        private float _hitHeight;
        private Vector3 _hitCenterOffset;
        private float _despawnDelay;
        private Vector3 _spawnPosition;
        private bool _initialized;

        /// <summary> 本端是否已命中目标或环境 </summary>
        public bool IsHit { get; private set; }

        /// <summary> 本端命中点（命中表现用） </summary>
        public Vector3 HitPoint { get; private set; }

        /// <summary> 飞行是否已结束（命中 / 航程耗尽） </summary>
        public bool FlightEnded { get; private set; }

        #region Lifecycle

        public void Initialize(
            Vector3 direction,
            float speed,
            float maxDistance,
            int damage,
            EntityId attackerId,
            int damageLayerMask,
            float hitRadius,
            float hitHeight,
            Vector3 hitCenterOffset,
            float despawnDelay)
        {
            _direction = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.forward;
            _speed = speed;
            _maxDistance = maxDistance;
            _damage = damage;
            _attackerId = attackerId;
            _damageLayerMask = damageLayerMask;
            _hitRadius = Mathf.Max(0f, hitRadius);
            _hitHeight = Mathf.Max(0f, hitHeight);
            _hitCenterOffset = hitCenterOffset;
            _despawnDelay = Mathf.Max(0f, despawnDelay);
            _spawnPosition = transform.position;
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized || FlightEnded) return;

            float step = _speed * Time.deltaTime;
            Vector3 nextPos = transform.position + _direction * step;

            // 命中判定：判定胶囊沿本端飞行路径扫掠；表现可分叉，结算只落在目标权威端。
            // 按约定不做生成初始重叠补判，只覆盖当前帧起点到下一位置的飞行段。
            float castDistance = step + 0.1f;
            bool hasHit = false;
            var hit = default(RaycastHit);

            if (_damageLayerMask != 0 && _hitRadius > 0f)
            {
                MonsterProjectileTiming.GetHitCapsuleAxisPoints(
                    _hitRadius,
                    _hitHeight,
                    transform.position,
                    transform.rotation,
                    _hitCenterOffset,
                    out Vector3 lowerPoint,
                    out Vector3 upperPoint
                );

                int hitCount = Physics.CapsuleCastNonAlloc(
                    lowerPoint,
                    upperPoint,
                    _hitRadius,
                    _direction,
                    _hitBuffer,
                    castDistance,
                    _damageLayerMask,
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
                HitPoint = hit.point;
                IsHit = true;

                // 命中实体（身份解析即用即弃）：仅目标权威端结算；其他端只表现
                var tag = hit.collider.GetComponentInParent<EntityTag>();
                if (tag != null
                    && tag.Id.IsValid
                    && MonsterSkillDamage.IsTargetAuthoritativeHere(tag.Id))
                {
                    var damageData = new DamageData { Damage = _damage, AttackerId = _attackerId, };

                    var hitData = new HitData()
                    {
                        HitPoint = hit.point,
                        HitDirection = _direction,
                        Force = 0f,
                    };

                    Msger.Send(MsgID.ApplyDamage, tag.Id, damageData);
                    Msger.Send(MsgID.ApplyHit, tag.Id, hitData);
                }

                EndFlight();
                return;
            }

            transform.position = nextPos;

            // 航程耗尽
            if (Vector3.Distance(transform.position, _spawnPosition) >= _maxDistance)
            {
                EndFlight();
            }
        }

        #endregion

        #region Private Methods

        private void EndFlight()
        {
            if (FlightEnded) return;

            FlightEnded = true;
            Destroy(gameObject, _despawnDelay);
        }

        #endregion
    }
}
