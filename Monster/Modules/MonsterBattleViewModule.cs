using System;
using Cysharp.Threading.Tasks;
using Framework;
using Framework.Core;
using Game.Components;
using Game.DTOs;
using UnityEngine;

namespace Game.Entities
{
    [Serializable]
    public class MonsterBattleViewModuleConfig
    {
        [Header("References")]
        [Tooltip("渲染组件（身体显隐）")]
        public MonsterRendering Rendering;

        [Tooltip("FinalIK 受击反应组件")]
        public MonsterHitReaction HitReaction;

        [Tooltip("眩晕音效事件")]
        public string DizzySound = "event:/game/creature/boss2_skill5_dizzy";

        [Header("Death Explosion")]
        [Tooltip("死亡爆炸特效 prefab（挂 MonsterDeathEffect）")]
        public GameObject DeathEffectPrefab;

        [Tooltip("死亡爆炸特效归还池延迟（秒）")]
        public float DeathEffectReleaseDelay = 3f;
    }

    /// <summary>
    /// 怪物战斗表现模块：监听受击 / 眩晕 / 破防 / 死亡事实，刷新战斗表现。
    /// 各端本地播放（受 [Networked] 计时器事实驱动，消灭旧双 RPC）；
    /// 死亡动画到期时各端本地播放爆炸，并全端本地广播 Monster_Died（端内事件，供世界层 MonsterSpawnerSystem 的本端实例监听回收）。
    /// </summary>
    public class MonsterBattleViewModule : ModuleBase
    {
        private readonly MonsterModel _model;
        private readonly MonsterBattleViewModuleConfig _config;

        private GameObjectPool<MonsterDeathEffect> _deathEffectPool;

        private int _dizzyAudioHandle = -1;

        /// <summary> 死亡动画进行中标记（区分状态投影与真实下降沿，防出生重投影误触爆炸）</summary>
        private bool _diedAnimRunning;

        #region Lifecycle

        public MonsterBattleViewModule(MonsterModel model, MonsterBattleViewModuleConfig config)
        {
            _model = model;
            _config = config;

            // 注册 Model / Components 的事件监听
            RegisterModelListeners();
            RegisterComponentListeners();
        }

        protected override void OnDispose()
        {
            // 清空 Model / Components 的事件监听
            ClearModelListeners();
            ClearComponentListeners();

            _deathEffectPool?.Clear();
            _deathEffectPool = null;

            HideDizzyVfxInternal();
        }

        protected override void OnStateAuthorityChanged() { }

        #endregion

        #region Registers

        private void RegisterModelListeners()
        {
            if (_model is null) return;

            _model.OnHitAnimTimerChanged += OnHitAnimTimerChangedHandler;
            _model.OnStunnedAnimTimerChanged += OnStunnedAnimTimerChangedHandler;
            _model.OnGuardBrokenAnimTimerChanged += OnGuardBrokenAnimTimerChangedHandler;
            _model.OnDiedAnimTimerChanged += OnDiedAnimTimerChangedHandler;
            _model.OnHitReceivedPushed += OnHitReceivedPushedHandler;
        }

        private void ClearModelListeners()
        {
            if (_model is null) return;

            _model.OnHitAnimTimerChanged -= OnHitAnimTimerChangedHandler;
            _model.OnStunnedAnimTimerChanged -= OnStunnedAnimTimerChangedHandler;
            _model.OnGuardBrokenAnimTimerChanged -= OnGuardBrokenAnimTimerChangedHandler;
            _model.OnDiedAnimTimerChanged -= OnDiedAnimTimerChangedHandler;
            _model.OnHitReceivedPushed -= OnHitReceivedPushedHandler;
        }

        private void RegisterComponentListeners()
        {
            if (_config is null) return;
        }

        private void ClearComponentListeners()
        {
            if (_config is null) return;
        }

        #endregion

        #region Model Handlers

        private void OnHitAnimTimerChangedHandler(bool running)
        {
            if (!running) return;
            if (_config is null) return;

            // 受击音效：权威端本地播（随机命中资源由权威端选择）
            if (!HasStateAuthority) return;
            if (string.IsNullOrEmpty(_model.LastHitSound)) return;

            var anchor = _config.Rendering != null ? _config.Rendering.transform : null;
            AudioMgr.Play(_model.LastHitSound, anchor);
        }

        private void OnStunnedAnimTimerChangedHandler(bool running)
        {
            if (_config is null) return;

            if (running)
            {
                // 全端表现：眩晕特效 + 眩晕音
                if (_config.Rendering != null) _config.Rendering.ShowDizzyVfx();

                if (!string.IsNullOrEmpty(_config.DizzySound) && _dizzyAudioHandle < 0)
                {
                    var anchor = _config.Rendering != null ? _config.Rendering.transform : null;
                    _dizzyAudioHandle = AudioMgr.Play(_config.DizzySound, anchor);
                }
            }
            else
            {
                HideDizzyVfxInternal();
            }
        }

        private void OnGuardBrokenAnimTimerChangedHandler(bool running)
        {
            if (!running) return;
            if (_config is null) return;

            // 全端表现：破防音效（配置派生，各端可自行读取）
            if (string.IsNullOrEmpty(_model.BreakSound)) return;

            var anchor = _config.Rendering != null ? _config.Rendering.transform : null;
            AudioMgr.Play(_model.BreakSound, anchor);
        }

        private void OnDiedAnimTimerChangedHandler(bool running)
        {
            if (_config is null) return;

            if (running)
            {
                _diedAnimRunning = true;

                // 全端表现：死亡音效（配置派生）
                if (!string.IsNullOrEmpty(_model.DeadSound))
                {
                    var anchor = _config.Rendering != null ? _config.Rendering.transform : null;
                    AudioMgr.Play(_model.DeadSound, anchor);
                }

                return;
            }

            // 仅真实下降沿触发爆炸：出生 / 权威易主重投影的 false（计时器从未运行）不触发
            if (!_diedAnimRunning) return;
            _diedAnimRunning = false;

            // 死亡动画到期（TickTimer 同步，各端同时到期；代理端经同步清除感知）：
            // 各端本地播放爆炸；全端本地广播 Monster_Died（各端 Spawner 实例各自收到本端广播，MC 实例执行回收与碎骸掉落）
            PlayDeathExplosion();

            // 死亡位置随载荷（怪物即将被回收销毁，接收方无法事后查询；§1.5 载荷最小化——位置是接收方无法自派生的数据）
            var deathTransform = _config.Rendering != null ? _config.Rendering.transform : null;
            Msger.Send(
                MsgID.MonsterDied,
                _model.Id,
                _model.Template.CfgId,
                deathTransform != null ? deathTransform.position : Vector3.zero,
                deathTransform != null ? deathTransform.rotation : Quaternion.identity);
        }

        private void OnHitReceivedPushedHandler(HitData hitData)
        {
            if (_config is null) return;
            if (!HasStateAuthority) return;

            // 权威端本地表现：受击 IK
            if (_config.HitReaction == null) return;

            _config.HitReaction.Hit(hitData.HitDirection * hitData.Force, hitData.HitPoint);
        }

        #endregion

        #region Private Methods

        private void HideDizzyVfxInternal()
        {
            if (_config != null && _config.Rendering != null)
            {
                _config.Rendering.HideDizzyVfx();
            }

            if (_dizzyAudioHandle >= 0)
            {
                if (AudioMgr.IsPlaying(_dizzyAudioHandle))
                {
                    AudioMgr.Stop(_dizzyAudioHandle, AudioStopMode.Immediate);
                }
                _dizzyAudioHandle = -1;
            }
        }

        /// <summary>
        /// 各端本地播放死亡爆炸：
        /// 隐藏身体网格 → 从池取特效播放 → 延迟归还。
        /// 仅权威端触发物理爆炸（Rayfire），避免多端重复模拟。
        /// </summary>
        private void PlayDeathExplosion()
        {
            if (_config.DeathEffectPrefab == null) return;

            // 隐藏身体（爆炸遮丑）
            if (_config.Rendering != null) _config.Rendering.SetBodyActive(false);

            if (_deathEffectPool == null)
            {
                _deathEffectPool = new GameObjectPool<MonsterDeathEffect>(
                    prefab: _config.DeathEffectPrefab,
                    preload: 0,
                    initialCapacity: 2
                );
            }

            var position = _config.Rendering != null ? _config.Rendering.transform.position : Vector3.zero;
            var effect = _deathEffectPool.Get(position);
            effect.Play(HasStateAuthority);

            ReleaseDeathEffectAsync(effect).Forget();
        }

        /// <summary>
        /// 延迟归还死亡爆炸特效到池（异步等待期间实体可能已销毁，对池与实例判空后安全跳过）
        /// </summary>
        private async UniTaskVoid ReleaseDeathEffectAsync(MonsterDeathEffect effect)
        {
            await UniTask.Delay(TimeSpan.FromSeconds(Mathf.Max(0.1f, _config.DeathEffectReleaseDelay)));

            if (effect == null) return;
            if (_deathEffectPool == null) return;
            if (!effect.gameObject.activeSelf) return;

            _deathEffectPool.Release(effect);
        }

        #endregion
    }
}
