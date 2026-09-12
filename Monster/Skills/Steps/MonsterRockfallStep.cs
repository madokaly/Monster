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
    public class MonsterRockfallStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("判定原点 / 半圆环朝向基准（怪物自身 Transform）")]
        public Transform SelfTransform;

        [Header("Inner AOE（内圈持续伤害）")]
        [Tooltip("内圈半径（米，以 SelfTransform 为圆心）")]
        public float InnerRadius = 20f;

        [Tooltip("内圈起始延迟（秒，步骤起点起算；前摇期不伤害）")]
        public float InnerStartDelay = 1.33f;

        [Tooltip("内圈持续时长（秒）")]
        public float InnerDuration = 5f;

        [Tooltip("内圈 tick 间隔（秒）")]
        public float InnerTickInterval = 0.5f;

        [Tooltip("内圈单 tick 伤害")]
        public int InnerDamage = 500;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 0f;

        [Header("Rocks（前半圆环随机陨石，锚点定长 3）")]
        [Tooltip("陨石数量（1~3）")]
        [Range(1, 3)]
        public int RockCount = 3;

        [Tooltip("半圆环内界半径（米）")]
        public float InnerRingRadius = 20f;

        [Tooltip("半圆环外界半径（米）")]
        public float OuterRingRadius = 50f;

        [Tooltip("陨石间最小水平间距（米，0 = 允许重叠）")]
        public float MinRockSpacing = 30f;

        [Tooltip("每石从步骤起到开始下落的延迟（秒，按石索引取用；警报圈自步骤开始即显示）")]
        public float[] RockFallDelays = { 4.33f, 5.33f, 6.33f };

        [Tooltip("陨石起始高度（米，相对落点）")]
        public float RockFallHeight = 60f;

        [Tooltip("陨石初始下落速度（米/秒）")]
        public float RockFallInitialSpeed = 200f;

        [Tooltip("陨石下落加速度（米/秒²）")]
        public float RockFallGravity = 9.8f;

        [Tooltip("陨石下落速度上限（米/秒）")]
        public float RockMaxFallSpeed = 220f;

        [Tooltip("单石落地伤害")]
        public int RockDamage = 4000;

        [Tooltip("落地判定半径（米，以落点为圆心）")]
        public float RockHitRadius = 8f;

        [Tooltip("落地驻场时长（秒，落地 → 自然碎裂；需覆盖到遁地出土时刻）")]
        public float RockLingerDuration = 30f;

        [Header("Layers")]
        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("地面层（落点贴合 raycast 用）")]
        public LayerMask GroundLayer;

        [Header("Effects")]
        [Tooltip("陨石表现 prefab（挂 MonsterRockfallRock，各端本地实例化，驻场自毁不随步骤回收）")]
        public GameObject RockPrefab;

        public override float Duration => Mathf.Max(
            InnerStartDelay + Mathf.Max(0f, InnerDuration),
            MonsterRockfallTiming.GetLastRockLandTime(this) + 0.5f
        );
    }

    /// <summary>
    /// 落石步骤：内圈持续 AOE + 前半圆环随机陨石（警告 → 下落 → 落地结算 → 驻场 → 碎裂）。
    /// 权威端选点一次写入 Model.RockfallState（落点跨端同步，状态事实）；
    /// 各端轮询 Seq 在锚点本地生成陨石表现体（驻场自生命周期，步骤退出不回收）；
    /// 全部伤害各端按共享时序（MonsterRockfallTiming）受击方本地结算——
    /// 单石落地以「本次施放的 Seq 到达本端」为结算门槛（防上一轮旧锚点）。
    /// </summary>
    public class MonsterRockfallStep : MonsterSkillStep
    {
        private const int MAX_ROCK_ANCHORS = 3;
        private const int ANCHOR_PICK_ATTEMPTS_PER_ROCK = 16;

        private readonly MonsterRockfallStepConfig _rockfallConfig;

        /// <summary> 本端：内圈 AOE 下次 tick 时刻（步骤时钟） </summary>
        private float _nextInnerTickTime;

        /// <summary> 本端：已结算落地的陨石游标 </summary>
        private int _nextRockSettleIndex;

        /// <summary> 本端：Enter 时刻的落石事实 Seq（本次施放的期望 Seq = 此值 + 1） </summary>
        private int _enterRockSeq;

        /// <summary> 本端：已生成表现体的落石 Seq </summary>
        private int _spawnedRockSeq;

        /// <summary> 内圈 tick 当次去重（每 tick 独立清空） </summary>
        private readonly HashSet<EntityId> _innerTickTargets = new();

        public MonsterRockfallStep(MonsterModel model, MonsterRockfallStepConfig config)
            : base(model, config)
        {
            _rockfallConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_rockfallConfig is null) return;

            // 本端先记当前 Seq（本次施放的期望 Seq = +1；单石落地结算以此为门槛，防上一轮旧锚点误结算）
            _enterRockSeq = _model.RockfallState.Seq;
            _nextInnerTickTime = 0f;
            _nextRockSettleIndex = 0;
            _innerTickTargets.Clear();

            // 权威端：随机选点 + 写跨端落石事实（代理端经网络同步后自行生成表现）
            if (HasStateAuthority)
            {
                PickAndBroadcastAnchors();
            }
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_rockfallConfig is null) return;

            float stepElapsed = elapsed - _config.StartOffset;

            // 各端本地：落石事实更新（代理端网络迟到容忍）→ 生成陨石表现体
            TrySpawnRockVisuals(stepElapsed);

            // 内圈 AOE 周期 tick（各端受击方本地结算）
            if (MonsterRockfallTiming.IsInnerAoeActive(_rockfallConfig, stepElapsed)
                && stepElapsed >= _nextInnerTickTime)
            {
                _nextInnerTickTime = stepElapsed + Mathf.Max(0.05f, _rockfallConfig.InnerTickInterval);
                SettleInnerAoeTick();
            }

            // 陨石落地时刻点式结算（各端受击方本地结算）：落石事实到达本端（Seq 越过进入值）才结算，
            // 游标停住等事实，到达后按时间追进（同 tick 可跨多石）
            while (_model.RockfallState.Seq > _enterRockSeq
                   && _nextRockSettleIndex < _rockfallConfig.RockCount
                   && stepElapsed >= MonsterRockfallTiming.GetRockLandTime(_rockfallConfig, _nextRockSettleIndex))
            {
                SettleRockLanding(_nextRockSettleIndex);
                _nextRockSettleIndex++;
            }
        }

        protected override void OnStepExit()
        {
            // 陨石表现体自管生命周期（驻场延续到遁地阶段），此处不回收
            _innerTickTargets.Clear();
        }

        #region Private Methods

        /// <summary>
        /// 权威端选点：SelfTransform 朝向的前半圆环（内界 ~ 外界）随机取 RockCount 个落点
        /// （最小间距去重 + raycast 贴地），写入 Model 落石事实（Seq 递增）。
        /// </summary>
        private void PickAndBroadcastAnchors()
        {
            var self = _rockfallConfig.SelfTransform;
            if (self == null) return;

            var anchors = new Vector3[MAX_ROCK_ANCHORS];

            Vector3 forward = Vector3.ProjectOnPlane(self.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);

            float groundY = GetGroundY(self.position);

            int placed = 0;
            int attempts = _rockfallConfig.RockCount * ANCHOR_PICK_ATTEMPTS_PER_ROCK;
            while (placed < _rockfallConfig.RockCount && attempts-- > 0)
            {
                float halfCircleAngle = UnityEngine.Random.Range(-Mathf.PI / 2f, Mathf.PI / 2f);
                float radius = UnityEngine.Random.Range(_rockfallConfig.InnerRingRadius, _rockfallConfig.OuterRingRadius);
                Vector3 offset = (forward * Mathf.Cos(halfCircleAngle) + right * Mathf.Sin(halfCircleAngle)) * radius;

                var anchor = new Vector3(self.position.x + offset.x, groundY, self.position.z + offset.z);

                if (_rockfallConfig.MinRockSpacing > 0f && !IsFarEnough(anchor, anchors, placed)) continue;

                anchors[placed++] = anchor;
            }

            if (placed < _rockfallConfig.RockCount)
            {
                Logging.Warning($"[MonsterRockfallStep] 落点随机未凑齐 {placed}/{_rockfallConfig.RockCount}（最小间距约束过紧？）");
            }

            // 驻场失效时刻（近似取末石落地 + 驻场时长，早落陨石略延后失效）
            float aliveSeconds = MonsterRockfallTiming.GetLastRockLandTime(_rockfallConfig)
                                 + Mathf.Max(0f, _rockfallConfig.RockLingerDuration);
            float tickDelta = Mathf.Max(0.001f, (float)_model.Runner.DeltaTime);

            _model.SetRockfallState(new MonsterRockfallState
            {
                Seq = _model.RockfallState.Seq + 1,
                Anchor0 = anchors[0],
                Anchor1 = anchors[1],
                Anchor2 = anchors[2],
                ExpireTick = _model.Runner.Tick + Mathf.CeilToInt(aliveSeconds / tickDelta),
            });
        }

        /// <summary>
        /// 从怪物位置向下 raycast 找落点基准 Y；未命中回落怪物自身 Y。
        /// </summary>
        private float GetGroundY(Vector3 fromPos)
        {
            if (_rockfallConfig.GroundLayer == 0) return fromPos.y;

            Vector3 rayStart = fromPos + Vector3.up * 5f;
            if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 100f, _rockfallConfig.GroundLayer, QueryTriggerInteraction.Ignore))
            {
                return hit.point.y;
            }
            return fromPos.y;
        }

        private bool IsFarEnough(Vector3 candidate, Vector3[] existing, int count)
        {
            float minSqr = _rockfallConfig.MinRockSpacing * _rockfallConfig.MinRockSpacing;
            for (int i = 0; i < count; i++)
            {
                if ((existing[i] - candidate).sqrMagnitude < minSqr) return false;
            }
            return true;
        }

        /// <summary>
        /// 各端本地：Seq 变化 → 在锚点生成陨石表现体，并对齐本端步骤时间轴。
        /// </summary>
        private void TrySpawnRockVisuals(float stepElapsed)
        {
            var state = _model.RockfallState;
            if (state.Seq <= 0 || state.Seq == _spawnedRockSeq) return;

            _spawnedRockSeq = state.Seq;

            if (_rockfallConfig.RockPrefab == null)
            {
                Logging.Error("[MonsterRockfallStep] TrySpawnRockVisuals: RockPrefab 为 null，请检查 Inspector 引用。");
                return;
            }

            for (int i = 0; i < _rockfallConfig.RockCount; i++)
            {
                var rockObj = UnityEngine.Object.Instantiate(_rockfallConfig.RockPrefab, GetAnchor(state, i), Quaternion.identity);
                var rock = rockObj.GetComponent<MonsterRockfallRock>();
                if (rock == null)
                {
                    Logging.Error("[MonsterRockfallStep] RockPrefab 缺 MonsterRockfallRock 组件，请检查 prefab 装配。");
                    UnityEngine.Object.Destroy(rockObj);
                    continue;
                }

                rock.Initialize(_model, _rockfallConfig, i, stepElapsed);
            }
        }

        /// <summary>
        /// 内圈单 tick 结算（各端受击方本地结算；每 tick 独立去重，复用基类共享管线）。
        /// </summary>
        private void SettleInnerAoeTick()
        {
            var self = _rockfallConfig.SelfTransform;
            if (self == null) return;

            _innerTickTargets.Clear();

            SettleSphereDamage(
                self.position,
                _rockfallConfig.InnerRadius,
                _rockfallConfig.DamageLayer,
                0f,
                _rockfallConfig.InnerDamage,
                _rockfallConfig.HitForce,
                _innerTickTargets,
                self.forward
            );
        }

        /// <summary>
        /// 单石落地结算（各端受击方本地结算；落点为圆心点式，每石每目标一次）。
        /// </summary>
        private void SettleRockLanding(int rockIndex)
        {
            var self = _rockfallConfig.SelfTransform;
            if (self == null) return;

            var hitTargets = new HashSet<EntityId>();

            SettleSphereDamage(
                GetAnchor(_model.RockfallState, rockIndex),
                _rockfallConfig.RockHitRadius,
                _rockfallConfig.DamageLayer,
                0f,
                _rockfallConfig.RockDamage,
                _rockfallConfig.HitForce,
                hitTargets,
                self.forward
            );
        }

        private static Vector3 GetAnchor(MonsterRockfallState state, int index)
        {
            return index switch
            {
                0 => state.Anchor0,
                1 => state.Anchor1,
                _ => state.Anchor2,
            };
        }

        #endregion
    }
}
