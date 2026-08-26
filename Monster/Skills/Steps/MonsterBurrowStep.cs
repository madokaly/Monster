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
    public class MonsterBurrowStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("地下追踪期间直接驱动的本体 Transform（限速位移，NetworkTransform 同步）")]
        public Transform Body;

        [Tooltip("判定原点 / 音效挂点（怪物自身 Transform）")]
        public Transform SelfTransform;

        [Tooltip("下潜期间隐藏的身体部件（出土恢复；各端本地显隐）")]
        public Transform[] BodyPartsToHide;

        [Header("Dive")]
        [Tooltip("下潜动画时长（秒；下潜动画 = 步骤 AnimId，由链运行器写入）")]
        public float DiveDuration = 1.567f;

        [Tooltip("出土动画 Id（权威端出土时刻经 Model.SetAnimId 切换）")]
        public int EmergeAnimId;

        [Tooltip("出土动画时长（秒；链总时长覆盖，出土后动画自然播完）")]
        public float EmergeAnimationLength = 1.5f;

        [Header("Tracking")]
        [Tooltip("下潜结束到开始追踪的延迟（秒）")]
        public float StartTrackDelay = 1f;

        [Tooltip("追踪时长上限（秒；到点强制出土）")]
        public float MaxTrackDuration = 3f;

        [Tooltip("追踪移动限速（米/秒）")]
        public float TrackSpeed = 5f;

        [Tooltip("撞伤判定半径（米；地下期间每 tick 在怪物位置结算）")]
        public float TriggerHitRadius = 2f;

        [Tooltip("撞伤伤害")]
        public int TriggerHitDamage = 1;

        [Tooltip("同一目标撞伤间隔（秒；<=0 时每 tick 均可命中）")]
        public float TriggerHitInterval;

        [Tooltip("撞到目标提前出土（true = 撞到追踪目标且移动距离达标即出土）")]
        public bool EndOnHitTrackedTarget = true;

        [Tooltip("提前出土所需的最小累计移动距离（米）")]
        public float MinMoveDistanceForHit = 1f;

        [Header("Emerge")]
        [Tooltip("出土 AOE 半径（米）")]
        public float EndAoeRadius = 5f;

        [Tooltip("出土 AOE 伤害")]
        public int EndAoeDamage = 1;

        [Tooltip("出土 AOE 受击方向反作用力")]
        public float HitForce = 5f;

        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Header("Effects")]
        [Tooltip("破土特效 prefab（纯表现，各端本地实例化于下潜时刻）")]
        public GameObject BreakEffectPrefab;

        [Tooltip("地下移动特效 prefab（纯表现，各端本地实例化于追踪开始、出土销毁）")]
        public GameObject MoveEffectPrefab;

        [Header("Sounds")]
        [Tooltip("下潜音效（空 = 不播）")]
        public string DiggingSoundPath;

        [Tooltip("出土音效（空 = 不播）")]
        public string UpSoundPath;

        public override float Duration =>
            DiveDuration + StartTrackDelay + MaxTrackDuration + EmergeAnimationLength;
    }

    /// <summary>
    /// 遁地步骤（§16.4）：下潜（无敌 + 身体隐藏 + 破土）→ 地下追踪（限速位移 + 撞伤，可撞到目标提前出土）
    /// → 出土（切出土动画 + 身体恢复 + AOE 结算 + 无敌关）。
    /// 权威端状态机驱动判定与结算；身体显隐 / 特效 / 音效各端本地。
    /// 提前出土路径仅在权威端触发，代理端按时间到路径表现（§1.7 偏差允许）。
    /// 旧制的「出土 AOE 眩晕目标」不迁移（需新增跨实体眩晕命令，独立 ticket 再议）。
    /// </summary>
    public class MonsterBurrowStep : MonsterSkillStep
    {
        private enum BurrowPhase
        {
            Diving,     // 下潜动画期
            TrackDelay, // 下潜结束 → 开始追踪的延迟
            Tracking,   // 地下追踪
            Emerging,   // 出土（一次性动作，已执行）
            Done,       // 收尾
        }

        private readonly MonsterBurrowStepConfig _burrowConfig;

        private BurrowPhase _phase = BurrowPhase.Diving;

        /// <summary> 权威端：追踪期累计移动距离（提前出土判据） </summary>
        private float _movedDistance;

        /// <summary> 权威端：同目标最近撞伤时间（间隔去重） </summary>
        private readonly Dictionary<EntityId, float> _triggerLastHitTimes = new();

        /// <summary> 本端：破土特效实例 </summary>
        private GameObject _breakEffectObj;

        /// <summary> 本端：地下移动特效实例 </summary>
        private GameObject _moveEffectObj;

        /// <summary> 本端：追踪特效是否已生成 </summary>
        private bool _moveEffectSpawned;

        /// <summary> 撞伤碰撞体缓冲 </summary>
        private readonly Collider[] _overlapBuffer = new Collider[32];

        public MonsterBurrowStep(MonsterModel model, MonsterBurrowStepConfig config)
            : base(model, config)
        {
            _burrowConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            if (_burrowConfig is null) return;

            _phase = BurrowPhase.Diving;
            _movedDistance = 0f;
            _moveEffectSpawned = false;
            _triggerLastHitTimes.Clear();

            // 表现（各端本地）：破土特效 + 下潜音效 + 身体隐藏
            SpawnBreakEffect();
            PlaySound(_burrowConfig.DiggingSoundPath);
            SetBodyPartsVisible(false);

            // 权威端：无敌
            if (HasStateAuthority)
            {
                _model.SetIsInvulnerable(true);
            }
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_burrowConfig is null) return;

            float stepElapsed = elapsed - _config.StartOffset;

            // 各端本地：追踪开始时刻生成地下移动特效
            if (!_moveEffectSpawned
                && stepElapsed >= _burrowConfig.DiveDuration + _burrowConfig.StartTrackDelay)
            {
                _moveEffectSpawned = true;
                SpawnMoveEffect();
            }

            if (!HasStateAuthority) return;

            switch (_phase)
            {
                case BurrowPhase.Diving:
                    if (stepElapsed >= _burrowConfig.DiveDuration)
                    {
                        _phase = BurrowPhase.TrackDelay;
                    }
                    break;
                case BurrowPhase.TrackDelay:
                    if (stepElapsed >= _burrowConfig.DiveDuration + _burrowConfig.StartTrackDelay)
                    {
                        BeginTracking();
                    }
                    break;
                case BurrowPhase.Tracking:
                    StepTracking(stepElapsed);
                    break;
            }
        }

        protected override void OnStepExit()
        {
            // 施法结束 / 打断兜底：仍在遁地中则保守出土收口
            if (_phase is BurrowPhase.Diving or BurrowPhase.TrackDelay or BurrowPhase.Tracking)
            {
                DoEmerge();
            }

            SetBodyPartsVisible(true);

            if (_breakEffectObj != null)
            {
                UnityEngine.Object.Destroy(_breakEffectObj);
                _breakEffectObj = null;
            }

            DestroyMoveEffect();

            _triggerLastHitTimes.Clear();
        }

        protected override void OnStepAuthorityChanged()
        {
            // 权威易主：保守关无敌（本端成为权威时收口）
            if (HasStateAuthority && _model.IsInvulnerable)
            {
                _model.SetIsInvulnerable(false);
            }
        }

        #region Tracking

        /// <summary>
        /// 开始追踪：停 Follower 让位（经 Model 事实，MoveModule 执行；出土后由 AI 恢复）。
        /// </summary>
        private void BeginTracking()
        {
            _phase = BurrowPhase.Tracking;
            _movedDistance = 0f;
            _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
        }

        /// <summary>
        /// 追踪逐 tick：向目标位置限速位移 + 撞伤；撞到目标（移动距离达标）或到点 → 出土。
        /// </summary>
        private void StepTracking(float stepElapsed)
        {
            var body = _burrowConfig.Body;
            if (body != null
                && TryGetTargetPosition(_model.CastTargetId, out var targetPos))
            {
                Vector3 toTarget = targetPos - body.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.001f)
                {
                    float step = _burrowConfig.TrackSpeed * _model.Runner.DeltaTime;
                    Vector3 move = toTarget.normalized * Mathf.Min(step, toTarget.magnitude);
                    body.position += move;
                    _movedDistance += move.magnitude;
                }
            }

            bool hitTrackedTarget = SettleTriggerHit();

            // 撞到目标提前出土（移动距离达标判据防贴脸秒出土）
            if (_burrowConfig.EndOnHitTrackedTarget
                && hitTrackedTarget
                && _movedDistance >= _burrowConfig.MinMoveDistanceForHit)
            {
                DoEmerge();
                return;
            }

            // 到点强制出土
            float emergeTime = _burrowConfig.DiveDuration
                               + _burrowConfig.StartTrackDelay
                               + _burrowConfig.MaxTrackDuration;
            if (stepElapsed >= emergeTime)
            {
                DoEmerge();
            }
        }

        /// <summary>
        /// 撞伤：怪物位置 OverlapSphere 小半径，同目标按间隔去重；返回是否撞到追踪目标。
        /// </summary>
        private bool SettleTriggerHit()
        {
            var self = _burrowConfig.SelfTransform;
            if (self == null) return false;
            if (_burrowConfig.TriggerHitRadius <= 0f) return false;
            if (_burrowConfig.TriggerHitDamage <= 0) return false;

            bool hitTrackedTarget = false;
            Vector3 center = self.position;
            float now = Time.time;

            int count = Physics.OverlapSphereNonAlloc(
                center,
                _burrowConfig.TriggerHitRadius,
                _overlapBuffer,
                _burrowConfig.DamageLayer,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < count; i++)
            {
                var col = _overlapBuffer[i];
                if (col == null) continue;

                var tag = col.GetComponentInParent<EntityTag>();
                if (tag == null) continue;

                EntityId targetId = tag.Id;
                if (!targetId.IsValid) continue;

                if (_burrowConfig.TriggerHitInterval > 0f
                    && _triggerLastHitTimes.TryGetValue(targetId, out float lastTime)
                    && now - lastTime < _burrowConfig.TriggerHitInterval)
                {
                    continue;
                }
                _triggerLastHitTimes[targetId] = now;

                if (targetId == _model.CastTargetId)
                {
                    hitTrackedTarget = true;
                }

                Vector3 hitPoint = col.transform.position;
                Vector3 hitDirection = hitPoint - center;
                hitDirection.y = 0f;
                hitDirection = hitDirection.sqrMagnitude > 0.001f ? hitDirection.normalized : self.forward;

                var damageData = new DamageData { Damage = _burrowConfig.TriggerHitDamage, AttackerId = _model.Id, };

                var hitData = new HitData
                {
                    HitPoint = hitPoint, HitDirection = hitDirection, Force = 0f,
                };

                Msger.Send(MsgID.ApplyDamage, targetId, damageData);
                Msger.Send(MsgID.ApplyHit, targetId, hitData);
            }

            return hitTrackedTarget;
        }

        #endregion

        #region Emerge

        /// <summary>
        /// 出土（权威端触发）：切出土动画 + 身体恢复 + 出土音效 + 销毁地下特效（各端本地）；AOE 结算 + 无敌关（权威端）。
        /// </summary>
        private void DoEmerge()
        {
            _phase = BurrowPhase.Emerging;

            // 表现（各端本地）
            SetBodyPartsVisible(true);
            PlaySound(_burrowConfig.UpSoundPath);
            DestroyMoveEffect();

            if (!HasStateAuthority) return;

            // 判定与状态（权威端）
            _model.SetIsInvulnerable(false);
            _model.SetAnimId(_burrowConfig.EmergeAnimId);
            SettleEmergeAoe();

            _phase = BurrowPhase.Done;
        }

        /// <summary>
        /// 出土 AOE：怪物位置 OverlapSphere 单次结算。
        /// </summary>
        private void SettleEmergeAoe()
        {
            var self = _burrowConfig.SelfTransform;
            if (self == null) return;

            var hitTargets = new HashSet<EntityId>();

            int count = Physics.OverlapSphereNonAlloc(
                self.position,
                _burrowConfig.EndAoeRadius,
                _overlapBuffer,
                _burrowConfig.DamageLayer,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < count; i++)
            {
                var col = _overlapBuffer[i];
                if (col == null) continue;

                var tag = col.GetComponentInParent<EntityTag>();
                if (tag == null) continue;

                EntityId targetId = tag.Id;
                if (!targetId.IsValid) continue;
                if (!hitTargets.Add(targetId)) continue;

                Vector3 hitPoint = col.transform.position;
                Vector3 hitDirection = hitPoint - self.position;
                hitDirection.y = 0f;
                hitDirection = hitDirection.sqrMagnitude > 0.001f ? hitDirection.normalized : self.forward;

                var damageData = new DamageData { Damage = _burrowConfig.EndAoeDamage, AttackerId = _model.Id, };

                var hitData = new HitData
                {
                    HitPoint = hitPoint, HitDirection = hitDirection, Force = _burrowConfig.HitForce,
                };

                Msger.Send(MsgID.ApplyDamage, targetId, damageData);
                Msger.Send(MsgID.ApplyHit, targetId, hitData);
            }
        }

        #endregion

        #region Effects

        private void SpawnBreakEffect()
        {
            if (_burrowConfig.BreakEffectPrefab == null) return;

            var self = _burrowConfig.SelfTransform;
            if (self == null) return;

            _breakEffectObj = UnityEngine.Object.Instantiate(
                _burrowConfig.BreakEffectPrefab,
                self.position,
                self.rotation,
                self
            );
        }

        private void SpawnMoveEffect()
        {
            if (_burrowConfig.MoveEffectPrefab == null) return;

            var body = _burrowConfig.Body != null ? _burrowConfig.Body : _burrowConfig.SelfTransform;
            if (body == null) return;

            _moveEffectObj = UnityEngine.Object.Instantiate(
                _burrowConfig.MoveEffectPrefab,
                body.position,
                body.rotation,
                body
            );
        }

        private void DestroyMoveEffect()
        {
            if (_moveEffectObj != null)
            {
                UnityEngine.Object.Destroy(_moveEffectObj);
                _moveEffectObj = null;
            }
        }

        private void SetBodyPartsVisible(bool visible)
        {
            if (_burrowConfig.BodyPartsToHide == null) return;

            for (int i = 0; i < _burrowConfig.BodyPartsToHide.Length; i++)
            {
                var part = _burrowConfig.BodyPartsToHide[i];
                if (part == null) continue;
                part.gameObject.SetActive(visible);
            }
        }

        private void PlaySound(string soundPath)
        {
            if (string.IsNullOrEmpty(soundPath)) return;
            if (_burrowConfig.SelfTransform != null) AudioMgr.Play(soundPath, _burrowConfig.SelfTransform);
        }

        #endregion
    }
}
