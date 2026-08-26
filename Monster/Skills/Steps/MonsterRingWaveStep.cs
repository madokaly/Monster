using System;
using System.Collections.Generic;
using Framework;
using Framework.Core;
using Game.Components;
using Game.DTOs;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterRingWaveStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("扩散圆心（怪物自身 Transform）")]
        public Transform SelfTransform;

        [Header("Waves")]
        [Tooltip("脉冲波数")]
        public int PulseCount = 3;

        [Tooltip("波间隔（秒；上一波扩散结束到下一波开始之间的间隔）")]
        public float PulseInterval = 0.8f;

        [Tooltip("放电蓄力时长（秒，步骤起点起算，第一波扩散前）")]
        public float ChargeTime = 1f;

        [Tooltip("单波扩散时长（秒，半径 0 → MaxRadius 的推进时间）")]
        public float ExpandDuration = 1f;

        [Tooltip("单波扩散最大半径（米）")]
        public float MaxRadius = 48f;

        [Tooltip("环带厚度（米；扩散前 DiscPhaseRatio 阶段内为实心圆，之后为环带）")]
        public float Thickness = 2f;

        [Tooltip("实心圆阶段占比（扩散前 N% 时间内内半径 = 0，环心也命中；0 = 全程环带，1 = 全程整圆）")]
        [Range(0f, 1f)]
        public float DiscPhaseRatio = 0.3f;

        [Header("Damage")]
        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("单波命中伤害（同一目标每波只命中 1 次）")]
        public int Damage = 1;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 5f;

        [Tooltip("跳躲高度阈值（目标离地高度超过该值视为跳起，不命中；<=0 不过滤）")]
        public float MaxAttackHeight = 0.3f;

        [Header("Ring Effect")]
        [Tooltip("环扩散特效 prefab（纯表现，各端本地每波实例化）")]
        public GameObject RingEffectPrefab;

        [Tooltip("环特效挂点（空则挂扩散圆心）")]
        public Transform EffectAttachPoint;

        [Tooltip("环特效相对挂点的本地位置偏移")]
        public Vector3 EffectOffset = Vector3.zero;

        [Tooltip("环特效相对挂点的本地旋转（欧拉角）")]
        public Vector3 EffectRotation = Vector3.zero;

        [Tooltip("环特效存活时长（秒，<=0 兜底 3s）")]
        public float EffectLifetime = 3f;

        public override float Duration =>
            ChargeTime + PulseCount * ExpandDuration + Mathf.Max(0, PulseCount - 1) * PulseInterval;
    }

    /// <summary>
    /// 扩散环波步骤（§16.4）：蓄力 → N 波向外扩散的环形范围伤害（玩家跳起可躲）。
    /// 权威端逐 tick 推进各波扩散（OverlapSphere 外半径 + 内半径距离过滤，
    /// DiscPhaseRatio 阶段内半径 = 0 为实心圆），同一目标每波只命中 1 次；
    /// 各端本地每波播环特效（本端时钟驱动）。
    /// </summary>
    public class MonsterRingWaveStep : MonsterSkillStep
    {
        private readonly MonsterRingWaveStepConfig _ringConfig;

        /// <summary> 环带扫描碰撞体缓冲 </summary>
        private readonly Collider[] _overlapBuffer = new Collider[64];

        /// <summary> 当前波已命中目标（每波重置，同目标每波 1 次） </summary>
        private readonly HashSet<EntityId> _pulseHitTargets = new();

        /// <summary> 本端：下一个待结算的波索引（权威端结算 / 各端特效共用本端时钟推进） </summary>
        private int _nextSettlePulseIndex;

        /// <summary> 本端：下一个待播特效的波索引 </summary>
        private int _nextEffectPulseIndex;

        /// <summary> 本端：当前正在扩散中的波索引（-1 = 无） </summary>
        private int _expandingPulseIndex = -1;

        public MonsterRingWaveStep(MonsterModel model, MonsterRingWaveStepConfig config)
            : base(model, config)
        {
            _ringConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_ringConfig is null) return;

            _nextSettlePulseIndex = 0;
            _nextEffectPulseIndex = 0;
            _expandingPulseIndex = -1;
            _pulseHitTargets.Clear();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_ringConfig is null) return;

            float stepElapsed = elapsed - _config.StartOffset;

            AdvancePulses(stepElapsed);
        }

        protected override void OnStepExit()
        {
            _pulseHitTargets.Clear();
            _expandingPulseIndex = -1;
        }

        protected override void OnStepAuthorityChanged()
        {
            _pulseHitTargets.Clear();
            _expandingPulseIndex = -1;
        }

        #region Private Methods

        /// <summary>
        /// 推进波状态：新波起点 → 开始扩散（重置去重 / 播特效）；扩散中逐 tick 环带扫描；
        /// 扩散结束 → 收尾。同 tick 可跨多波（追结过期波）。
        /// </summary>
        private void AdvancePulses(float stepElapsed)
        {
            float pulseStart = GetPulseStart(_nextSettlePulseIndex);
            while (_nextSettlePulseIndex < _ringConfig.PulseCount && stepElapsed >= pulseStart)
            {
                BeginPulse(_nextSettlePulseIndex);
                _nextSettlePulseIndex++;
                pulseStart = GetPulseStart(_nextSettlePulseIndex);
            }

            if (_expandingPulseIndex >= 0)
            {
                float start = GetPulseStart(_expandingPulseIndex);
                float t = Mathf.Clamp01((stepElapsed - start) / _ringConfig.ExpandDuration);
                if (t >= 1f)
                {
                    _expandingPulseIndex = -1;
                }
                else if (HasStateAuthority)
                {
                    SettlePulse(_expandingPulseIndex, t);
                }
            }

            // 各端本地：追播特效（与结算同本端时钟，权威端结算与特效同刻）
            float effectPulseStart = GetPulseStart(_nextEffectPulseIndex);
            while (_nextEffectPulseIndex < _ringConfig.PulseCount && stepElapsed >= effectPulseStart)
            {
                PlayRingEffect();
                _nextEffectPulseIndex++;
                effectPulseStart = GetPulseStart(_nextEffectPulseIndex);
            }
        }

        /// <summary>
        /// 波起点 = 蓄力 + 已完成的波周期（每波 = 扩散 + 间隔；波间不重叠）
        /// </summary>
        private float GetPulseStart(int pulseIndex)
        {
            return _ringConfig.ChargeTime + pulseIndex * (_ringConfig.ExpandDuration + _ringConfig.PulseInterval);
        }

        /// <summary>
        /// 波开始：重置本波去重集、标记扩散中。
        /// </summary>
        private void BeginPulse(int pulseIndex)
        {
            _pulseHitTargets.Clear();
            _expandingPulseIndex = pulseIndex;
        }

        /// <summary>
        /// 扩散中逐 tick 环带扫描：外半径 = MaxRadius × t；内半径 = disc 阶段 0 / 环带阶段外半径 - Thickness；
        /// 高度过滤（跳躲）+ 每波每目标一次 → ApplyDamage + ApplyHit。
        /// </summary>
        private void SettlePulse(int pulseIndex, float t)
        {
            var self = _ringConfig.SelfTransform;
            if (self == null) return;

            float outerRadius = _ringConfig.MaxRadius * t;
            if (outerRadius <= 0f) return;

            float innerRadius = t <= _ringConfig.DiscPhaseRatio
                ? 0f
                : Mathf.Max(0f, outerRadius - _ringConfig.Thickness);

            Vector3 center = self.position;

            int count = Physics.OverlapSphereNonAlloc(
                center,
                outerRadius,
                _overlapBuffer,
                _ringConfig.DamageLayer,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < count; i++)
            {
                var col = _overlapBuffer[i];
                if (col == null) continue;

                // 环带几何：实心圆阶段内半径 0；环带阶段仅命中环带内
                Vector3 toTarget = col.transform.position - center;
                toTarget.y = 0f;
                float dist = toTarget.magnitude;
                if (dist < innerRadius) continue;

                // 跳躲：目标离地高度超过阈值不命中
                if (_ringConfig.MaxAttackHeight > 0f
                    && Mathf.Abs(col.transform.position.y - center.y) > _ringConfig.MaxAttackHeight)
                {
                    continue;
                }

                // 身份解析：即用即弃，只提取 EntityId（§1.6）
                var tag = col.GetComponentInParent<EntityTag>();
                if (tag == null) continue;

                EntityId targetId = tag.Id;
                if (!targetId.IsValid) continue;
                if (!_pulseHitTargets.Add(targetId)) continue;

                Vector3 hitPoint = col.transform.position;
                Vector3 hitDirection = hitPoint - center;
                hitDirection.y = 0f;
                hitDirection = hitDirection.sqrMagnitude > 0.001f ? hitDirection.normalized : self.forward;

                var damageData = new DamageData { Damage = _ringConfig.Damage, AttackerId = _model.Id, };

                var hitData = new HitData
                {
                    HitPoint = hitPoint, HitDirection = hitDirection, Force = _ringConfig.HitForce,
                };

                Msger.Send(MsgID.ApplyDamage, targetId, damageData);
                Msger.Send(MsgID.ApplyHit, targetId, hitData);
            }
        }

        /// <summary>
        /// 各端本地播环扩散特效：挂点实例化 + 本地偏移 / 旋转 + 到时自毁。
        /// </summary>
        private void PlayRingEffect()
        {
            if (_ringConfig.RingEffectPrefab == null)
            {
                Logging.Error("[MonsterRingWaveStep] PlayRingEffect: RingEffectPrefab 为 null，请检查 Inspector 引用。");
                return;
            }

            var attach = _ringConfig.EffectAttachPoint != null
                ? _ringConfig.EffectAttachPoint
                : _ringConfig.SelfTransform;
            if (attach == null) return;

            var effectObj = UnityEngine.Object.Instantiate(
                _ringConfig.RingEffectPrefab,
                attach.position,
                attach.rotation,
                attach
            );
            effectObj.transform.localPosition = _ringConfig.EffectOffset;
            effectObj.transform.localEulerAngles = _ringConfig.EffectRotation;

            float lifetime = _ringConfig.EffectLifetime > 0f ? _ringConfig.EffectLifetime : 3f;
            UnityEngine.Object.Destroy(effectObj, Mathf.Max(0.1f, lifetime));
        }

        #endregion
    }
}
