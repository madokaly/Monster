using System.Collections.Generic;
using Framework;
using Game.Entities;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 环波召唤物（boss0001 电流激荡）：蓄力 → N 波向外扩散的环形伤害（跳起可躲）。
    /// 判定为受击方本地结算 + 扫掠体环带——每次结算覆盖 [上次内径, 本次外径]，
    /// 任何轮询步长下不漏目标（内径推进与去重集吸收重叠区重复命中）；
    /// 圆心冻结出生点（施放期怪物被 AI 门控静止，语义等价跟随）。
    /// 时序公式内聚本类，预演经 SampleTimeline / GetDamageWindows 共用（§18.6.2 单一计算入口）。
    /// </summary>
    public class MonsterRingWaveSummon : MonsterSummonBehaviour
    {
        [Header("Timing（秒，生成起算）")]
        [SerializeField]
        [Tooltip("放电蓄力时长（第一波扩散前）")]
        private float _chargeTime = 1f;

        [SerializeField]
        [Tooltip("脉冲波数")]
        private int _pulseCount = 3;

        [SerializeField]
        [Tooltip("波间隔（上一波扩散结束到下一波开始）")]
        private float _pulseInterval = 0.8f;

        [SerializeField]
        [Tooltip("单波扩散时长（半径 0 → MaxRadius 的推进时间）")]
        private float _expandDuration = 1f;

        [Header("Geometry")]
        [SerializeField]
        [Tooltip("单波扩散最大半径（米）")]
        private float _maxRadius = 48f;

        [SerializeField]
        [Tooltip("环带厚度（米；扩散前 DiscPhaseRatio 阶段为实心圆，之后为环带）")]
        private float _thickness = 2f;

        [SerializeField]
        [Tooltip("实心圆阶段占比（扩散前该比例时间内内半径 = 0；0 = 全程环带，1 = 全程整圆）")]
        [Range(0f, 1f)]
        private float _discPhaseRatio = 0.3f;

        [SerializeField]
        [Tooltip("跳躲高度阈值（目标高度差超过该值视为跳起不命中；<=0 不过滤）")]
        private float _maxAttackHeight = 0.3f;

        [Header("Damage")]
        [SerializeField]
        [Tooltip("伤害结算层（受击方本地结算时对层内目标判定命中）")]
        private LayerMask _damageLayer;

        [SerializeField]
        [Tooltip("单波命中伤害（同一目标每波只命中 1 次）")]
        private int _damage = 1;

        [SerializeField]
        [Tooltip("受击方向反作用力")]
        private float _hitForce = 5f;

        [Header("Effect")]
        [SerializeField]
        [Tooltip("环扩散特效 prefab（每波实例化于召唤物出生位，圆心冻结）")]
        private GameObject _ringEffectPrefab;

        [SerializeField]
        [Tooltip("环特效相对召唤物的本地位置偏移")]
        private Vector3 _effectOffset = Vector3.zero;

        [SerializeField]
        [Tooltip("环特效相对召唤物的本地旋转（欧拉角）")]
        private Vector3 _effectRotation = Vector3.zero;

        [SerializeField]
        [Tooltip("环特效存活时长（秒，<=0 兜底 3s）")]
        private float _effectLifetime = 3f;

        /// <summary> 当前扩散中的波索引（-1 = 蓄力期 / 波间空档 / 已完成） </summary>
        private int _currentPulse = -1;

        /// <summary> 当前波上次扫掠的内 / 外半径（扫掠体推进基准） </summary>
        private float _prevInnerRadius;
        private float _prevOuterRadius;

        private readonly HashSet<EntityId> _pulseTargets = new();

        /// <summary> 预演波特效实例（挂克隆下随其清理） </summary>
        private readonly List<GameObject> _previewEffects = new();

        private float EffectLifetime => _effectLifetime > 0f ? _effectLifetime : 3f;

        public override float TimelineDuration =>
            _chargeTime
            + _pulseCount * _expandDuration
            + Mathf.Max(0, _pulseCount - 1) * _pulseInterval
            + EffectLifetime;

        #region Runtime Timeline

        protected override void OnInitialize()
        {
            _currentPulse = -1;
            _prevInnerRadius = 0f;
            _prevOuterRadius = 0f;
            _pulseTargets.Clear();
        }

        protected override void OnTimelineTick(float elapsed)
        {
            TryEvaluateActivePulse(elapsed, out int pulseIndex, out float progress);

            if (pulseIndex != _currentPulse)
            {
                // 波切换：前一波扫掠收尾到满径（帧间隙兜底），再重置新波扫掠基准
                if (_currentPulse >= 0 && !DamageStopped)
                {
                    SweepPulse(1f);
                }

                _currentPulse = pulseIndex;
                _prevInnerRadius = 0f;
                _prevOuterRadius = 0f;
                _pulseTargets.Clear();

                if (pulseIndex >= 0)
                {
                    SpawnPulseEffect();
                }
            }
            else if (pulseIndex >= 0 && !DamageStopped)
            {
                SweepPulse(progress);
            }

            if (elapsed >= TimelineDuration)
            {
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// 单次扫掠结算：覆盖 [上次内径, 本次外径]（含 disc 阶段内径 0 起的全覆盖；
        /// 同波每目标一次，去重集吸收重叠区重复命中）。
        /// </summary>
        private void SweepPulse(float progress)
        {
            EvaluateRadii(progress, out float outerRadius, out float innerRadius);

            if (outerRadius > _prevOuterRadius + 0.001f)
            {
                MonsterSkillDamage.SettleAnnulus(
                    AttackerId,
                    transform.position,
                    _prevInnerRadius,
                    outerRadius,
                    _damageLayer,
                    _maxAttackHeight,
                    _damage,
                    _hitForce,
                    _pulseTargets,
                    transform.forward
                );
            }

            _prevInnerRadius = innerRadius;
            _prevOuterRadius = outerRadius;
        }

        private void EvaluateRadii(float progress, out float outerRadius, out float innerRadius)
        {
            float t = Mathf.Clamp01(progress);
            outerRadius = _maxRadius * t;
            innerRadius = t <= _discPhaseRatio
                ? 0f
                : Mathf.Max(0f, outerRadius - _thickness);
        }

        /// <summary>
        /// 波起点 = 蓄力 + 已完成的波周期（每波 = 扩散 + 间隔；波间不重叠）。
        /// </summary>
        private float GetPulseStart(int pulseIndex)
        {
            return _chargeTime + pulseIndex * (_expandDuration + _pulseInterval);
        }

        private void TryEvaluateActivePulse(float elapsed, out int pulseIndex, out float progress)
        {
            pulseIndex = -1;
            progress = 0f;

            if (_pulseCount <= 0 || _expandDuration <= 0f) return;
            if (elapsed < _chargeTime) return;

            float pulsePeriod = _expandDuration + _pulseInterval;
            if (pulsePeriod <= 0f) return;

            int candidate = Mathf.FloorToInt((elapsed - _chargeTime) / pulsePeriod);
            if (candidate < 0 || candidate >= _pulseCount) return;

            float localElapsed = elapsed - GetPulseStart(candidate);
            if (localElapsed < 0f || localElapsed >= _expandDuration) return;

            pulseIndex = candidate;
            progress = localElapsed / _expandDuration;
        }

        private void SpawnPulseEffect()
        {
            if (_ringEffectPrefab == null) return;

            var effect = Instantiate(
                _ringEffectPrefab,
                transform.position + transform.rotation * _effectOffset,
                transform.rotation * Quaternion.Euler(_effectRotation)
            );
            Destroy(effect, Mathf.Max(0.1f, EffectLifetime));
        }

        #endregion

        #region Preview

        public override void SampleTimeline(float localTime)
        {
            if (_ringEffectPrefab == null) return;

            // 每次采样重建（表现 = 时间纯函数）：先清上一采样实例，再按波起点年龄实例化
            for (int i = _previewEffects.Count - 1; i >= 0; i--)
            {
                if (_previewEffects[i] != null) Destroy(_previewEffects[i]);
            }
            _previewEffects.Clear();

            for (int i = 0; i < _pulseCount; i++)
            {
                float age = localTime - GetPulseStart(i);
                if (age < 0f || age > EffectLifetime) continue;

                var effect = Instantiate(_ringEffectPrefab, transform);
                effect.transform.localPosition = _effectOffset;
                effect.transform.localRotation = Quaternion.Euler(_effectRotation);
                effect.transform.localScale = _ringEffectPrefab.transform.localScale;
                _previewEffects.Add(effect);

                SimulateVfx(effect, age);
            }
        }

        public override void GetDamageWindows(float localTime, List<MonsterSummonDamageWindow> results)
        {
            for (int i = 0; i < _pulseCount; i++)
            {
                float pulseStart = GetPulseStart(i);
                float pulseEnd = pulseStart + _expandDuration;

                if (localTime >= pulseStart && localTime < pulseEnd)
                {
                    EvaluateRadii((localTime - pulseStart) / _expandDuration, out float outer, out float inner);
                    results.Add(new MonsterSummonDamageWindow
                    {
                        Shape = MonsterSummonDamageShape.Annulus,
                        Phase = MonsterSummonWindowPhase.Active,
                        InnerRadius = inner,
                        OuterRadius = outer,
                        MaxAttackHeight = _maxAttackHeight,
                        Damage = _damage,
                        Label = $"伤害波 {i + 1}/{_pulseCount} · {inner:0.0}–{outer:0.0}m · {_damage}",
                    });
                }
                else if (localTime < pulseStart)
                {
                    results.Add(new MonsterSummonDamageWindow
                    {
                        Shape = MonsterSummonDamageShape.Annulus,
                        Phase = MonsterSummonWindowPhase.Upcoming,
                        InnerRadius = 0f,
                        OuterRadius = Mathf.Max(0.5f, _thickness),
                        MaxAttackHeight = _maxAttackHeight,
                        Damage = _damage,
                        Label = $"待发波 {i + 1}/{_pulseCount} · {pulseStart - localTime:0.00}s 后",
                    });
                }
            }
        }

        #endregion
    }
}
