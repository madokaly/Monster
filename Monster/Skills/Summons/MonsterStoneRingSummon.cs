using System;
using System.Collections.Generic;
using Framework;
using Game.Entities;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 地刺单环配置：容器引用 + 升起时序 + 该环判定与冲击数值。
    /// </summary>
    [Serializable]
    public class MonsterStoneRingConfig
    {
        [Tooltip("环容器（该圈所有石头子物体，逐石贴地与预演升起的操作对象）")]
        public Transform Ring;

        [Tooltip("升起时刻（秒，召唤物生成起算）")]
        public float RiseTime = 1.7f;

        [Tooltip("环带中径（米，以召唤物为圆心的驻场判定带）")]
        public float Radius = 20f;

        [Tooltip("环带厚度（米；判定带 = [Radius ± Thickness/2]）")]
        public float Thickness = 4f;

        [Tooltip("环带命中伤害（每圈每目标一次，后进入者后续轮询命中）")]
        public int Damage = 1500;

        [Tooltip("升起时全圆冲击半径（米，0 = 关闭）")]
        public float BlastRadius = 8f;

        [Tooltip("升起冲击伤害")]
        public int BlastDamage = 1500;
    }

    /// <summary>
    /// 地刺召唤物（boss0007 skill6）：双圈刺环升起 → 驻场环带判定 → Break 全圆爆炸（自毁）。
    /// 升起 = 逐石 raycast 贴地 + 全圆冲击；升起后至 Break 前环带驻场轮询（连续带、每圈每目标一次、
    /// 跳躲高度过滤——旧版 trigger 驻场语义的轮询等价实现，零 collider）；
    /// Break = 全圆（无高度过滤）+ 爆炸特效 + 延迟自毁。
    /// 判定为受击方本地结算（目标权威端）；贴地 raycast 仅运行时（预演置于召唤物根高度）。
    /// </summary>
    public class MonsterStoneRingSummon : MonsterSummonBehaviour
    {
        private const float BREAK_VFX_TAIL = 2f;

        [Header("Rings")]
        [Tooltip("首环（内圈）")]
        [SerializeField]
        private MonsterStoneRingConfig _firstRing = new() { RiseTime = 1.7f, Radius = 20f };

        [Tooltip("次环（外圈）")]
        [SerializeField]
        private MonsterStoneRingConfig _secondRing = new() { RiseTime = 3.03f, Radius = 33f };

        [Header("References")]
        [SerializeField]
        [Tooltip("Break 爆炸特效（默认隐藏，Break 时刻激活重启）")]
        private Transform _breakVfx;

        [Header("Timing（秒，生成起算）")]
        [SerializeField]
        [Tooltip("Break 全圆爆炸时刻")]
        private float _breakTime = 4.67f;

        [SerializeField]
        [Tooltip("Break 后到自毁的等待时长")]
        private float _destroyDelay = 0.5f;

        [Header("Band（驻场环带公共参数）")]
        [SerializeField]
        [Tooltip("环带轮询间隔（秒）")]
        private float _bandPollInterval = 0.2f;

        [SerializeField]
        [Tooltip("跳躲高度阈值（环带判定的对称竖向带过滤，目标高度差超过该值视为跳起不命中；<=0 不过滤）")]
        private float _maxAttackHeight;

        [Header("Break（全圆爆炸，无高度过滤）")]
        [SerializeField]
        [Tooltip("Break 半径（米）")]
        private float _breakRadius = 45f;

        [SerializeField]
        [Tooltip("Break 伤害")]
        private int _breakDamage = 3000;

        [Header("Damage Common")]
        [SerializeField]
        [Tooltip("伤害结算层（受击方本地结算时对层内目标判定命中）")]
        private LayerMask _damageLayer;

        [SerializeField]
        [Tooltip("受击方向反作用力")]
        private float _hitForce = 5f;

        [Header("Ground Snap（逐石贴地，仅运行时）")]
        [SerializeField]
        [Tooltip("地面层（石头 raycast 找升起位置用）")]
        private LayerMask _groundLayer;

        [SerializeField]
        [Tooltip("raycast 起点相对石头当前 Y 向上偏移（米）")]
        private float _raycastStartHeight = 15f;

        [SerializeField]
        [Tooltip("raycast 向下最大距离（米）")]
        private float _raycastMaxDistance = 20f;

        [SerializeField]
        [Tooltip("石头刺入地面的深度（米，命中点 Y 下移该值）")]
        private float _pierceOffset = 0.1f;

        [SerializeField]
        [Tooltip("raycast 未命中时石头的兜底 localPosition.y（相对圈容器）")]
        private float _fallbackLocalY;

        private bool _firstRisen;
        private bool _secondRisen;
        private bool _breakDone;
        private float _nextFirstPollTime;
        private float _nextSecondPollTime;

        private readonly HashSet<EntityId> _firstBandTargets = new();
        private readonly HashSet<EntityId> _secondBandTargets = new();

        /// <summary> 预演石头原始 local Y 缓存（预演克隆每次重建，天然单次捕获） </summary>
        private readonly Dictionary<Transform, float> _previewStoneYs = new();

        public override float TimelineDuration => _breakTime + Mathf.Max(0.1f, _destroyDelay) + BREAK_VFX_TAIL;

        #region Runtime Timeline

        protected override void OnInitialize()
        {
            _firstRisen = false;
            _secondRisen = false;
            _breakDone = false;
            _nextFirstPollTime = 0f;
            _nextSecondPollTime = 0f;
            _firstBandTargets.Clear();
            _secondBandTargets.Clear();

            if (_breakVfx != null) _breakVfx.gameObject.SetActive(false);
        }

        protected override void OnTimelineTick(float elapsed)
        {
            if (!_firstRisen && _firstRing != null && elapsed >= _firstRing.RiseTime)
            {
                _firstRisen = true;
                RiseRing(_firstRing);
            }

            if (!_secondRisen && _secondRing != null && elapsed >= _secondRing.RiseTime)
            {
                _secondRisen = true;
                RiseRing(_secondRing);
            }

            // 驻场环带轮询：升起后 ~ Break 前（停伤后跳过判定）
            if (!DamageStopped && !_breakDone)
            {
                if (_firstRisen && elapsed >= _nextFirstPollTime)
                {
                    _nextFirstPollTime = elapsed + Mathf.Max(0.05f, _bandPollInterval);
                    PollBand(_firstRing, _firstBandTargets);
                }

                if (_secondRisen && elapsed >= _nextSecondPollTime)
                {
                    _nextSecondPollTime = elapsed + Mathf.Max(0.05f, _bandPollInterval);
                    PollBand(_secondRing, _secondBandTargets);
                }
            }

            if (!_breakDone && elapsed >= _breakTime)
            {
                DoBreak();
            }
        }

        /// <summary>
        /// 单环升起：逐石 raycast 贴地 + 全圆冲击（一次性）。
        /// </summary>
        private void RiseRing(MonsterStoneRingConfig ring)
        {
            ring.Ring.gameObject.SetActive(true);
            SnapRingToGround(ring.Ring);

            if (DamageStopped || ring.BlastRadius <= 0f || ring.BlastDamage <= 0) return;

            MonsterSkillDamage.SettleSphere(
                AttackerId,
                transform.position,
                ring.BlastRadius,
                _damageLayer,
                0f,
                ring.BlastDamage,
                _hitForce,
                new HashSet<EntityId>(),
                transform.forward
            );
        }

        /// <summary>
        /// 驻场环带单次轮询（每圈每目标一次；后进入者后续轮询命中）。
        /// </summary>
        private void PollBand(MonsterStoneRingConfig ring, HashSet<EntityId> bandTargets)
        {
            if (ring.Thickness <= 0f || ring.Damage <= 0) return;

            MonsterSkillDamage.SettleAnnulus(
                AttackerId,
                transform.position,
                Mathf.Max(0f, ring.Radius - ring.Thickness * 0.5f),
                ring.Radius + ring.Thickness * 0.5f,
                _damageLayer,
                _maxAttackHeight,
                ring.Damage,
                _hitForce,
                bandTargets,
                transform.forward
            );
        }

        private void DoBreak()
        {
            _breakDone = true;

            PlayVfx(_breakVfx != null ? _breakVfx.gameObject : null);

            if (!DamageStopped && _breakDamage > 0)
            {
                MonsterSkillDamage.SettleSphere(
                    AttackerId,
                    transform.position,
                    _breakRadius,
                    _damageLayer,
                    0f,
                    _breakDamage,
                    _hitForce,
                    new HashSet<EntityId>(),
                    transform.forward
                );
            }

            Destroy(gameObject, Mathf.Max(0.1f, _destroyDelay));
        }

        /// <summary>
        /// 逐石 raycast 贴地：世界命中点（下移刺入深度）转回圈容器 local Y，保留原 x/z 圆周位置；
        /// 未命中用兜底 local Y。
        /// </summary>
        private void SnapRingToGround(Transform ring)
        {
            if (ring == null) return;

            float castDistance = _raycastStartHeight + _raycastMaxDistance;

            foreach (Transform stone in ring)
            {
                Vector3 startWorld = stone.position + Vector3.up * _raycastStartHeight;

                var lp = stone.localPosition;
                if (Physics.Raycast(
                        startWorld,
                        Vector3.down,
                        out RaycastHit hit,
                        castDistance,
                        _groundLayer,
                        QueryTriggerInteraction.Ignore
                    ))
                {
                    Vector3 targetWorld = hit.point;
                    targetWorld.y -= _pierceOffset;
                    lp.y = ring.InverseTransformPoint(targetWorld).y;
                }
                else
                {
                    lp.y = _fallbackLocalY;
                }

                stone.localPosition = lp;
            }
        }

        #endregion

        #region Preview

        public override void SampleTimeline(float localTime)
        {
            SampleRing(_firstRing, localTime);
            SampleRing(_secondRing, localTime);

            if (_breakVfx == null) return;

            bool broken = localTime >= _breakTime;
            if (_breakVfx.gameObject.activeSelf != broken)
            {
                _breakVfx.gameObject.SetActive(broken);
            }

            if (broken)
            {
                SimulateVfx(_breakVfx.gameObject, localTime - _breakTime);
            }
        }

        /// <summary>
        /// 单环预演采样：升起前置于埋藏原始位，升起后置于召唤物根高度（预演禁 raycast）。
        /// </summary>
        private void SampleRing(MonsterStoneRingConfig ring, float localTime)
        {
            if (ring?.Ring == null) return;

            bool risen = localTime >= ring.RiseTime;
            if (ring.Ring.gameObject.activeSelf != risen)
            {
                ring.Ring.gameObject.SetActive(risen);
            }

            float riseLocalY = -ring.Ring.localPosition.y;

            foreach (Transform stone in ring.Ring)
            {
                if (!_previewStoneYs.TryGetValue(stone, out float buriedY))
                {
                    buriedY = stone.localPosition.y;
                    _previewStoneYs[stone] = buriedY;
                }

                var lp = stone.localPosition;
                lp.y = risen ? riseLocalY : buriedY;
                stone.localPosition = lp;
            }
        }

        public override void GetDamageWindows(float localTime, List<MonsterSummonDamageWindow> results)
        {
            AppendBandWindow(results, localTime, _firstRing, "首环带");
            AppendBandWindow(results, localTime, _secondRing, "次环带");
            AppendInstantDisc(results, localTime, _firstRing, "首环冲击");
            AppendInstantDisc(results, localTime, _secondRing, "次环冲击");
            AppendInstantDisc(results, localTime, _breakTime, _breakRadius, _breakDamage, "Break 全圆");
        }

        /// <summary> 驻场环带窗口：升起前 Upcoming，升起 ~ Break 间 Active（结束后不返回） </summary>
        private void AppendBandWindow(
            List<MonsterSummonDamageWindow> results,
            float localTime,
            MonsterStoneRingConfig ring,
            string label)
        {
            if (ring == null || ring.Thickness <= 0f || ring.Damage <= 0) return;
            if (localTime >= _breakTime) return;

            float innerRadius = Mathf.Max(0f, ring.Radius - ring.Thickness * 0.5f);
            float outerRadius = ring.Radius + ring.Thickness * 0.5f;

            results.Add(
                new MonsterSummonDamageWindow
                {
                    Shape = MonsterSummonDamageShape.Annulus,
                    Phase = localTime < ring.RiseTime
                        ? MonsterSummonWindowPhase.Upcoming
                        : MonsterSummonWindowPhase.Active,
                    InnerRadius = innerRadius,
                    OuterRadius = outerRadius,
                    MaxAttackHeight = _maxAttackHeight,
                    Damage = ring.Damage,
                    Label = $"{label} {ring.RiseTime:0.00}s 升起 · 带 {innerRadius:0.0}–{outerRadius:0.0}m",
                }
            );
        }

        /// <summary> 环冲击窗口（瞬时全圆） </summary>
        private void AppendInstantDisc(
            List<MonsterSummonDamageWindow> results,
            float localTime,
            MonsterStoneRingConfig ring,
            string label)
        {
            if (ring == null) return;

            AppendInstantDisc(results, localTime, ring.RiseTime, ring.BlastRadius, ring.BlastDamage, label);
        }

        /// <summary> 瞬时全圆窗口：命中时刻 ±INSTANT_FLASH 邻域内 Active，此前 Upcoming，此后不返回 </summary>
        private void AppendInstantDisc(
            List<MonsterSummonDamageWindow> results,
            float localTime,
            float settleTime,
            float radius,
            int damage,
            string label)
        {
            const float INSTANT_FLASH = 0.25f;

            if (radius <= 0f || damage <= 0) return;
            if (localTime > settleTime + INSTANT_FLASH) return;

            results.Add(
                new MonsterSummonDamageWindow
                {
                    Shape = MonsterSummonDamageShape.Disc,
                    Phase = localTime >= settleTime - INSTANT_FLASH
                        ? MonsterSummonWindowPhase.Active
                        : MonsterSummonWindowPhase.Upcoming,
                    InnerRadius = 0f,
                    OuterRadius = radius,
                    MaxAttackHeight = 0f,
                    Damage = damage,
                    Label = $"{label} {settleTime:0.00}s · R{radius:0}m · {damage}",
                }
            );
        }

        #endregion
    }
}
