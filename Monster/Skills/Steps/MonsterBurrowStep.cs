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
        [Tooltip("判定原点 / 移动累计与音效挂点（怪物自身 Transform）")]
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

        [Tooltip("地下追踪寻路速度（米/秒；经移动指令下发，MoveModule 驱动 FollowerEntity）")]
        public float TrackSpeed = 5f;

        [Tooltip("追踪目标点刷新间隔（秒；节流寻路重规划，目标静止时 Model no-op 早退兜底）")]
        public float DestinationRefreshInterval = 0.3f;

        [Tooltip("撞伤判定半径（米；地下期间每 tick 在怪物位置结算；同时作寻路停距）")]
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

        [Header("Underground Collision")]
        [Tooltip("遁地期间切为 trigger 的碰撞体（隐藏身体时切换、出土恢复；各端本地，防地下移动物理阻挡玩家）")]
        public Collider[] CollidersToTrigger;

        [Header("Rock Stun（出土撞陨石自眩晕）")]
        [Tooltip("出土点与未失效陨石落点的判定距离（米，0 = 关闭撞石眩晕）")]
        public float RockStunDetectRadius = 5f;

        [Tooltip("眩晕动画 Id（撞石触发，时长取动画表）")]
        public int RockStunAnimId = 40;

        [Tooltip("出土到切眩晕的延迟（秒；给出土动画留表现时间）")]
        public float StunDelayAfterEmerge = 0.5f;

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
    /// 遁地步骤：下潜（无敌 + 身体隐藏 + 碰撞体切 trigger + 破土）→ 地下追踪（寻路跟随目标：
    /// 经移动指令下发——AI 施法期间让位不重申，MoveModule 驱动 FollowerEntity，全程留在
    /// NavMesh 内且贴地 Y 跟随，玩家无法引导出网卡死；可贴近目标提前出土）→ 出土（切出土动画 +
    /// 身体恢复 + 碰撞体恢复 + AOE 结算 + 无敌关 + 停移动）。
    /// 移动指令 / 相位机 / 提前出土传感器 / 无敌 / 动画写入在权威端（感知与结算分离）；
    /// 撞伤与出土 AOE 各端受击方本地结算（§1.7——代理端按链尾时间到路径表现，
    /// 判定落在本端看到的时刻，所见即所得）；身体显隐 / 碰撞体 / 特效 / 音效各端本地。
    /// 出土撞陨石自眩晕（旧制 DiveGround 的 stun 语义：撞的是 boss 自己）：
    /// 权威端出土时按 Model 落石事实检测未失效锚点，命中即置位该石碎裂位
    /// （在场陨石表现体轮询掩码提前碎裂），并延迟切眩晕动画。
    /// </summary>
    public class MonsterBurrowStep : MonsterSkillStep
    {
        /// <summary> 落石事实锚点槽位数（与 MonsterRockfallState 定长一致） </summary>
        private const int MAX_ROCK_ANCHOR_CHECKS = 3;

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

        /// <summary> 权威端：追踪期累计实际移动距离（提前出土判据） </summary>
        private float _movedDistance;

        /// <summary> 权威端：上一 tick 本体位置（实际位移累计） </summary>
        private Vector3 _lastSelfPosition;

        /// <summary> 权威端：下次寻路目标点刷新时刻（步骤时钟） </summary>
        private float _nextDestinationRefreshTime;

        /// <summary> 本端：同目标最近撞伤时间（间隔去重，各端独立） </summary>
        private readonly Dictionary<EntityId, float> _triggerLastHitTimes = new();

        /// <summary> 本端：是否已出土（地下撞伤时间窗收口；各端在自己的出土时刻停伤） </summary>
        private bool _emerged;

        /// <summary> 本端：破土特效实例 </summary>
        private GameObject _breakEffectObj;

        /// <summary> 本端：地下移动特效实例 </summary>
        private GameObject _moveEffectObj;

        /// <summary> 本端：追踪特效是否已生成 </summary>
        private bool _moveEffectSpawned;

        /// <summary> 撞伤碰撞体缓冲 </summary>
        private readonly Collider[] _overlapBuffer = new Collider[32];

        /// <summary> 权威端：撞石眩晕待触发（出土时命中未失效陨石） </summary>
        private bool _rockStunPending;

        /// <summary> 权威端：眩晕触发时刻（Time.time；出土动画留表现时间） </summary>
        private float _rockStunTriggerTime;

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
            _nextDestinationRefreshTime = 0f;
            _moveEffectSpawned = false;
            _rockStunPending = false;
            _emerged = false;
            _triggerLastHitTimes.Clear();

            // 表现（各端本地）：破土特效 + 下潜音效 + 身体隐藏 + 碰撞体切 trigger（防地下阻挡玩家）
            SpawnBreakEffect();
            PlaySound(_burrowConfig.DiggingSoundPath);
            SetBodyPartsVisible(false);
            SetCollidersTrigger(true);

            // 权威端：无敌（移动指令无需处理——施放起点 AI 已停驻，追踪期由本步骤接管）
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
                && stepElapsed >= MonsterBurrowTiming.GetTrackStartTime(_burrowConfig))
            {
                _moveEffectSpawned = true;
                SpawnMoveEffect();
            }

            // 各端：地下撞伤（受击方本地结算 §1.7；时间窗 = 追踪期开始 → 本端出土 / 内容结束，
            // 代理端按链尾时间到路径表现，撞伤随本端地下视角延续至本端出土时刻；收尾段不结算）
            if (!_emerged
                && !IsContentEnded
                && stepElapsed >= MonsterBurrowTiming.GetTrackStartTime(_burrowConfig))
            {
                SettleTriggerHit();
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
                    if (stepElapsed >= MonsterBurrowTiming.GetTrackStartTime(_burrowConfig))
                    {
                        BeginTracking();
                    }
                    break;
                case BurrowPhase.Tracking:
                    StepTracking(stepElapsed);
                    break;
            }

            // 撞石眩晕到点触发（TriggerStunned 内部 InterruptCasting 会回调本步 Exit——相位守卫已可重入）
            if (_rockStunPending && Time.time >= _rockStunTriggerTime)
            {
                _rockStunPending = false;
                _model.TriggerStunned(_burrowConfig.RockStunAnimId);
            }
        }

        protected override void OnStepExit()
        {
            // 施法结束 / 打断兜底：仍在遁地中则保守出土收口（出土内含权威端停移动）
            if (_phase is BurrowPhase.Diving or BurrowPhase.TrackDelay or BurrowPhase.Tracking)
            {
                DoEmerge();
            }

            _rockStunPending = false;

            SetBodyPartsVisible(true);
            SetCollidersTrigger(false);

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
        /// 开始追踪：重置位移累计，立即下发首条寻路指令。
        /// </summary>
        private void BeginTracking()
        {
            _phase = BurrowPhase.Tracking;
            _movedDistance = 0f;
            _nextDestinationRefreshTime = 0f;

            var self = _burrowConfig.SelfTransform;
            _lastSelfPosition = self != null ? self.position : Vector3.zero;

            RefreshTrackingDestination(0f);
        }

        /// <summary>
        /// 下发 / 刷新寻路指令（权威端）：目标实时位置为目的地，速度 = TrackSpeed、停距 = 撞伤半径
        /// （到位即贴近目标，供提前出土传感器触发）。落点投影合法节点由 MoveModule 保证；
        /// 目标不可达时 Follower 停在最近可达点，超时强制出土兜底。
        /// </summary>
        private void RefreshTrackingDestination(float stepElapsed)
        {
            _nextDestinationRefreshTime = stepElapsed + Mathf.Max(0.05f, _burrowConfig.DestinationRefreshInterval);

            if (!TryGetTargetPosition(_model.CastTargetId, out var targetPos)) return;

            _model.SetMoveCommand(new MonsterMoveCommand
            {
                IsStopped = false,
                Destination = targetPos,
                MoveSpeed = _burrowConfig.TrackSpeed,
                Gait = MonsterGait.Run,
                StopDistance = Mathf.Max(0.1f, _burrowConfig.TriggerHitRadius),
            });
        }

        /// <summary>
        /// 追踪逐 tick（权威端）：累计实际位移（提前出土判据——寻路实际走了多远，而非指令意图）；
        /// 节流刷新寻路指令；贴近追踪目标（移动距离达标）或到点 → 出土。
        /// </summary>
        private void StepTracking(float stepElapsed)
        {
            var self = _burrowConfig.SelfTransform;
            if (self != null)
            {
                Vector3 delta = self.position - _lastSelfPosition;
                delta.y = 0f;
                _movedDistance += delta.magnitude;
                _lastSelfPosition = self.position;
            }

            if (stepElapsed >= _nextDestinationRefreshTime)
            {
                RefreshTrackingDestination(stepElapsed);
            }

            // 贴近目标提前出土（感知与结算分离：权威端纯位置查询驱动自身行为，
            // 移动距离达标判据防贴脸秒出土；撞伤判定归各端受击方本地，§1.7）
            if (_burrowConfig.EndOnHitTrackedTarget
                && IsNearTrackedTarget()
                && _movedDistance >= _burrowConfig.MinMoveDistanceForHit)
            {
                DoEmerge();
                return;
            }

            // 到点强制出土
            float emergeTime = MonsterBurrowTiming.GetForcedEmergeTime(_burrowConfig);
            if (stepElapsed >= emergeTime)
            {
                DoEmerge();
            }
        }

        /// <summary>
        /// 权威端行为传感器：追踪目标与本端怪物位置的平面距离 ≤ TriggerHitRadius
        /// （旧「撞伤命中即出土」的贴近判据等价化）。读本端位置——怪物端自己的
        /// 行为决策，非受害者伤害判定，代理误差可接受。
        /// </summary>
        private bool IsNearTrackedTarget()
        {
            var self = _burrowConfig.SelfTransform;
            if (self == null) return false;

            if (!TryGetTargetPosition(_model.CastTargetId, out var targetPos)) return false;

            Vector3 toTarget = targetPos - self.position;
            toTarget.y = 0f;
            return toTarget.magnitude <= _burrowConfig.TriggerHitRadius;
        }

        /// <summary>
        /// 地下撞伤（各端受击方本地结算，§1.7）：怪物位置 OverlapSphere 小半径，
        /// 仅结算 SA 在本端的目标，同目标按间隔去重（非本端目标不结算、不记录去重）。
        /// </summary>
        private void SettleTriggerHit()
        {
            var self = _burrowConfig.SelfTransform;
            if (self == null) return;
            if (_burrowConfig.TriggerHitRadius <= 0f) return;
            if (_burrowConfig.TriggerHitDamage <= 0) return;

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

                // 受击方本地结算（§1.7）：非本端目标交目标权威端自行判定
                if (!MonsterSkillDamage.IsTargetAuthoritativeHere(targetId)) continue;

                if (_burrowConfig.TriggerHitInterval > 0f
                    && _triggerLastHitTimes.TryGetValue(targetId, out float lastTime)
                    && now - lastTime < _burrowConfig.TriggerHitInterval)
                {
                    continue;
                }
                _triggerLastHitTimes[targetId] = now;

                Vector3 hitPoint = col.transform.position;
                Vector3 hitDirection = hitPoint - center;
                hitDirection.y = 0f;
                hitDirection = hitDirection.sqrMagnitude > 0.001f ? hitDirection.normalized : self.forward;

                var damageData = new DamageData { Damage = _burrowConfig.TriggerHitDamage, AttackerId = _model.Id, };

                var hitData = new HitData
                {
                    HitPoint = hitPoint,
                    HitDirection = hitDirection,
                    Force = 0f,
                };

                Msger.Send(MsgID.ApplyDamage, targetId, damageData);
                Msger.Send(MsgID.ApplyHit, targetId, hitData);
            }
        }

        #endregion

        #region Emerge

        /// <summary>
        /// 出土（各端在本端时刻执行）：身体恢复 + 碰撞体恢复 + 出土音效 + 销毁地下特效（表现）
        /// + 出土 AOE 结算（受击方本地 §1.7，落在本端看到的出土时刻）；停移动 / 无敌关 /
        /// 出土动画写入 / 撞石置位与眩晕检测（权威端）。权威端提前出土时代理端仍在地下表现，
        /// 按链尾时间到路径收口（§1.7 偏差允许）。
        /// </summary>
        private void DoEmerge()
        {
            _phase = BurrowPhase.Emerging;
            _emerged = true;

            // 表现（各端本地）
            SetBodyPartsVisible(true);
            SetCollidersTrigger(false);
            PlaySound(_burrowConfig.UpSoundPath);
            DestroyMoveEffect();

            // 判定（各端受击方本地）
            SettleEmergeAoe();

            // 状态（权威端）
            if (!HasStateAuthority) return;

            _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
            _model.SetIsInvulnerable(false);
            _model.SetAnimId(_burrowConfig.EmergeAnimId);
            TryBeginRockStun();

            _phase = BurrowPhase.Done;
        }

        /// <summary>
        /// 出土 AOE（各端受击方本地结算，§1.7）：怪物位置单次球形结算，复用基类共享管线。
        /// </summary>
        private void SettleEmergeAoe()
        {
            var self = _burrowConfig.SelfTransform;
            if (self == null) return;

            SettleSphereDamage(
                self.position,
                _burrowConfig.EndAoeRadius,
                _burrowConfig.DamageLayer,
                0f,
                _burrowConfig.EndAoeDamage,
                _burrowConfig.HitForce,
                new HashSet<EntityId>(),
                self.forward
            );
        }

        #endregion

        #region Rock Stun

        /// <summary>
        /// 撞石眩晕检测（权威端，出土时刻）：Model 落石事实存在未失效锚点且距出土点
        /// ≤ RockStunDetectRadius → 立即置位该石碎裂位（在场陨石表现体轮询掩码提前碎裂，
        /// 碎裂 = 撞击事实），并延迟 StunDelayAfterEmerge 触发眩晕（Tick 内到点执行）。
        /// </summary>
        private void TryBeginRockStun()
        {
            if (_burrowConfig.RockStunDetectRadius <= 0f || _burrowConfig.RockStunAnimId <= 0) return;

            var self = _burrowConfig.SelfTransform;
            if (self == null) return;

            var rockfallState = _model.RockfallState;
            if (rockfallState.Seq <= 0 || _model.Runner.Tick >= rockfallState.ExpireTick) return;

            float sqrRadius = _burrowConfig.RockStunDetectRadius * _burrowConfig.RockStunDetectRadius;
            for (int i = 0; i < MAX_ROCK_ANCHOR_CHECKS; i++)
            {
                Vector3 anchor = i switch
                {
                    0 => rockfallState.Anchor0,
                    1 => rockfallState.Anchor1,
                    _ => rockfallState.Anchor2,
                };

                Vector3 toAnchor = anchor - self.position;
                toAnchor.y = 0f;
                if (toAnchor.sqrMagnitude > sqrRadius) continue;

                // 撞石瞬间置位该石碎裂（Seq 不变写回快照；新轮落石自带掩码归零，不误伤）
                rockfallState.BrokenMask |= (byte)(1 << i);
                _model.SetRockfallState(rockfallState);

                _rockStunPending = true;
                _rockStunTriggerTime = Time.time + Mathf.Max(0f, _burrowConfig.StunDelayAfterEmerge);
                return;
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

            var self = _burrowConfig.SelfTransform;
            if (self == null) return;

            _moveEffectObj = UnityEngine.Object.Instantiate(
                _burrowConfig.MoveEffectPrefab,
                self.position,
                self.rotation,
                self
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

        /// <summary>
        /// 遁地期间主碰撞体切 trigger（各端本地 isTrigger；隐藏身体时开启、出土恢复）。
        /// </summary>
        private void SetCollidersTrigger(bool isTrigger)
        {
            if (_burrowConfig.CollidersToTrigger == null) return;

            for (int i = 0; i < _burrowConfig.CollidersToTrigger.Length; i++)
            {
                var collider = _burrowConfig.CollidersToTrigger[i];
                if (collider == null) continue;
                collider.isTrigger = isTrigger;
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
