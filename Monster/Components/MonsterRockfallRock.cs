using Framework.Core;
using Game.Entities;
using UnityEngine;

namespace Game.Components
{
    /// <summary>
    /// 落石陨石纯表现组件（各端本地）：警告（生成即显示）→ 下落（运动学积分）→ 落地驻场 → 碎裂自毁。
    /// 时序单源于落石步骤 Config（MonsterRockfallTiming 共享计算）；
    /// 判定归 Rockfall 步骤受击方本地结算，本组件不结算伤害（事实与表现分流）。
    /// 驻场期间轮询落石事实的碎裂位掩码提前碎裂（遁地出土撞到本石 → 权威端置位 →
    /// 网络事实轮询，MonsterProjectileVisual 同范式）；掩码按 Seq 归属本轮，新一轮落石不误碎旧石。
    /// </summary>
    public class MonsterRockfallRock : MonoBehaviour
    {
        [Header("Visuals")]
        [SerializeField]
        [Tooltip("警告视觉（生成即显示，落地时隐藏）")]
        private GameObject _warningVfx;

        [SerializeField]
        [Tooltip("陨石石体视觉（下落期从高处积分落到驻场位）")]
        private GameObject _rockVfx;

        [SerializeField]
        [Tooltip("落地一击视觉（落地时激活）")]
        private GameObject _landVfx;

        [SerializeField]
        [Tooltip("碎裂视觉（自然到期 / 撞碎时激活）")]
        private GameObject _breakVfx;

        [Header("Sounds")]
        [SerializeField]
        [Tooltip("警告音效（空 = 不播）")]
        private string _warningSound = "event:/game/creature/boss2_skill4_warning";

        [SerializeField]
        [Tooltip("落地音效（空 = 不播）")]
        private string _landSound = "event:/game/creature/rock2";

        [SerializeField]
        [Tooltip("碎裂音效（空 = 不播）")]
        private string _breakSound = "event:/game/creature/break";

        [SerializeField]
        [Tooltip("碎裂后自毁时长（秒）")]
        private float _breakLifetime = 2f;

        private MonsterModel _model;
        private int _rockIndex;
        private int _spawnSeq;
        private float _timelineOffset;
        private float _spawnTime;
        private float _fallStartTime;
        private float _fallDuration;
        private float _lingerDuration;
        private float _fallInitialSpeed;
        private float _fallGravity;
        private float _fallMaxSpeed;
        private float _fallHeight;
        private Vector3 _rockRestLocalPosition;

        private enum RockPhase
        {
            Warning,   // 警告已显示（生成即显示），等下落
            Falling,   // 下落中
            Landed,    // 落地驻场
            Broken,    // 已碎裂（待自毁）
        }

        private RockPhase _phase = RockPhase.Warning;

        /// <summary>
        /// 初始化（由落石步骤在各端调用）。
        /// timelineOffset = 生成时刻的本端步骤时钟读数（表现体本地时间轴与步骤对齐，容忍网络迟到）。
        /// 警告圈生成即显示（步骤开始 = 全部警报就位），落地才隐藏。
        /// </summary>
        public void Initialize(MonsterModel model, MonsterRockfallStepConfig config, int rockIndex, float timelineOffset)
        {
            _model = model;
            _rockIndex = rockIndex;
            _spawnSeq = model != null ? model.RockfallState.Seq : 0;
            _timelineOffset = timelineOffset;
            _spawnTime = Time.time;
            _fallStartTime = MonsterRockfallTiming.GetRockFallStartTime(config, rockIndex);
            _fallDuration = Mathf.Max(0.01f, MonsterRockfallTiming.GetFallDuration(config));
            _lingerDuration = Mathf.Max(0f, config.RockLingerDuration);
            _fallInitialSpeed = Mathf.Max(0f, config.RockFallInitialSpeed);
            _fallGravity = Mathf.Max(0.001f, config.RockFallGravity);
            _fallMaxSpeed = Mathf.Max(0.001f, config.RockMaxFallSpeed);
            _fallHeight = Mathf.Max(0f, config.RockFallHeight);

            if (_warningVfx != null) _warningVfx.SetActive(true);
            PlaySound(_warningSound);
        }

        private void Awake()
        {
            if (_rockVfx != null)
            {
                _rockRestLocalPosition = _rockVfx.transform.localPosition;
                _rockVfx.SetActive(false);
            }
            if (_warningVfx != null) _warningVfx.SetActive(false);
            if (_landVfx != null) _landVfx.SetActive(false);
            if (_breakVfx != null) _breakVfx.SetActive(false);
        }

        private void Update()
        {
            if (_phase == RockPhase.Broken) return;

            float age = _timelineOffset + (Time.time - _spawnTime);

            switch (_phase)
            {
                case RockPhase.Warning:
                    if (age >= _fallStartTime)
                    {
                        _phase = RockPhase.Falling;
                        if (_rockVfx != null)
                        {
                            _rockVfx.SetActive(true);
                            var position = _rockRestLocalPosition;
                            position.y += GetFallHeightAt(age - _fallStartTime);
                            _rockVfx.transform.localPosition = position;
                        }
                    }
                    break;

                case RockPhase.Falling:
                    if (_rockVfx != null)
                    {
                        var position = _rockRestLocalPosition;
                        position.y += GetFallHeightAt(age - _fallStartTime);
                        _rockVfx.transform.localPosition = position;
                    }

                    if (age >= _fallStartTime + _fallDuration)
                    {
                        EnterLanded();
                    }
                    break;

                case RockPhase.Landed:
                    if (age >= _fallStartTime + _fallDuration + _lingerDuration)
                    {
                        EnterBroken();
                    }
                    break;
            }

            // 撞石碎裂：遁地出土撞到本石 → 权威端在落石事实置位本石碎裂位 → 在场表现体提前碎裂
            // （掩码按 Seq 归属本轮；新一轮落石开始后旧石只按自然寿命自毁）
            if (_phase is RockPhase.Warning or RockPhase.Falling or RockPhase.Landed
                && _model != null
                && _model.RockfallState.Seq == _spawnSeq
                && (_model.RockfallState.BrokenMask & (1 << _rockIndex)) != 0)
            {
                EnterBroken();
            }
        }

        private void EnterLanded()
        {
            _phase = RockPhase.Landed;

            if (_rockVfx != null)
            {
                _rockVfx.transform.localPosition = _rockRestLocalPosition;
            }
            if (_warningVfx != null) _warningVfx.SetActive(false);
            if (_landVfx != null) PlayVfx(_landVfx);
            PlaySound(_landSound);
        }

        private void EnterBroken()
        {
            _phase = RockPhase.Broken;

            if (_rockVfx != null) _rockVfx.SetActive(false);
            if (_warningVfx != null) _warningVfx.SetActive(false);
            if (_landVfx != null) _landVfx.SetActive(false);
            if (_breakVfx != null) PlayVfx(_breakVfx);
            PlaySound(_breakSound);

            Destroy(gameObject, Mathf.Max(0.1f, _breakLifetime));
        }

        /// <summary>
        /// 下落开始 fallAge 秒后石体距落点的高度（MonsterRockfallTiming 共享运动学）。
        /// </summary>
        private float GetFallHeightAt(float fallAge)
        {
            return MonsterRockfallTiming.GetFallHeightAt(
                fallAge,
                _fallInitialSpeed,
                _fallGravity,
                _fallMaxSpeed,
                _fallHeight
            );
        }

        private static void PlayVfx(GameObject vfx)
        {
            vfx.SetActive(true);
            var particles = vfx.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                particles[i].Play(true);
            }
        }

        private void PlaySound(string soundPath)
        {
            if (string.IsNullOrEmpty(soundPath)) return;
            AudioMgr.Play(soundPath, transform);
        }
    }
}
