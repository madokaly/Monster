using System;
using System.Collections.Generic;
using Framework;
using Framework.Core;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterWaterBallStepConfig : MonsterSkillStepConfig
    {
        [Header("References")]
        [Tooltip("汇聚 / 抛射期跟随的怪物本体 Transform")]
        public Transform Body;

        [Tooltip("水球特效 prefab；子节点 FireSmkA = 汇聚烟，water_ball = 水球")]
        public GameObject EffectPrefab;

        [Tooltip("水球特效相对本体挂点的本地位置偏移；汇聚与抛射起点共用")]
        public Vector3 SpawnOffset = Vector3.zero;

        [Header("Charge")]
        [Tooltip("汇聚时长（秒；期间 FireSmkA 显示，water_ball 从 0 长到最终倍率）")]
        public float ChargeDuration = 4.5f;

        [Tooltip("水球最终缩放倍率（相对特效 prefab 内 water_ball 原始 localScale）")]
        public float FinalScaleMultiplier = 1f;

        [Tooltip("汇聚期本体 yaw 匀角速转向目标（度/秒）")]
        public float TurnSpeed = 180f;

        [Header("Throw")]
        [Tooltip("抛射时长（秒；汇聚结束后水球沿抛物线飞向锁定落点）")]
        public float ThrowDuration = 1.5f;

        [Tooltip("抛物线最高附加高度（米）")]
        public float ArcHeight = 5f;

        [Tooltip("目标失效时的本体前向抛射距离（米）")]
        public float FallbackThrowDistance = 20f;

        [Header("Damage")]
        [Tooltip("伤害结算层（对层内目标判定命中并发 ApplyDamage；0 = 不过滤）")]
        public LayerMask DamageLayer;

        [Tooltip("落地伤害")]
        public int Damage = 150;

        [Tooltip("落地伤害半径")]
        public float Radius = 8f;

        [Tooltip("高度过滤（受击目标与落点高度差超过该值不命中；<=0 不过滤）")]
        public float MaxAttackHeight = 3f;

        [Tooltip("受击方向反作用力")]
        public float HitForce = 0f;

        [Header("Interrupt")]
        [Tooltip("汇聚期累计受击伤害达到该值时打断并破防（0 = 不启用；统计防御减免后的伤害）")]
        public int InterruptDamageThreshold = 500;

        [Tooltip("该次打断使用的破防动画 Id（<=0 时回退怪物表配置 BreakAnim）")]
        public int InterruptAnimId = 50;

        [Tooltip("蓄力被打断时的音效路径数组（随机其一，各端本地播，音源跟随怪物本体；空 = 不播")]
        public string[] ChargeInterruptSounds;

        [Header("Splash")]
        [Tooltip("落地水流溅射特效 prefab（允许为空；空则跳过）")]
        public GameObject SplashEffectPrefab;

        [Tooltip("落地爆炸音效路径数组（随机其一，各端本地播，音源跟随落地特效；空 = 不播）")]
        public string[] SplashSounds;

        public override float Duration => ChargeDuration + ThrowDuration;
    }

    /// <summary>
    /// 汇聚水球步骤：全端本地实例化特效，汇聚期跟随本体转向并让 water_ball 平滑变大；
    /// 汇聚结束隐藏 FireSmkA、锁定本端目标当前位置并沿抛物线抛出，落地本端单次球形结算
    /// （受击方本地结算）。汇聚期权威端可按 Config 阈值被累计伤害打断为破防；
    /// 抛射开始后不再可打断。
    /// </summary>
    public class MonsterWaterBallStep : MonsterSkillStep
    {
        private readonly MonsterWaterBallStepConfig _waterBallConfig;

        private readonly HashSet<EntityId> _hitTargets = new();

        private GameObject _effectInstance;
        private Transform _fireSmkTransform;
        private Transform _waterBallTransform;
        private Vector3 _waterBallBaseScale;
        private Vector3 _throwStartPosition;
        private Vector3 _landingPosition;
        private GameObject _splashEffect;
        private bool _throwStarted;
        private bool _damageResolved;

        public MonsterWaterBallStep(MonsterModel model, MonsterWaterBallStepConfig config)
            : base(model, config)
        {
            _waterBallConfig = config;
        }

        protected override void OnStepEnter(float elapsed)
        {
            ResetState();

            if (_waterBallConfig is null) return;

            if (HasStateAuthority)
            {
                _model.SetMoveCommand(new MonsterMoveCommand { IsStopped = true });
                _model.RegisterCastInterruptPolicy(
                    _waterBallConfig.InterruptDamageThreshold,
                    _waterBallConfig.InterruptAnimId
                );
            }

            CreateEffect();
        }

        protected override void OnStepTick(float elapsed)
        {
            if (_waterBallConfig is null) return;

            float stepElapsed = Mathf.Max(0f, elapsed - _waterBallConfig.StartOffset);
            if (stepElapsed < MonsterWaterBallTiming.GetChargeDuration(_waterBallConfig))
            {
                if (HasStateAuthority)
                {
                    TurnBodyTowardTarget();
                }
                return;
            }

            BeginThrow(stepElapsed);
        }

        protected override void OnStepVisualUpdate(float elapsed)
        {
            if (_waterBallConfig is null) return;

            float stepElapsed = Mathf.Max(0f, elapsed - _waterBallConfig.StartOffset);
            bool thrown = stepElapsed >= MonsterWaterBallTiming.GetChargeDuration(_waterBallConfig);
            if (!thrown)
            {
                UpdateChargeVisual(stepElapsed);
                return;
            }

            // 投掷事实已由同帧逻辑通道建立（Tick 先于本回调），此处仅采样轨迹
            if (!_throwStarted || _effectInstance == null) return;

            _effectInstance.transform.position = MonsterWaterBallTiming.EvaluateThrowPosition(
                _waterBallConfig,
                _throwStartPosition,
                _landingPosition,
                stepElapsed
            );
        }

        protected override void OnStepContentEnded()
        {
            if (_waterBallConfig is null) return;

            float landingElapsed = MonsterWaterBallTiming.GetLandingTime(_waterBallConfig);
            BeginThrow(landingElapsed);
            LandWaterBall();
        }

        protected override void OnStepExit()
        {
            PlayChargeInterruptSound();
            ClearAuthorityInterruptPolicy();
            DestroyEffects();
        }

        protected override void OnStepDispose()
        {
            ClearAuthorityInterruptPolicy();
            DestroyEffects();
        }

        #region Private Methods

        private void ResetState()
        {
            DestroyEffects();

            _throwStarted = false;
            _damageResolved = false;
            _throwStartPosition = Vector3.zero;
            _landingPosition = Vector3.zero;
            _hitTargets.Clear();
        }

        private void CreateEffect()
        {
            if (_waterBallConfig.EffectPrefab == null)
            {
                Logging.Error("[MonsterWaterBallStep] CreateEffect: EffectPrefab 为 null，请检查 Inspector 引用。");
                return;
            }

            var body = _waterBallConfig.Body;
            if (body == null)
            {
                Logging.Error("[MonsterWaterBallStep] CreateEffect: Body 为 null，请检查 Inspector 引用。");
                return;
            }

            _effectInstance = UnityEngine.Object.Instantiate(
                _waterBallConfig.EffectPrefab,
                MonsterWaterBallTiming.GetSpawnPosition(_waterBallConfig, body),
                body.rotation
            );
            _effectInstance.transform.SetParent(body, true);
            _effectInstance.transform.localRotation = Quaternion.identity;
            _effectInstance.transform.localScale = Vector3.one;

            _fireSmkTransform = _effectInstance.transform.Find(MonsterWaterBallTiming.FIRE_SMOKE_CHILD_NAME);
            _waterBallTransform = _effectInstance.transform.Find(MonsterWaterBallTiming.WATER_BALL_CHILD_NAME);
            if (_fireSmkTransform == null || _waterBallTransform == null)
            {
                Logging.Error(
                    "[MonsterWaterBallStep] CreateEffect: EffectPrefab 缺少 FireSmkA 或 water_ball 子节点。"
                );
                UnityEngine.Object.Destroy(_effectInstance);
                _effectInstance = null;
                _fireSmkTransform = null;
                _waterBallTransform = null;
                return;
            }

            _waterBallBaseScale = _waterBallTransform.localScale;
            _fireSmkTransform.gameObject.SetActive(true);
            _waterBallTransform.gameObject.SetActive(true);
            UpdateChargeVisual(0f);

            var particleSystems = _effectInstance.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particleSystems.Length; i++)
            {
                particleSystems[i].Play(true);
            }
        }

        private void UpdateChargeVisual(float stepElapsed)
        {
            if (_fireSmkTransform != null && !_fireSmkTransform.gameObject.activeSelf)
            {
                _fireSmkTransform.gameObject.SetActive(true);
            }

            if (_waterBallTransform == null) return;

            float scale = MonsterWaterBallTiming.GetGrowthScale(_waterBallConfig, stepElapsed);
            _waterBallTransform.localScale = _waterBallBaseScale * scale;
        }

        private void BeginThrow(float stepElapsed)
        {
            if (_throwStarted) return;
            if (_waterBallConfig.Body == null || _effectInstance == null) return;

            _throwStarted = true;
            _throwStartPosition = _effectInstance.transform.position;

            bool hasTarget = TryGetTargetPosition(_model.CastTargetId, out Vector3 targetPosition);
            _landingPosition = MonsterWaterBallTiming.ResolveLandingPosition(
                _waterBallConfig,
                _waterBallConfig.Body.position,
                _waterBallConfig.Body.forward,
                hasTarget,
                targetPosition
            );

            _effectInstance.transform.SetParent(null, true);
            if (_fireSmkTransform != null)
            {
                _fireSmkTransform.gameObject.SetActive(false);
            }

            ClearAuthorityInterruptPolicy();

            _effectInstance.transform.position = MonsterWaterBallTiming.EvaluateThrowPosition(
                _waterBallConfig,
                _throwStartPosition,
                _landingPosition,
                stepElapsed
            );
        }

        private void LandWaterBall()
        {
            if (_damageResolved || !_throwStarted) return;

            _damageResolved = true;
            if (_waterBallTransform != null)
            {
                _waterBallTransform.gameObject.SetActive(false);
            }

            PlaySplashEffect();
            SettleDamage();
        }

        private void PlaySplashEffect()
        {
            if (_waterBallConfig.SplashEffectPrefab == null || _splashEffect != null) return;

            _splashEffect = UnityEngine.Object.Instantiate(
                _waterBallConfig.SplashEffectPrefab,
                _landingPosition,
                Quaternion.identity
            );

            MonsterSkillPresentation.PlayRandomSound(_waterBallConfig.SplashSounds, _splashEffect.transform);
        }

        private void SettleDamage()
        {
            var body = _waterBallConfig.Body;
            if (body == null) return;

            Vector3 fallbackDirection = _landingPosition - _throwStartPosition;
            fallbackDirection.y = 0f;
            fallbackDirection = fallbackDirection.sqrMagnitude > 0.001f
                ? fallbackDirection.normalized
                : body.forward;

            SettleSphereDamage(
                _landingPosition,
                _waterBallConfig.Radius,
                _waterBallConfig.DamageLayer,
                _waterBallConfig.MaxAttackHeight,
                _waterBallConfig.Damage,
                _waterBallConfig.HitForce,
                _hitTargets,
                fallbackDirection
            );
        }

        private void PlayChargeInterruptSound()
        {
            if (_model.CastingChainIndex != -1 || _throwStarted || _effectInstance == null) return;

            MonsterSkillPresentation.PlayRandomSound(_waterBallConfig.ChargeInterruptSounds, _waterBallConfig.Body);
        }
        private void TurnBodyTowardTarget()
        {
            var body = _waterBallConfig.Body;
            if (body == null || !TryGetTargetPosition(_model.CastTargetId, out var targetPosition)) return;

            Vector3 toTarget = targetPosition - body.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude <= 0.0001f) return;

            float maxDegrees = _waterBallConfig.TurnSpeed * Mathf.Max(0f, Time.deltaTime);
            _model.PushBodyRotation(
                MonsterAimTiming.RotateTowardsPlanarTarget(body.rotation, toTarget, maxDegrees)
            );
        }

        private void ClearAuthorityInterruptPolicy()
        {
            if (!HasStateAuthority) return;

            _model.ClearCastInterruptPolicy();
        }

        private void DestroyEffects()
        {
            if (_effectInstance != null)
            {
                UnityEngine.Object.Destroy(_effectInstance);
            }
            if (_splashEffect != null)
            {
                UnityEngine.Object.Destroy(_splashEffect);
            }

            _effectInstance = null;
            _fireSmkTransform = null;
            _waterBallTransform = null;
            _splashEffect = null;
        }

        #endregion
    }
}
